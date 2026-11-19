using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace BtAudioGuard;

/// <summary>
/// LE Audio announcement type, carried in the service data of the Common Audio Service (0x1853)
/// and the Audio Stream Control Service (0x184E). General: "available". Targeted: "I want my
/// paired central to connect now". Observed on this headset: General while it lies on the
/// table, Targeted right after it is switched on.
/// </summary>
public enum LeAnnouncement
{
    None,
    General,
    Targeted,
}

/// <summary>One advertisement that looks like it comes from the headphones.</summary>
public sealed record AdvertSeen(
    ulong Address,
    string AddressType,
    bool AddressMatch,
    bool NameMatch,
    bool BoseData,
    string Name,
    short Rssi,
    string Kind,
    bool Connectable,
    bool ScanResponse,
    string Services,
    string Payload,
    LeAnnouncement Announcement)
{
    /// <summary>Strong enough to act on: our headphones' own address, or their name.</summary>
    public bool Strong => AddressMatch || NameMatch;

    public string Describe() =>
        $"{Address:X12} ({AddressType}{(AddressMatch ? ", identity match" : "")}) " +
        $"name '{Name}'{(NameMatch ? " (match)" : "")}, rssi {Rssi} dBm, {Kind}" +
        $"{(Connectable ? ", connectable" : "")}{(ScanResponse ? ", scan response" : "")}" +
        $"{(BoseData ? ", Bose data" : "")}{(Services.Length > 0 ? $", services {Services}" : "")}" +
        $"{(Announcement != LeAnnouncement.None ? $", {Announcement.ToString().ToLowerInvariant()} announcement" : "")}";

    /// <summary>The raw advertisement content: one "type:hex" entry per AD structure.
    /// FF = manufacturer data (first two bytes 9E00 = Bose), 16 = service data, 09 = name.</summary>
    public string DescribePayload() => $"{Kind}{(ScanResponse ? " (scan response)" : "")}: {Payload}";
}

/// <summary>
/// Listens for Bluetooth LE advertisements while the headphones have no link at all. Covers the
/// case where the headphones are switched on but reconnect nothing (not even Classic), so no
/// connect event ever arrives. Runs only while nothing is connected: scanning shares radio time
/// with the LE Audio stream and could cause dropouts during playback.
/// </summary>
internal sealed class AdvertScanner : IDisposable
{
    private const ushort BoseCompanyId = 0x009E;

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly Func<string> _nameMatch;
    private readonly Func<ulong?> _identityAddress;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>When the current scan run started; null while not scanning.</summary>
    public DateTime? RunningSinceUtc { get; private set; }

