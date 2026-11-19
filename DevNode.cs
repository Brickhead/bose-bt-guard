using System.Runtime.InteropServices;

namespace BtAudioGuard;

/// <summary>
/// Disable + re-enable of a device node through CfgMgr32 (same as Device Manager). Needs admin.
/// A node must never be left disabled: the instance ID is written to disk before the disable and
/// removed after the enable, so if the app dies in between (exit, logoff, crash) the next start
/// re-enables it. A failed enable is retried.
/// </summary>
internal static class DevNode
{
    private const int CrSuccess = 0;
    private const int CrRemoveVetoed = 0x17;
    private const uint CmDisableUiNotOk = 0x00000004;
    private const uint CmLocateDevNodePhantom = 0x00000001;
    private const int EnableAttempts = 3;

    private static readonly string PendingFile = Path.Combine(Settings.Dir, "pending-enable.txt");
    private static readonly object Gate = new();
    private static int _inFlight;
    private static int _recovering;

    /// <summary>Number of restarts between disable and enable right now. Exit waits for zero.</summary>
    public static int InFlight => Volatile.Read(ref _inFlight);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceInstanceId, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Disable_DevNode(uint devInst, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    public static async Task<(bool Ok, string Detail)> RestartAsync(string instanceId, TimeSpan offTime)
    {
        int cr = CM_Locate_DevNodeW(out uint devInst, instanceId, 0);
        if (cr != CrSuccess)
            return (false, $"locate CR={cr}");

        Interlocked.Increment(ref _inFlight);
        try
        {
            AddPending(instanceId);
            cr = CM_Disable_DevNode(devInst, CmDisableUiNotOk);
            if (cr != CrSuccess)
            {
                RemovePending(instanceId);
                return (false, cr == CrRemoveVetoed
                    ? "disable vetoed (something holds the device open)"
                    : $"disable CR={cr}");
            }

            await Task.Delay(offTime);

            for (int attempt = 1; ; attempt++)
            {
                cr = Enable(instanceId, devInst);
                if (cr == CrSuccess)
                {
                    RemovePending(instanceId);
                    return (true, attempt == 1 ? "restarted" : $"restarted (enable needed {attempt} attempts)");
                }
                if (attempt == EnableAttempts)
                {
                    return (false, $"ENABLE FAILED CR={cr}: device left disabled. The app retries on the next " +
                                   $"Bluetooth connect and at its next start, or enable it in Device Manager: {instanceId}");
                }
                await Task.Delay(1000);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    // Re-locate before each try: if the device went away meanwhile (headphones switched off while
    // their driver node was disabled), the old handle is stale and a normal locate fails. The
    // phantom flag finds the non-present node so its disabled flag can still be cleared.
    private static int Enable(string instanceId, uint fallback)
    {
        uint devInst = fallback;
        if (CM_Locate_DevNodeW(out uint located, instanceId, 0) == CrSuccess ||
            CM_Locate_DevNodeW(out located, instanceId, CmLocateDevNodePhantom) == CrSuccess)
        {
            devInst = located;
        }
        return CM_Enable_DevNode(devInst, 0);
    }

    /// <summary>
    /// Re-enables nodes that were disabled but never re-enabled (app died mid-restart, or all
    /// enable attempts failed). Called at startup and on Bluetooth connect events. Skips nodes a
    /// restart is handling right now.
    /// </summary>
    public static void RecoverPending()
    {
        if (!File.Exists(PendingFile) || InFlight > 0)
            return;
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
            return; // another call is already at it

        try
        {
            List<string> ids;
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(PendingFile))
                        return;
                    ids = File.ReadAllLines(PendingFile).Where(l => l.Length > 0).Distinct().ToList();
                }
                catch (Exception ex)
                {
                    Log.Info($"Pending re-enable list unreadable: {ex.Message}");
                    return;
                }
            }

            foreach (var id in ids)
            {
                int cr = CM_Locate_DevNodeW(out uint devInst, id, 0);
                if (cr != CrSuccess)
                    cr = CM_Locate_DevNodeW(out devInst, id, CmLocateDevNodePhantom);
                if (cr == CrSuccess)
                    cr = CM_Enable_DevNode(devInst, 0);

                if (cr == CrSuccess)
                {
                    RemovePending(id);
                    Log.Info($"Re-enabled device left disabled earlier: {id}");
                }
                else
                {
                    Log.Info($"Could not re-enable device left disabled earlier (CR={cr}). Enable it in Device Manager: {id}");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    private static void AddPending(string instanceId)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllLines(PendingFile, [instanceId]);
            }
            catch
            {
                // Best effort: the restart itself still works without the safety net.
            }
        }
    }

    private static void RemovePending(string instanceId)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PendingFile))
                    return;
                var rest = File.ReadAllLines(PendingFile).Where(l => l.Length > 0 && l != instanceId).ToList();
                if (rest.Count == 0)
                    File.Delete(PendingFile);
                else
                    File.WriteAllLines(PendingFile, rest);
            }
            catch
            {
                // Worst case the next start tries to enable an already enabled node, which is harmless.
            }
        }
    }
}
