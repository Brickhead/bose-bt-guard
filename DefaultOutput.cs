using System.Runtime.InteropServices;

namespace BtAudioGuard;

/// <summary>
/// Sets the Windows default audio endpoint through the undocumented IPolicyConfig interface
/// (the one the Sound control panel uses). Stable since Windows 7 and used by SoundSwitch,
/// AudioDeviceCmdlets and similar tools.
/// </summary>
internal static class DefaultOutput
{
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;

    public static (bool Ok, string Detail) Set(string endpointId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (int role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
            {
                int hr = policy.SetDefaultEndpoint(endpointId, role);
                if (hr != 0)
                    return (false, $"SetDefaultEndpoint role {role}: 0x{hr:X8}");
            }
            return (true, "set as default output (all roles)");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    // Vtable order matters. The first ten slots are unused placeholders.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
