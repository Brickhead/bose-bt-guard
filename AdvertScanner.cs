using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace BtAudioGuard;

/// <summary>
/// LE Audio announcement type, carried in the service data of the Common Audio Service (0x1853)
/// and the Audio Stream Control Service (0x184E). General: "available". Targeted: "I want my
/// paired central to connect now". Observed on this headset: General for 10-20 min while it lies
/// on the table, Targeted for a few seconds when it is picked up after sleeping.
/// </summary>
public enum LeAnnouncement
{
    None,
    General,
    Targeted,
}

/// <summary>One advertisement from the headphones (matched by their address or name).</summary>
public sealed record AdvertSeen(
    ulong Address,
    bool AddressMatch,
    string Name,
    short Rssi,
    bool Connectable,
    LeAnnouncement Announcement,
    string Payload)
{
    public string Describe() =>
        $"{Address:X12} ({(AddressMatch ? "address match" : $"name '{Name}'")}), rssi {Rssi} dBm" +
        $"{(Connectable ? ", connectable" : "")}" +
        $"{(Announcement != LeAnnouncement.None ? $", {Announcement.ToString().ToLowerInvariant()} announcement" : "")}";
}

/// <summary>
/// Listens for Bluetooth LE advertisements while the headphones have no link at all. Covers the
/// case where the headphones are switched on but reconnect nothing (not even Classic), so no
/// connect event ever arrives. Runs only while nothing is connected: scanning shares radio time
/// with the LE Audio stream and could cause dropouts during playback.
/// </summary>
internal sealed class AdvertScanner : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly Func<string> _nameMatch;
    private readonly Func<ulong?> _identityAddress;
    private readonly object _gate = new();
    private bool _disposed;

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

    /// <summary>Raised (on a background thread) for each advertisement from the headphones.</summary>
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
                if (_retryStreak == 0)
                    Log.Info($"Advertisement scan started (nothing connected){(_watcher.AllowExtendedAdvertisements ? "" : ", legacy advertising only")}");
            }
            else if (!run && _started && status != BluetoothLEAdvertisementWatcherStatus.Stopping)
            {
                _started = false;
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
                Log.Info($"Advertisement scan aborted by Windows ({args.Error}) with extended advertising on: retrying with legacy only. " +
                         "Legacy advertisements carry no LE Audio announcement, so the wake on a Targeted announcement cannot fire.");
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
            string name = ad.LocalName ?? "";
            ulong? identity = _identityAddress();
            bool addressMatch = identity == args.BluetoothAddress;
            // The headset advertises its own (identity) address, so once it is paired the address
            // decides. The name is only a fallback while the paired entry is not known yet: another
            // nearby headset of the same model must not start or extend episodes.
            bool nameMatch = identity is null && name.Length > 0 &&
                             name.Contains(_nameMatch(), StringComparison.OrdinalIgnoreCase);
            if (!addressMatch && !nameMatch)
                return;

            Seen?.Invoke(new AdvertSeen(
                args.BluetoothAddress,
                addressMatch,
                name,
                args.RawSignalStrengthInDBm,
                args.IsConnectable,
                Announcement(ad),
                Payload(ad)));
        }
        catch (Exception ex)
        {
            Log.Info($"Advertisement handling failed: {ex.Message}");
        }
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

    /// <summary>
    /// Compact raw content for the log: "type:hex" per section, without flags (01), name (09) and
    /// TX power (0A). FF = manufacturer data (9E00 = Bose), 16 = service data.
    /// </summary>
    private static string Payload(BluetoothLEAdvertisement ad) =>
        string.Join(" ", ad.DataSections
            .Where(d => d.DataType is not (0x01 or 0x09 or 0x0A))
            .Select(d => $"{d.DataType:X2}:{Convert.ToHexString(Bytes(d.Data))}"));

    private static byte[] Bytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
            reader.ReadBytes(bytes);
        return bytes;
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
