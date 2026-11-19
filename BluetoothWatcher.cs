using Windows.Devices.Enumeration;

namespace BtAudioGuard;

public sealed record BtDevice(string Id, string Name, string Protocol, bool Connected);

/// <summary>
/// Watches paired Bluetooth association endpoints (both LE and Classic) whose name contains
/// the configured match string, and reports connect / disconnect transitions.
/// Headphones powering on (lifted from the desk) shows up here as a connect.
/// </summary>
public sealed class BluetoothWatcher : IDisposable
{
    private const string LeProtocolId = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    private const string ClassicProtocolId = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";

    private readonly string _match;
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<string, BtDevice> _devices = [];
    private readonly object _gate = new();
    private volatile bool _disposed;
    private int _enumerated;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BluetoothWatcher(string match) => _match = match;

    /// <summary>Raised on a real connect/disconnect transition (not on initial enumeration).</summary>
    public event Action<BtDevice>? ConnectionChanged;

    /// <summary>Raised whenever the device list or any state in it changes.</summary>
    public event Action? ListChanged;

    public bool AnyConnected
    {
        get { lock (_gate) return _devices.Values.Any(d => d.Connected); }
    }

    public BtDevice? LeDevice
    {
        get { lock (_gate) return _devices.Values.FirstOrDefault(d => d.Protocol == "LE"); }
    }

    public BtDevice? ClassicDevice
    {
        get { lock (_gate) return _devices.Values.FirstOrDefault(d => d.Protocol == "Classic"); }
    }

    /// <summary>True once both watchers finished their initial enumeration.</summary>
    public bool Ready => _ready.Task.IsCompleted;

    /// <summary>Waits for the initial enumeration of both watchers (bounded).</summary>
    public async Task WaitReadyAsync(TimeSpan max, CancellationToken token)
    {
        await Task.WhenAny(_ready.Task, Task.Delay(max, token));
        token.ThrowIfCancellationRequested();
        if (!Ready)
            Log.Info($"Bluetooth enumeration not complete after {max.TotalSeconds:0} s, continuing");
    }

    public IReadOnlyList<BtDevice> Snapshot()
    {
        lock (_gate) return _devices.Values.ToList();
    }

    public void Start()
    {
        AddWatcher(LeProtocolId, "LE");
        AddWatcher(ClassicProtocolId, "Classic");
    }

    private void AddWatcher(string protocolId, string protocol)
    {
        string aqs = $"System.Devices.Aep.ProtocolId:=\"{protocolId}\" AND " +
                     "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

        var watcher = DeviceInformation.CreateWatcher(
            aqs, [IsConnectedKey], DeviceInformationKind.AssociationEndpoint);

        watcher.Added += (_, info) => OnAdded(info, protocol);
        watcher.Updated += (_, update) => OnUpdated(update);
        watcher.Removed += (_, update) => OnRemoved(update);
        watcher.EnumerationCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref _enumerated) == 2)
                _ready.TrySetResult();
        };
        watcher.Stopped += (sender, _) =>
        {
            Log.Info($"Bluetooth watcher ({protocol}) stopped, status {sender.Status}");
            // An aborted watcher reports nothing anymore: restart it, or connects go unnoticed.
            if (!_disposed && sender.Status == DeviceWatcherStatus.Aborted)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    if (_disposed)
                        return;
                    try
                    {
                        sender.Start();
                        Log.Info($"Bluetooth watcher ({protocol}) restarted after abort");
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Bluetooth watcher ({protocol}) restart failed: {ex.Message}");
                    }
                });
            }
        };

        watcher.Start();
        _watchers.Add(watcher);
    }

    private static bool? ReadConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(IsConnectedKey, out var value) && value is bool connected ? connected : null;

    private void OnAdded(DeviceInformation info, string protocol)
    {
        if (!info.Name.Contains(_match, StringComparison.OrdinalIgnoreCase))
            return;

        var device = new BtDevice(info.Id, info.Name, protocol, ReadConnected(info.Properties) ?? false);
        BtDevice? old;
        lock (_gate)
        {
            _devices.TryGetValue(info.Id, out old);
            _devices[info.Id] = device;
        }

        Log.Info($"Bluetooth device found: {device.Name} [{protocol}], connected={device.Connected}");
        ListChanged?.Invoke();

        // After the initial enumeration (a watcher restarted after an abort, or a device paired
        // later), an add that changes the connection state is a real transition.
        if (Ready && (old?.Connected ?? false) != device.Connected)
            ConnectionChanged?.Invoke(device);
    }

    private void OnRemoved(DeviceInformationUpdate update)
    {
        BtDevice? removed;
        lock (_gate)
        {
            if (!_devices.Remove(update.Id, out removed))
                return;
        }

        Log.Info($"Bluetooth device removed: {removed.Name} [{removed.Protocol}]");
        ListChanged?.Invoke();
        // A connected device that disappears (unpaired, adapter gone) is also a disconnect.
        if (removed.Connected)
            ConnectionChanged?.Invoke(removed with { Connected = false });
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        bool? connected = ReadConnected(update.Properties);
        if (connected is null)
            return;

        BtDevice device;
        lock (_gate)
        {
            if (!_devices.TryGetValue(update.Id, out var old) || old.Connected == connected.Value)
                return;
            device = old with { Connected = connected.Value };
            _devices[update.Id] = device;
        }

        Log.Info($"Bluetooth {device.Protocol} {(device.Connected ? "CONNECTED" : "disconnected")}: {device.Name}");
        ListChanged?.Invoke();
        ConnectionChanged?.Invoke(device);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var watcher in _watchers)
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        _watchers.Clear();
    }
}
