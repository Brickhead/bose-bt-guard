using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BtAudioGuard;

/// <summary>
/// The Windows LE Audio driver instance for the headphones: a software device
/// "Audio Kernel-Mode Proxy Unit Device" (SWD\APXENUM#...\{...-&lt;bluetooth address&gt;}).
/// It owns the headphones' audio endpoints. Restarting it rebuilds the LE Audio stream setup
/// without touching the Bluetooth link, the Classic connection or the adapter. Needs admin.
/// </summary>
internal static partial class LeAudioNode
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint CmGetIdListFilterEnumerator = 0x00000001;
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [GeneratedRegex("([0-9a-f]{2}:){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressAtEnd();

    /// <summary>"BluetoothLE#BluetoothLEaa:..-68:f2:1f:61:39:7c" -> "68F21F61397C".</summary>
    public static string? AddressFromAepId(string aepId)
    {
        var match = AddressAtEnd().Match(aepId);
        return match.Success ? match.Value.Replace(":", "").ToUpperInvariant() : null;
    }

    public static List<string> FindInstanceIds(string address)
    {
        const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;

        // A device can appear between the size and the list call: retry with a fresh size.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, "SWD", flags) != CrSuccess || length == 0)
                return [];

            var buffer = new char[length];
            int cr = CM_Get_Device_ID_ListW("SWD", buffer, length, flags);
            if (cr == CrBufferSmall)
                continue;
            if (cr != CrSuccess)
                return [];

            return new string(buffer)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => id.StartsWith(@"SWD\APXENUM", StringComparison.OrdinalIgnoreCase) &&
                             id.Contains(address, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return [];
    }

    /// <summary>Short state of the node(s), e.g. "started" or "problem 22", for the log.</summary>
    public static string Describe(string address)
    {
        try
        {
            var ids = FindInstanceIds(address);
            if (ids.Count == 0)
                return "LE Audio driver node not present";

            return string.Join(", ", ids.Select(id =>
            {
                if (CM_Locate_DevNodeW(out uint devInst, id, 0) != CrSuccess)
                    return "LE Audio driver node: locate failed";
                if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != CrSuccess)
                    return "LE Audio driver node: status failed";
                if ((status & DnHasProblem) != 0)
                    return $"LE Audio driver node: problem {problem}";
                return (status & DnStarted) != 0 ? "LE Audio driver node: started" : "LE Audio driver node: not started";
            }));
        }
        catch (Exception ex)
        {
            return $"LE Audio driver node: {ex.Message}";
        }
    }

    public static async Task<(bool Ok, string Detail)> RestartAsync(string address)
    {
        var ids = FindInstanceIds(address);
        if (ids.Count == 0)
            return (false, $"no LE Audio driver node for {address}");

        var notes = new List<string>();
        bool anyOk = false;
        foreach (var id in ids)
        {
            var (ok, detail) = await DevNode.RestartAsync(id, TimeSpan.FromMilliseconds(1500));
            anyOk |= ok;
            notes.Add(detail);
        }
        return (anyOk, string.Join("; ", notes));
    }
}
