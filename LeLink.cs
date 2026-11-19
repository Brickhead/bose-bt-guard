using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BtAudioGuard;

/// <summary>
/// Brings up the Bluetooth LE link to the headphones and holds it open until Windows has rebuilt
/// the LE Audio render endpoint.
///
/// On this headset the LE link does not reconnect on its own after the headphones power back on:
/// only the Classic ACL link comes back. Forcing an uncached GATT query brings the LE link up, but
/// the moment the app releases its GATT objects Windows tears the link down again within a few
/// seconds — before the audio endpoint is rebuilt. So we hold a GattSession with
/// MaintainConnection = true until the audio endpoint reports Active (checked via the supplied
/// predicate), then release it. The hold is time-boxed, and released as soon as the endpoint is
/// up, so the headphones can still power off when taken off the head. The caller's abort check
/// ends the hold early when the headphones start disconnecting.
/// </summary>
internal static class LeLink
{
    public static async Task<(bool Ok, string Detail)> WakeAsync(
        string leDeviceId, Func<bool> endpointActive, int holdSeconds, Func<bool> abort)
    {
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        try
        {
            device = await BluetoothLEDevice.FromIdAsync(leDeviceId);
            if (device is null)
                return (false, "BluetoothLEDevice.FromIdAsync returned null");

            // Hold the link open for the whole wait so Windows keeps the LE connection long enough
            // to rebuild the audio endpoint, instead of dropping it the instant we let go.
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = true;

            // Uncached discovery forces the actual connection attempt. The hold limit counts from
            // here, so a slow discovery does not extend how long the headphones are held.
            var stopwatch = Stopwatch.StartNew();
            var gatt = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            foreach (var service in gatt.Services)
                service.Dispose();

            var limit = TimeSpan.FromSeconds(holdSeconds);
            bool linkUp = false;
            while (stopwatch.Elapsed < limit)
            {
                // The headphones are powering off: stop holding them on.
                if (abort())
                    return (false, $"aborted after {stopwatch.ElapsedMilliseconds} ms: headphones are disconnecting");

                linkUp = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                if (linkUp && endpointActive())
                    return (true, $"endpoint active after {stopwatch.ElapsedMilliseconds} ms (held LE link open)");
                await Task.Delay(250);
            }

            return (false, linkUp
                ? $"LE link up but endpoint still not active after {holdSeconds} s"
                : $"LE link did not come up within {holdSeconds} s");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Release the link so the headphones can power off normally.
            session?.Dispose();
            device?.Dispose();
        }
    }
}
