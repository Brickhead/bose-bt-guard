using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BtAudioGuard;

/// <summary>Disables and re-enables the Bluetooth adapter (same as Device Manager). Needs admin.</summary>
internal static class AdapterControl
{
    private const string InstanceIdKey = "System.Devices.DeviceInstanceId";

    // Cached so a re-enable still works if the adapter is already disabled.
    private static string? _instanceId;

    public static async Task<string?> GetInstanceIdAsync()
    {
        if (_instanceId is not null)
            return _instanceId;

        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
            return null;

        var info = await DeviceInformation.CreateFromIdAsync(
            adapter.DeviceId, [InstanceIdKey], DeviceInformationKind.DeviceInterface);

        _instanceId = info.Properties.TryGetValue(InstanceIdKey, out var value) ? value as string : null;
        return _instanceId;
    }

    public static async Task<(bool Ok, string Detail)> RestartAsync()
    {
        string? instanceId = await GetInstanceIdAsync();
        if (instanceId is null)
            return (false, "no Bluetooth adapter found");

        var (ok, detail) = await DevNode.RestartAsync(instanceId, TimeSpan.FromSeconds(2));
        return (ok, $"adapter {detail} ({instanceId})");
    }
}