    public AdvertScanner(Func<string> nameMatch, Func<ulong?> identityAddress)
    {
        _nameMatch = nameMatch;
        _identityAddress = identityAddress;
        // Active scanning also gets scan responses, which is where many devices put their name.
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        try
        {
            // LE Audio devices often advertise with extended advertising.
            _watcher.AllowExtendedAdvertisements = true;
        }
        catch
        {
            // Older stacks: legacy advertisements only.
        }
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    /// <summary>Raised (on a background thread) for each advertisement that may be the headphones.</summary>
    public event Action<AdvertSeen>? Seen;

    /// <summary>
    /// Starts or stops the scan to match <paramref name="shouldRun"/>. The condition is evaluated
    /// under the lock (and again after every stop), so concurrent callers cannot apply a stale
    /// decision and a request made while the watcher was stopping is not lost.
    /// </summary>
    public void Update(Func<bool> shouldRun)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _shouldRun = shouldRun;
            ApplyLocked();
        }
    }

    private Func<bool> _shouldRun = () => false;

    // Our own view of "started": right after Start() the watcher's Status can still read Created,
    // and a second caller would start (and log) again.
    private bool _started;

    // Radio off, busy or temporarily unavailable: retried later. Only the first failure of a
    // streak is logged, so a radio that stays off does not flood the log.
    private int _retryStreak;

    private void ApplyLocked()
    {
        bool run = _shouldRun();
        var status = _watcher.Status;
        try
        {
            if (run && !_started && status is BluetoothLEAdvertisementWatcherStatus.Created
                    or BluetoothLEAdvertisementWatcherStatus.Stopped
                    or BluetoothLEAdvertisementWatcherStatus.Aborted)
            {
                _watcher.Start();
                _started = true;
                RunningSinceUtc = DateTime.UtcNow;
                if (_retryStreak == 0)
                    Log.Info($"Advertisement scan started (nothing connected){(_watcher.AllowExtendedAdvertisements ? "" : ", legacy advertising only")}");
            }
            else if (!run && _started && status != BluetoothLEAdvertisementWatcherStatus.Stopping)
            {
                _started = false;
                RunningSinceUtc = null;
                _watcher.Stop();
                Log.Info("Advertisement scan stopped");
            }
            // Stopping: OnStopped applies the current decision once the stop completes.
        }
        catch (Exception ex)
        {
            if (_retryStreak == 0)
                Log.Info($"Advertisement scan {(run ? "start" : "stop")} failed: 0x{ex.HResult:X8} {ex.Message}".TrimEnd());
            if (run)
            {
                _started = false;
                ScheduleRetryLocked();
            }
            else
            {
                // A failed stop may leave the scan running next to a connected headset: try again.
                _started = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    Update(_shouldRun);
                });
            }
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (_gate)
        {
            _started = false;
            RunningSinceUtc = null;
            if (_disposed)
                return;

            if (args.Error == Windows.Devices.Bluetooth.BluetoothError.Success)
            {
                // Our own stop. Re-apply in case a start was requested while it was stopping.
                _ = Task.Run(() => Update(_shouldRun));
                return;
            }

            if (_watcher.AllowExtendedAdvertisements)
            {
                // Adapters without extended advertising support abort the scan (Start itself does
                // not throw). Fall back to legacy advertisements and try again right away.
                _watcher.AllowExtendedAdvertisements = false;
                Log.Info($"Advertisement scan aborted by Windows ({args.Error}) with extended advertising on: retrying with legacy only");
                _ = Task.Run(() => Update(_shouldRun));
                return;
            }

            if (_retryStreak == 0)
                Log.Info($"Advertisement scan stopped by Windows: {args.Error}");
            ScheduleRetryLocked();
        }
    }

    private void ScheduleRetryLocked()
    {
        int streak = ++_retryStreak;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(streak < 5 ? 30 : 300));
            Func<bool> shouldRun;
            lock (_gate)
            {
                if (_disposed)
                    return;
                shouldRun = _shouldRun;
            }
            Update(shouldRun);
        });
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (Volatile.Read(ref _retryStreak) != 0)
        {
            lock (_gate)
            {
                if (_retryStreak != 0)
                    Log.Info("Advertisement scan running again");
                _retryStreak = 0;
            }
        }

        try
        {
            var ad = args.Advertisement;
            string match = _nameMatch();
            string name = ad.LocalName ?? "";
            bool addressMatch = _identityAddress() == args.BluetoothAddress;
            bool nameMatch = name.Length > 0 && name.Contains(match, StringComparison.OrdinalIgnoreCase);
            bool bose = ad.ManufacturerData.Any(m => m.CompanyId == BoseCompanyId);
            if (!addressMatch && !nameMatch && !bose)
                return;

            string services = string.Join(",", ad.ServiceUuids.Select(ShortUuid));
            Seen?.Invoke(new AdvertSeen(
                args.BluetoothAddress,
                args.BluetoothAddressType.ToString(),
                addressMatch,
                nameMatch,
                bose,
                name,
                args.RawSignalStrengthInDBm,
                args.AdvertisementType.ToString(),
                args.IsConnectable,
                args.IsScanResponse,
                services,
                Payload(ad),
                Announcement(ad)));
        }
        catch (Exception ex)
        {
            Log.Info($"Advertisement handling failed: {ex.Message}");
        }
    }

    private static string Payload(BluetoothLEAdvertisement ad)
    {
        // Flags (01) carry nothing useful and are left out.
        return string.Join(" ", ad.DataSections
            .Where(d => d.DataType != 0x01)
            .Select(d => $"{d.DataType:X2}:{Hex(d.Data)}"));
    }

    private static LeAnnouncement Announcement(BluetoothLEAdvertisement ad)
    {
        // Service data (0x16): 16-bit UUID little endian, then the announcement type byte.
        var result = LeAnnouncement.None;
        foreach (var section in ad.DataSections.Where(d => d.DataType == 0x16))
        {
            var b = Bytes(section.Data);
            if (b.Length < 3)
                continue;
            int uuid = b[0] | (b[1] << 8);
            if (uuid is not (0x1853 or 0x184E))
                continue;
            if (b[2] == 0x01)
                return LeAnnouncement.Targeted;
            if (b[2] == 0x00)
                result = LeAnnouncement.General;
        }
        return result;
    }

    private static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
            reader.ReadBytes(bytes);
        return bytes;
    }

    private static string Hex(IBuffer buffer) => Convert.ToHexString(Bytes(buffer));

    // 0000184e-0000-1000-8000-00805f9b34fb -> 184E; anything else stays a full GUID.
    private static string ShortUuid(Guid uuid)
    {
        string s = uuid.ToString();
        return s.StartsWith("0000") && s.EndsWith("-0000-1000-8000-00805f9b34fb")
            ? s.Substring(4, 4).ToUpperInvariant()
            : s;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher.Received -= OnReceived;
            _watcher.Stopped -= OnStopped;
            try
            {
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    _watcher.Stop();
            }
            catch
            {
                // Shutting down.
            }
        }
    }
}
