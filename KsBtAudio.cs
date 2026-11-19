using System.Runtime.InteropServices;

namespace BtAudioGuard;

/// <summary>
/// Asks the Bluetooth audio driver to disconnect and reconnect a device through its
/// private KS property set (KSPROPSETID_BtAudio), the same mechanism tools like ToothTray use.
/// This equals pressing "Disconnect" then "Connect" in Windows settings, without touching the adapter.
/// Known to work with the Classic A2DP/HFP driver. Whether the LE Audio driver (APXENUM proxy)
/// supports it is unconfirmed: a "KsProperty 0x..." result means it does not. Failures are
/// returned as text, never thrown.
/// </summary>
internal static class KsBtAudio
{
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KsPropertyOneshotReconnect = 0;
    private const uint KsPropertyOneshotDisconnect = 1;
    private const uint KsPropertyTypeGet = 0x00000001;
    private const int ClsctxAll = 0x17;

    public static (bool Ok, string Detail) Cycle(string endpointId)
    {
        var disconnect = Send(endpointId, KsPropertyOneshotDisconnect);
        if (!disconnect.Ok)
            return (false, $"disconnect failed: {disconnect.Detail}");

        Thread.Sleep(1500);

        var reconnect = Send(endpointId, KsPropertyOneshotReconnect);
        return (reconnect.Ok, $"disconnect OK, reconnect: {reconnect.Detail}");
    }

    private static (bool Ok, string Detail) Send(string endpointId, uint propertyId)
    {
        string stage = "create enumerator";
        object? topologyObject = null;
        try
        {
            // Not "new MMDeviceEnumeratorComObject()": the enumerator is a per-process singleton, and
            // once NAudio has wrapped it the runtime hands back NAudio's wrapper type, so that
            // expression throws InvalidCastException. Creating by CLSID returns the shared wrapper,
            // which casts to our interface through QueryInterface.
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true)!)!;
            stage = "get endpoint";

            int hr = enumerator.GetDevice(endpointId, out var endpoint);
            if (hr != 0)
                return (false, $"GetDevice 0x{hr:X8}");

            stage = "activate topology";
            var topologyIid = typeof(IDeviceTopology).GUID;
            hr = endpoint.Activate(ref topologyIid, ClsctxAll, IntPtr.Zero, out topologyObject);
            if (hr != 0)
                return (false, $"Activate IDeviceTopology 0x{hr:X8}");

            stage = "cast IDeviceTopology";
            var topology = (IDeviceTopology)topologyObject;
            hr = topology.GetConnectorCount(out uint connectorCount);
            if (hr != 0)
                return (false, $"GetConnectorCount 0x{hr:X8}");

            var errors = new List<string>();
            for (uint i = 0; i < connectorCount; i++)
            {
                stage = $"connector {i}";
                IConnector? connector = null;
                IMMDevice? filterDevice = null;
                object? ksObject = null;
                try
                {
                    if (topology.GetConnector(i, out connector) != 0)
                        continue;

                    // The endpoint connector links to the KS filter of the Bluetooth audio driver.
                    hr = connector.GetDeviceIdConnectedTo(out string filterId);
                    if (hr != 0)
                    {
                        errors.Add($"connector {i}: GetDeviceIdConnectedTo 0x{hr:X8}");
                        continue;
                    }

                    hr = enumerator.GetDevice(filterId, out filterDevice);
                    if (hr != 0)
                    {
                        errors.Add($"filter GetDevice 0x{hr:X8}");
                        continue;
                    }

                    stage = $"filter {filterId}";
                    var ksControlIid = typeof(IKsControl).GUID;
                    hr = filterDevice.Activate(ref ksControlIid, ClsctxAll, IntPtr.Zero, out ksObject);
                    if (hr != 0)
                    {
                        errors.Add($"Activate IKsControl 0x{hr:X8}");
                        continue;
                    }

                    var property = new KsProperty
                    {
                        Set = KsPropSetIdBtAudio,
                        Id = propertyId,
                        Flags = KsPropertyTypeGet,
                    };
                    stage = "cast IKsControl";
                    hr = ((IKsControl)ksObject).KsProperty(
                        ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);

                    if (hr == 0)
                        return (true, "OK");

                    errors.Add($"KsProperty 0x{hr:X8} (driver may not support this, e.g. LE Audio)");
                }
                finally
                {
                    // Release now, not at the next GC: the KS control object holds a handle to the
                    // driver's filter, which would make a driver restart right after this step fail
                    // with "disable vetoed".
                    Release(ksObject);
                    Release(filterDevice);
                    Release(connector);
                }
            }

            return (false, errors.Count > 0 ? string.Join("; ", errors) : "endpoint has no connectors");
        }
        catch (Exception ex)
        {
            return (false, $"at '{stage}': {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Release(topologyObject);
            // Not the enumerator and not the endpoint: the runtime may share those wrappers with
            // NAudio (same COM identity), and releasing them would break NAudio's objects.
        }
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // Already released or not releasable: nothing to do.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    // Only the vtable slots up to the last used method are declared. Order matters.

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector connectTo);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connectedTo);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string connectorId);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(ref KsProperty property, uint propertyLength, IntPtr propertyData,
            uint dataLength, out uint bytesReturned);
    }
}
