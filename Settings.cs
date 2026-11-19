using System.Text.Json;

namespace BtAudioGuard;

/// <summary>What to do when the headphones advertise while nothing is connected.</summary>
public enum AdvertScanModes
{
    Off = 0,
    LogOnly = 1,
    Wake = 2,
}

public sealed class Settings
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BtAudioGuard");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Substring matched against Bluetooth device names and audio endpoint names.</summary>
    public string DeviceNameMatch { get; set; } = "Bose QC Ultra";

    /// <summary>Run the fix ladder automatically when a connect is followed by a dead stream.</summary>
    public bool AutoFix { get; set; } = true;

    /// <summary>Make the headphones the Windows default output on every check.</summary>
    public bool MakeDefaultOutput { get; set; } = true;

    /// <summary>Seconds to wait after the headphones connect before testing the stream.</summary>
    public int SettleSeconds { get; set; } = 4;

    /// <summary>How long the silent test stream may take to start draining before it counts as stalled.</summary>
    public int StreamTestSeconds { get; set; } = 3;

    /// <summary>Minimum time between automatic fix runs. Prevents restart loops.</summary>
    public int FixCooldownSeconds { get; set; } = 90;

    /// <summary>
    /// Max seconds to hold the LE link open (GattSession, MaintainConnection) during a Wake LE
    /// link step, waiting for Windows to rebuild the audio endpoint. Released as soon as the
    /// endpoint is Active, so the headphones can still power off normally.
    /// </summary>
    public int LeWakeHoldSeconds { get; set; } = 20;

    /// <summary>
    /// While the LE link is up and another app is playing to the headphones, re-run the silent
    /// stream test every this many seconds, so a stall during use is caught (and fixed) instead of
    /// only on connect. Each probe briefly opens a silent stream (up to "Stream test" seconds).
    /// 0 = off. Never changes the default output.
    /// </summary>
    public int WatchdogSeconds { get; set; } = 60;

    /// <summary>
    /// LE link down while the headphones stay on: they advertise "connect me", which Windows
    /// ignores. Scan for that while no link is up. LogOnly records what is seen; Wake also runs
    /// "Wake LE link" on a connectable advertisement from the headphones (right after a
    /// disconnect only once they kept advertising for 5 s; at most once a minute).
    /// </summary>
    public AdvertScanModes AdvertScanMode { get; set; } = AdvertScanModes.Wake;

    public bool StepLeWake { get; set; } = true;
    public bool StepKsReconnect { get; set; } = true;
    public bool StepLeAudioRestart { get; set; } = true;
    /// <summary>Off by default: on this setup the headphones do not reconnect after an adapter restart.</summary>
    public bool StepAdapterRestart { get; set; } = false;
    public bool StepAudioServiceRestart { get; set; } = false;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()).Normalized();
        }
        catch (Exception ex)
        {
            Log.Info($"Settings load failed, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    /// <summary>
    /// Clamps hand-edited values into the ranges the UI allows. A negative delay, for example,
    /// would make every connect check throw before it runs.
    /// </summary>
    public Settings Normalized()
    {
        if (string.IsNullOrWhiteSpace(DeviceNameMatch))
            DeviceNameMatch = new Settings().DeviceNameMatch;
        SettleSeconds = Math.Clamp(SettleSeconds, 1, 30);
        StreamTestSeconds = Math.Clamp(StreamTestSeconds, 1, 15);
        FixCooldownSeconds = Math.Clamp(FixCooldownSeconds, 0, 3600);
        LeWakeHoldSeconds = Math.Clamp(LeWakeHoldSeconds, 5, 60);
        WatchdogSeconds = WatchdogSeconds <= 0 ? 0 : Math.Clamp(WatchdogSeconds, 15, 3600);
        if (!Enum.IsDefined(AdvertScanMode))
            AdvertScanMode = AdvertScanModes.LogOnly;
        return this;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
