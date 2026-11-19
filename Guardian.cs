using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace BtAudioGuard;

public enum GuardState
{
    Starting,
    Disconnected,
    Checking,
    Healthy,
    Stalled,
    Fixing,
}

/// <summary>State and its detail text, always read and written together.</summary>
public sealed record GuardStatus(GuardState State, string Detail);

/// <summary>
/// Watches the headphones, tests the audio stream after each connect (and periodically while
/// audio plays) and, when the stream is dead, walks a ladder of fixes from least to most
/// disruptive. Every step is logged, so the log shows which fix works for this headset/adapter.
/// </summary>
public sealed class Guardian : IDisposable
{
    private sealed record FixStep(string Name, Func<Task<(bool Ok, string Detail)>> Run, Func<Task> SettleAsync);

    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _endpointGate = new();
    private readonly object _refreshGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BluetoothWatcher _bluetooth = null!;
    private AudioEndpoints _audio = null!;
    private CancellationTokenSource? _pendingCheck;
    // Lives as long as one Build(): cancels the startup check and the watchdog on Teardown.
    private CancellationTokenSource _buildLife = new();
    private string _lastSessions = "";
    private volatile GuardStatus _status = new(GuardState.Starting, "");

    // True while a fix step runs. Connect/disconnect events in that window are caused by the
    // step itself (forced LE connect, driver or adapter restart) and must not trigger another check.
    private volatile bool _fixing;

    // Connect events shortly after a fix are usually late echoes of the fix itself (KS reconnect,
    // driver restart). Treating them as a new headphone connect would re-run a check that moves
    // the default output back to the headphones.
    private DateTime _fixEndedUtc = DateTime.MinValue;

    // Set when an automatic fix run fails. Blocks the next automatic run for the cooldown, so a fix
    // that does not work is not repeated in a loop. Successful and manual runs do not set it.
    private DateTime _lastFailedAutoFixUtc = DateTime.MinValue;
    private Dictionary<string, EndpointInfo> _endpoints = [];

    // Advertisement scan (headphones on, but nothing connects).
    private AdvertScanner _scanner = null!;
    private volatile bool _startupDone;
    // An "episode" is one unbroken run of advertisements from the headphones. Its cause decides
    // whether waking them is safe (see WakeDecision).
    private enum EpisodeCause { Unknown, LeDropWhilePlaying, LeDropIdle, PowerOn }

    private DateTime _lastLinkChangeUtc = DateTime.MinValue;
    private DateTime _lastLeDropUtc = DateTime.MinValue;
    private bool _playingAtLeDrop;
    private long _lastPlaybackTicks; // UTC ticks of the last time an app was seen playing to the headphones
    private DateTime _lastHeadsetAdvertUtc = DateTime.MinValue;
    private DateTime _episodeStartUtc = DateTime.MinValue;
    private EpisodeCause _episodeCause;
    private int _episodeWakes;
    private string _episodeDecision = "";
    private LeAnnouncement _episodeAnnouncement;
    private readonly HashSet<string> _episodePayloads = [];
    private DateTime _lastAdvertWakeUtc = DateTime.MinValue;

    private static readonly TimeSpan SilenceForPowerOn = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SustainedAdvertising = TimeSpan.FromSeconds(5);
    private readonly object _advertGate = new();

    public Guardian(Settings settings)
    {
        Settings = settings;
        Build();
    }

    public Settings Settings { get; }

    public GuardStatus Status => _status;

    public GuardState State => _status.State;

    public string Detail => _status.Detail;

    public event Action<GuardStatus>? StateChanged;

    public event Action? DevicesChanged;

    /// <summary>Every advertisement that may be the headphones (for the Advertisements window).</summary>
    public event Action<AdvertSeen>? AdvertObserved;

    public IReadOnlyList<BtDevice> BluetoothDevices => _bluetooth.Snapshot();

    public IReadOnlyList<EndpointInfo> Endpoints
    {
        get { lock (_endpointGate) return _endpoints.Values.OrderBy(e => e.Flow).ThenBy(e => e.Name).ToList(); }
    }

    /// <summary>
    /// Re-creates the watchers. Call after the device name match changes. Runs on the thread pool
    /// and waits for any running check or fix, which would otherwise see the watchers swapped.
    /// </summary>
    public Task RebuildAsync() => Task.Run(async () =>
    {
        try
        {
            await _busy.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            Teardown();
            lock (_endpointGate) _endpoints = [];
            Build();
        }
        finally
        {
            _busy.Release();
        }
        DevicesChanged?.Invoke();
    });

    /// <summary>
    /// Stops new fix steps and waits (bounded) for a device node restart in progress, so a
    /// disabled node gets re-enabled before the process exits.
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        var stopwatch = Stopwatch.StartNew();
        while (DevNode.InFlight > 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(100);
        if (DevNode.InFlight > 0)
            Log.Info("Exit: a device restart was still running after 10 s; the next start re-enables it");
        Teardown();
    }

    private void Build()
    {
        Log.Info($"Watching devices with name containing \"{Settings.DeviceNameMatch}\"");

        // Create everything the watcher's events touch (the scanner) before starting it: its first
        // events arrive on other threads right after Start().
        _bluetooth = new BluetoothWatcher(Settings.DeviceNameMatch);
        _scanner = new AdvertScanner(() => Settings.DeviceNameMatch, IdentityAddress);
        _scanner.Seen += OnAdvertSeen;
        _startupDone = false;

        _bluetooth.ConnectionChanged += OnBluetoothConnectionChanged;
        _bluetooth.ListChanged += OnBluetoothListChanged;
        _bluetooth.Start();

        _audio = new AudioEndpoints();
        _audio.Changed += OnEndpointNotification;
        _audio.DefaultChanged += OnDefaultChanged;

        _buildLife.Dispose();
        _buildLife = new CancellationTokenSource();
        var token = _buildLife.Token;
        var bluetooth = _bluetooth;

        _ = Task.Run(RefreshEndpoints);
        _ = Task.Run(async () =>
        {
            try
            {
                // The link state is only known once both watchers finished enumerating. Before
                // that, "nothing connected" would start the scanner next to a connected headset.
                await bluetooth.WaitReadyAsync(TimeSpan.FromSeconds(30), token);
                await RunCoreAsync(fix: false, manual: false, "startup", watchdog: false);
                if (token.IsCancellationRequested)
                    return;
                _startupDone = true;
                UpdateScanner();
            }
            catch (OperationCanceledException)
            {
                // Rebuilt or exiting before startup finished.
            }
        });

        _ = WatchdogLoopAsync(token);
    }

    private void Teardown()
    {
        Interlocked.Exchange(ref _pendingCheck, null)?.Cancel();
        _buildLife.Cancel();
        _bluetooth.ConnectionChanged -= OnBluetoothConnectionChanged;
        _bluetooth.ListChanged -= OnBluetoothListChanged;
        _bluetooth.Dispose();
        _audio.Changed -= OnEndpointNotification;
        _audio.DefaultChanged -= OnDefaultChanged;
        _audio.Dispose();
        _scanner.Seen -= OnAdvertSeen;
        _scanner.Dispose();
    }

    private void OnBluetoothListChanged()
    {
        UpdateScanner(); // enumeration results can change "anything connected"
        DevicesChanged?.Invoke();
    }

    // Core Audio forbids calls back into it from the notification thread, so hop off.
    private void OnEndpointNotification() => Task.Run(RefreshEndpoints);

    private void OnDefaultChanged(DataFlow flow, Role role, string endpointId) => Task.Run(() =>
        Log.Info($"Windows default {(flow == DataFlow.Render ? "output" : "input")} ({role}) -> {AudioEndpoints.NameOf(endpointId)}"));

    private void RefreshEndpoints()
    {
        // Notifications arrive in bursts. Serialize the scans so an older, slower scan cannot
        // overwrite the result of a newer one.
        lock (_refreshGate)
        {
            List<EndpointInfo> current;
            try
            {
                current = AudioEndpoints.Find(Settings.DeviceNameMatch, DataFlow.Render)
                    .Concat(AudioEndpoints.Find(Settings.DeviceNameMatch, DataFlow.Capture))
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Info($"Endpoint scan failed: {ex.Message}");
                return;
            }

            bool changed = false;
            lock (_endpointGate)
            {
                foreach (var endpoint in current)
                {
                    if (_endpoints.TryGetValue(endpoint.Id, out var old) && old == endpoint)
                        continue;
                    string kind = endpoint.Flow == DataFlow.Render ? "output" : "input";
                    string from = old is null ? "" : $"{old.State} -> ";
                    Log.Info($"Audio {kind}: {endpoint.Name}, state={from}{endpoint.State}, default={endpoint.IsDefault}");
                    changed = true;
                }

                foreach (var gone in _endpoints.Values.Where(old => current.All(c => c.Id != old.Id)))
                {
                    Log.Info($"Audio endpoint gone: {gone.Name}");
                    changed = true;
                }

                _endpoints = current.ToDictionary(e => e.Id);
            }

            if (changed)
                DevicesChanged?.Invoke();
        }
    }

    private void OnBluetoothConnectionChanged(BtDevice device)
    {
        // Tracked even during a fix: the advertisement wake needs to know how each advertising
        // episode began.
        var now = DateTime.UtcNow;
        lock (_advertGate)
        {
            _lastLinkChangeUtc = now;
            if (device.Protocol == "LE" && !device.Connected)
            {
                _lastLeDropUtc = now;
                _playingAtLeDrop = now - new DateTime(Interlocked.Read(ref _lastPlaybackTicks), DateTimeKind.Utc) < TimeSpan.FromSeconds(12);
            }
        }
        if (device.Protocol == "LE" && !device.Connected)
            Log.Info($"LE link dropped {(_playingAtLeDrop ? "while audio was playing" : "with nothing playing")}");
        UpdateScanner();
        _ = Task.Run(DevNode.RecoverPending); // a node a failed enable left disabled

        if (_fixing)
            return;
        if (device.Connected && DateTime.UtcNow - _fixEndedUtc < TimeSpan.FromSeconds(5))
        {
            Log.Info($"Ignoring {device.Protocol} connect right after a fix (caused by the fix)");
            return;
        }

        if (!device.Connected)
        {
            if (!_bluetooth.AnyConnected)
            {
                Interlocked.Exchange(ref _pendingCheck, null)?.Cancel();
                _lastSessions = "";
                SetState(GuardState.Disconnected, "headphones off");
            }
            return;
        }

        // LE and Classic entries both connect within a moment of each other: debounce to one check.
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pendingCheck, cts);
        previous?.Cancel();
        previous?.Dispose();
        var pendingToken = cts.Token; // read now: cts may be disposed by the next event
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Settings.SettleSeconds), pendingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunCoreAsync(Settings.AutoFix, manual: false, "headphones connected", watchdog: false);
        });
    }

    /// <summary>
    /// While the LE link is up: logs which apps hold streams whenever that changes. While an app
    /// is playing and audio is OK, re-tests the stream so a stall during use (frozen video, silent
    /// Spotify) is caught. While Stalled, re-tests so the status turns green again on recovery.
    /// No probe while nothing plays: it would be pointless and could keep the headset from
    /// switching itself off when idle.
    /// </summary>
    private async Task WatchdogLoopAsync(CancellationToken token)
    {
        // Ticks every 5 s so a changed interval applies quickly; probes when the interval is due.
        var lastProbe = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);

                // Remember when something last played to the headphones: an LE drop during playback
                // is an interruption worth fixing, one with nothing playing is the headset going to sleep.
                if (!_fixing && _bluetooth.LeDevice?.Connected == true &&
                    await Task.Run(() => AudioEndpoints.HasActivePlayback(Settings.DeviceNameMatch, audible: true), token))
                {
                    Interlocked.Exchange(ref _lastPlaybackTicks, DateTime.UtcNow.Ticks);
                }

                int seconds = Settings.WatchdogSeconds;
                if (seconds <= 0 || DateTime.UtcNow - lastProbe < TimeSpan.FromSeconds(seconds))
                    continue;
                lastProbe = DateTime.UtcNow;

                // Only while the LE link (the one carrying audio) is up. When the headphones power
                // off, LE drops before Classic: probing in that gap would "fix" it by waking the LE
                // link and keep the headphones from turning off.
                if (_fixing || _bluetooth.LeDevice?.Connected != true)
                    continue;

                string sessions = await Task.Run(() => AudioEndpoints.DescribeSessions(Settings.DeviceNameMatch), token);
                if (sessions != _lastSessions)
                {
                    Log.Info($"Sessions: {sessions}");
                    _lastSessions = sessions;
                }

                // Never probe while nothing plays (also while Stalled): pointless, and it could keep
                // the headset from switching itself off when idle.
                var state = State;
                if (state is GuardState.Healthy or GuardState.Stalled &&
                    await Task.Run(() => AudioEndpoints.HasActivePlayback(Settings.DeviceNameMatch), token))
                {
                    await RunCoreAsync(Settings.AutoFix, manual: false, "watchdog", watchdog: true);
                }
            }
            catch (OperationCanceledException)
            {
                return; // teardown
            }
            catch (Exception ex)
            {
                // One bad tick (e.g. the endpoint builder restarting) must not end the watchdog.
                Log.Info($"Watchdog tick failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scans for the headphones' advertisements only while nothing is connected (and the mode is
    /// not Off). Call whenever the link state or the setting changes.
    /// </summary>
    public void UpdateScanner()
    {
        var bluetooth = _bluetooth;
        _scanner.Update(() => _startupDone && Settings.AdvertScanMode != AdvertScanModes.Off && !bluetooth.AnyConnected);
    }

    private ulong? IdentityAddress()
    {
        var device = _bluetooth.LeDevice ?? _bluetooth.ClassicDevice;
        string? hex = device is null ? null : LeAudioNode.AddressFromAepId(device.Id);
        return hex is null ? null : Convert.ToUInt64(hex, 16);
    }

    private void OnAdvertSeen(AdvertSeen seen)
    {
        try
        {
            AdvertObserved?.Invoke(seen);
        }
        catch
        {
            // A closing window must not break the scan.
        }

        if (!seen.Strong || _bluetooth.AnyConnected)
            return; // only our headphones decide anything; connected: the scan is about to stop

        var lines = new List<string>();
        string decision;
        lock (_advertGate)
        {
            var now = DateTime.UtcNow;
            var gap = now - _lastHeadsetAdvertUtc;
            if (_episodeStartUtc < _lastLinkChangeUtc || gap >= SilenceForPowerOn)
            {
                _episodeStartUtc = now;
                _episodeWakes = 0;
                _episodeDecision = "";
                _episodeAnnouncement = LeAnnouncement.None;
                _episodePayloads.Clear();
                var scanSince = _scanner.RunningSinceUtc;
                _episodeCause =
                    now - _lastLeDropUtc < TimeSpan.FromSeconds(15)
                        ? (_playingAtLeDrop ? EpisodeCause.LeDropWhilePlaying : EpisodeCause.LeDropIdle)
                    : scanSince is { } since && now - since >= SilenceForPowerOn && gap >= SilenceForPowerOn
                        ? EpisodeCause.PowerOn
                        : EpisodeCause.Unknown;
                lines.Add($"Headphones advertising, {Describe(_episodeCause)}: {seen.Describe()}");
            }
            _lastHeadsetAdvertUtc = now;

            // Only the extended advertisements carry the announcement; the legacy ones in between
            // say nothing about it and leave the episode's state alone.
            if (seen.Announcement != LeAnnouncement.None && seen.Announcement != _episodeAnnouncement)
            {
                lines.Add($"  announcement: {Describe(_episodeAnnouncement)} -> {Describe(seen.Announcement)}");
                _episodeAnnouncement = seen.Announcement;
            }

            // Each distinct payload once per episode (capped): the history needed to spot a byte
            // that changes when the headphones are put on.
            if (_episodePayloads.Count < 20 && _episodePayloads.Add(seen.DescribePayload()))
                lines.Add($"  payload {seen.DescribePayload()}");

            decision = WakeDecisionLocked(seen, now);
            if (decision != _episodeDecision)
            {
                _episodeDecision = decision;
                if (decision != "wake")
                    lines.Add($"  -> {decision}");
            }
        }

        foreach (var line in lines)
            Log.Info(line);

        if (decision == "wake")
            _ = Task.Run(() => WakeFromAdvertisementAsync(seen));
    }

    private static string Describe(LeAnnouncement announcement) => announcement switch
    {
        LeAnnouncement.Targeted => "targeted (asking the PC to connect)",
        LeAnnouncement.General => "general (available, not asking)",
        _ => "none yet",
    };

    private static string Describe(EpisodeCause cause) => cause switch
    {
        EpisodeCause.LeDropWhilePlaying => "LE dropped while audio was playing",
        EpisodeCause.LeDropIdle => "LE dropped with nothing playing",
        EpisodeCause.PowerOn => "after silence (switched on)",
        _ => "was already advertising when the scan started",
    };

    /// <summary>
    /// "wake", or the reason not to. Decided per episode, not per advertisement: the headset
    /// alternates legacy advertisements (no announcement) with extended ones (announcement).
    /// - Targeted announcement: the headset asks its paired PC to connect (seen right after it
    ///   is switched on). Windows ignores it; this answers it.
    /// - General announcement: only "available". Seen while it lies on the table going to sleep;
    ///   answering it keeps it awake. Never woken.
    /// - Fallback without Targeted: the LE link dropped while audio was audibly playing (an
    ///   interruption), after 5 s of advertising (a headset switched off stops within a second).
    /// </summary>
    private string WakeDecisionLocked(AdvertSeen seen, DateTime now)
    {
        if (Settings.AdvertScanMode != AdvertScanModes.Wake)
            return "log only";

        bool targeted = _episodeAnnouncement == LeAnnouncement.Targeted;
        bool interrupted = _episodeCause == EpisodeCause.LeDropWhilePlaying;
        if (!targeted && !interrupted)
        {
            return _episodeAnnouncement == LeAnnouncement.General
                ? "not acting: general announcement (available, not asking the PC to connect)"
                : "not acting: no targeted announcement";
        }
        if (!targeted && now - _episodeStartUtc < SustainedAdvertising)
            return $"waiting for {SustainedAdvertising.TotalSeconds:0} s of advertising";
        if (_fixing || _busy.CurrentCount == 0)
            return "not acting: a check or fix is running";
        if (_episodeWakes >= 2)
            return "not acting: tried twice in this episode";
        if (now - _lastAdvertWakeUtc < TimeSpan.FromSeconds(60))
            return "not acting: tried less than 60 s ago";

        _episodeWakes++;
        _lastAdvertWakeUtc = now;
        return "wake";
    }

    private async Task WakeFromAdvertisementAsync(AdvertSeen seen)
    {
        if (!await _busy.WaitAsync(0))
            return;

        bool woke = false;
        _fixing = true;
        try
        {
            if (_bluetooth.AnyConnected)
                return; // connected on its own meanwhile: checked below

            Log.Info($"--- Headphones advertising ({Describe(_episodeCause)}, announcement {Describe(_episodeAnnouncement)}): Wake LE link ---");
            SetState(GuardState.Fixing, "Wake LE link (headphones advertising)");
            var (ok, detail) = await WakeLeLinkAsync();
            Log.Info($"  Wake LE link: {(ok ? "done" : "failed")}: {detail}");
            woke = ok;
            if (!ok)
                SetIdleState("wake from advertisement failed");
        }
        catch (Exception ex)
        {
            Log.Info($"  Wake from advertisement error: {ex.Message}");
            SetIdleState("wake from advertisement failed");
        }
        finally
        {
            EndFix();
            _busy.Release();
        }

        // Connect events while _fixing was set (the forced LE connect, or the headphones connecting
        // on their own meanwhile) were ignored: run the check here instead.
        UpdateScanner();
        if (woke || _bluetooth.AnyConnected)
        {
            await Task.Delay(1500);
            await RunCoreAsync(Settings.AutoFix, manual: false, "after wake from advertisement", watchdog: false);
        }
    }

    /// <summary>Check (and optionally fix). Runs on the thread pool, so UI callers never block.</summary>
    public Task RunAsync(bool fix, bool manual, string reason) =>
        Task.Run(() => RunCoreAsync(fix, manual, reason, watchdog: false));

    private async Task RunCoreAsync(bool fix, bool manual, string reason, bool watchdog)
    {
        if (!await _busy.WaitAsync(0))
        {
            if (watchdog)
                return; // something else is already testing

            // Queue instead of drop: an automatic check must still run after a manual one.
            Log.Info($"Check queued ({reason}): waiting for active run");
            try
            {
                if (!await _busy.WaitAsync(TimeSpan.FromMinutes(5), _lifetime.Token))
                {
                    Log.Info($"Check dropped ({reason}): active run did not finish in 5 min");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return; // exiting
            }
        }

        try
        {
            // Read once, at entry: the check below takes seconds, and the headphones may switch
            // off meanwhile. Decides whether the ladder may run with nothing connected.
            bool connectedAtStart = _bluetooth.AnyConnected;
            if (!manual && !connectedAtStart)
            {
                SetState(GuardState.Disconnected, "headphones off");
                return;
            }

            // Watchdog probes stay silent while nothing changes: no log line, no tray flicker,
            // and no default-output change (a manual switch to other speakers must stick).
            if (!watchdog)
            {
                Log.Info($"--- Check: {reason} ---");
                SetState(GuardState.Checking, "testing audio stream");
            }

            var result = await CheckAsync(setDefault: !watchdog);
            if (watchdog)
            {
                if (result.Ok)
                {
                    if (State != GuardState.Healthy)
                    {
                        Log.Info($"Watchdog: audio OK again: {result.Detail}");
                        SetState(GuardState.Healthy, result.Detail);
                    }
                    return;
                }
                // Still stalled and no fix due (auto-fix off or cooldown): already reported.
                if (State == GuardState.Stalled && (!fix || CooldownActive() || result.Hung))
                    return;
                Log.Info($"--- Check: {reason} ---");
            }

            Log.Info($"Check {(result.Ok ? "OK" : "FAILED")}: {result.Detail}");
            if (result.Ok)
            {
                SetState(GuardState.Healthy, result.Detail);
                return;
            }

            LogContext();

            // A hung WASAPI call leaves a stuck thread in the audio stack. The fix steps cannot
            // clear that; only an audio service restart or an app restart can.
            if (result.Hung)
            {
                Log.Info("Audio stack hung: fix steps skipped. Use \"Restart audio services\", or restart the app.");
                SetState(GuardState.Stalled, "audio stack hung: use Restart audio services, or restart the app");
                return;
            }

            // A watchdog failure where the link or endpoint is gone is the headphones powering off
            // mid-probe, not a stall. Waking them now would keep them on.
            if (watchdog && !(_bluetooth.LeDevice?.Connected == true && EndpointActive()))
            {
                Log.Info("Watchdog: LE link or endpoint went away during the probe (headphones powering off?): no fix");
                SetIdleState(result.Detail);
                return;
            }

            if (!fix)
            {
                SetState(GuardState.Stalled, result.Detail);
                return;
            }

            if (!manual && CooldownActive())
            {
                Log.Info("Auto-fix skipped: an automatic fix failed less than the cooldown ago (prevents restart loops). Use \"Fix now\".");
                SetState(GuardState.Stalled, $"{result.Detail} (auto-fix on cooldown)");
                return;
            }

            // The ladder stops when the headphones are gone (taken off, switched off): waking them
            // then would keep them on. Exception: a manual fix that started with nothing connected
            // (Windows shows "Not connected"). There Wake LE link is the point: it connects them,
            // like the Connect button in Settings.
            bool mayRunDisconnected = manual && !connectedAtStart;
            _fixing = true;
            foreach (var step in FixSteps())
            {
                if (_lifetime.IsCancellationRequested)
                {
                    Log.Info("Exiting: fix stopped");
                    return;
                }
                if (!mayRunDisconnected && !_bluetooth.AnyConnected)
                {
                    Log.Info("Headphones disconnected: fix stopped");
                    SetState(GuardState.Disconnected, "headphones off");
                    return;
                }

                SetState(GuardState.Fixing, step.Name);
                Log.Info($"Fix step: {step.Name}");

                var (ok, detail) = await step.Run();
                Log.Info($"  {step.Name}: {(ok ? "done" : "failed")}: {detail}");
                if (!ok)
                    continue;

                await step.SettleAsync();
                result = await CheckAsync(setDefault: !watchdog);
                Log.Info($"  Re-check {(result.Ok ? "OK" : "FAILED")}: {result.Detail}");
                if (result.Ok)
                {
                    Log.Info($"FIXED by: {step.Name}");
                    SetState(GuardState.Healthy, $"fixed by {step.Name}");
                    return;
                }
            }

            if (!manual)
                _lastFailedAutoFixUtc = DateTime.UtcNow;
            LogContext();
            SetIdleState("all enabled fix steps failed");
        }
        catch (Exception ex)
        {
            Log.Info($"Run error: {ex}");
            SetState(GuardState.Stalled, ex.Message);
        }
        finally
        {
            if (_fixing)
                EndFix();
            _busy.Release();
        }
    }

    private bool CooldownActive() =>
        DateTime.UtcNow - _lastFailedAutoFixUtc < TimeSpan.FromSeconds(Settings.FixCooldownSeconds);

    private void EndFix()
    {
        _fixEndedUtc = DateTime.UtcNow;
        _fixing = false;
    }

    /// <summary>
    /// Runs one fix step from a UI button. Takes the same lock as automatic runs, so a manual
    /// step never overlaps an automatic fix, then re-checks so the status reflects the result.
    /// Runs on the thread pool, so the UI never blocks.
    /// </summary>
    public Task RunManualStepAsync(string name, Func<Task<(bool Ok, string Detail)>> step) => Task.Run(async () =>
    {
        Log.Info($"Manual action: {name}");
        if (!await _busy.WaitAsync(0))
        {
            Log.Info($"  {name}: waiting for active run");
            try
            {
                await _busy.WaitAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _fixing = true;
        try
        {
            SetState(GuardState.Fixing, name);
            var (ok, detail) = await step();
            Log.Info($"  {name}: {(ok ? "done" : "failed")}: {detail}");
        }
        catch (Exception ex)
        {
            Log.Info($"  {name}: error: {ex.Message}");
        }
        finally
        {
            EndFix();
            _busy.Release();
        }

        await RunCoreAsync(fix: false, manual: true, $"after {name}", watchdog: false);
    });

    /// <summary>Stalled if the headphones are still there, otherwise Disconnected.</summary>
    private void SetIdleState(string detail)
    {
        if (_bluetooth.AnyConnected)
            SetState(GuardState.Stalled, detail);
        else
            SetState(GuardState.Disconnected, "headphones off");
    }

    /// <summary>Everything that helps explain a failed check, in one block.</summary>
    private void LogContext()
    {
        try
        {
            var links = _bluetooth.Snapshot().Select(d => $"{d.Protocol} {(d.Connected ? "up" : "down")}");
            Log.Info($"  Context: Bluetooth {string.Join(", ", links)}; {DescribeLeAudioNode()}");
            Log.Info($"  Context: sessions: {AudioEndpoints.DescribeSessions(Settings.DeviceNameMatch)}");
        }
        catch (Exception ex)
        {
            Log.Info($"  Context: failed: {ex.Message}");
        }
    }

    private string DescribeLeAudioNode()
    {
        string? address = LeAddress();
        return address is null ? "LE Audio driver node: no LE entry" : LeAudioNode.Describe(address);
    }

    private string? LeAddress()
    {
        var le = _bluetooth.LeDevice;
        return le is null ? null : LeAudioNode.AddressFromAepId(le.Id);
    }

    private IEnumerable<FixStep> FixSteps()
    {
        if (Settings.StepLeWake)
            yield return new FixStep("Wake LE link", WakeLeLinkAsync, () => Task.Delay(1500));

        if (Settings.StepKsReconnect)
            yield return new FixStep("Reconnect headphones (KS)", ReconnectEndpointsAsync, () => Task.Delay(5000));

        if (Settings.StepLeAudioRestart)
            yield return new FixStep("Restart LE Audio driver", RestartLeAudioDriverAsync, SettleAfterDriverRestartAsync);

        if (Settings.StepAdapterRestart)
            yield return new FixStep("Restart Bluetooth adapter", AdapterControl.RestartAsync,
                () => WaitForReconnectAsync(TimeSpan.FromSeconds(25)));

        if (Settings.StepAudioServiceRestart)
            yield return new FixStep("Restart Windows audio services", AudioServices.RestartAsync, () => Task.Delay(5000));
    }

    public Task<(bool Ok, string Detail)> ReconnectEndpointsAsync() => Task.Run<(bool Ok, string Detail)>(() =>
    {
        // KS reconnect pages the headset. Only for a stalled stream on an active endpoint: on an
        // Unplugged one it would wake headphones that are switching off.
        var endpoints = AudioEndpoints.Find(Settings.DeviceNameMatch)
            .Where(e => e.State == DeviceState.Active)
            .ToList();
        if (endpoints.Count == 0)
            return (false, "not needed: no active endpoint (" + EndpointSummary() + ")");

        var notes = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var (ok, detail) = KsBtAudio.Cycle(endpoint.Id);
            notes.Add($"{endpoint.Name}: {detail}");
            if (ok)
                return (true, string.Join("; ", notes));
        }
        return (false, string.Join("; ", notes));
    });

    public async Task<(bool Ok, string Detail)> WakeLeLinkAsync()
    {
        var le = _bluetooth.LeDevice;
        if (le is null)
            return (false, "no paired LE entry for the headphones");

        // Waking only helps when the endpoint is missing. With an active endpoint the link is
        // already up, and a stalled stream needs one of the later steps.
        if (EndpointActive())
            return (false, "not needed: endpoint already active, LE link is up");

        // Stop holding the link if the headphones start disconnecting. Keyed on the Classic link,
        // which is up in the "LE did not come back" case; a manual wake with nothing connected
        // (Classic already down) is allowed to run.
        bool classicAtStart = _bluetooth.ClassicDevice?.Connected == true;
        bool Abort() => classicAtStart && _bluetooth.ClassicDevice?.Connected != true;

        var (ok, detail) = await LeLink.WakeAsync(le.Id, EndpointActive, Settings.LeWakeHoldSeconds, Abort);
        return ok ? (ok, detail) : (ok, $"{detail} ({EndpointSummary()})");
    }

    public async Task<(bool Ok, string Detail)> RestartLeAudioDriverAsync()
    {
        string? address = LeAddress();
        if (address is null)
            return (false, "no paired LE entry for the headphones");
        return await LeAudioNode.RestartAsync(address);
    }

    private async Task SettleAfterDriverRestartAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) && !EndpointActive())
            await Task.Delay(250);

        if (EndpointActive())
        {
            Log.Info($"  Endpoint active {stopwatch.ElapsedMilliseconds} ms after driver restart");
        }
        else if (_bluetooth.AnyConnected)
        {
            // The driver came back but did not set up the stream on its own: bring the LE link up.
            // Not when nothing is connected: the headphones may be powering off.
            Log.Info("  Endpoint not active 10 s after driver restart, waking LE link");
            var (ok, detail) = await WakeLeLinkAsync();
            Log.Info($"  Wake LE link: {(ok ? "done" : "failed")}: {detail}");
        }

        await Task.Delay(1500);
    }

    private string EndpointSummary()
    {
        try
        {
            var endpoints = AudioEndpoints.Find(Settings.DeviceNameMatch);
            return endpoints.Count == 0
                ? "no matching endpoint in any state"
                : string.Join(", ", endpoints.Select(e => $"{e.Name}={e.State}"));
        }
        catch (Exception ex)
        {
            return $"endpoint scan failed: {ex.Message}";
        }
    }

    // Used to decide when the LE link can be released / a restart has finished: Windows has
    // rebuilt a usable render endpoint once a matching endpoint reports Active.
    private bool EndpointActive()
    {
        try
        {
            return AudioEndpoints.Find(Settings.DeviceNameMatch)
                .Any(e => e.State == DeviceState.Active);
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForReconnectAsync(TimeSpan max)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(3000); // connection state needs a moment to drop after the adapter restart
        while (stopwatch.Elapsed < max && !_bluetooth.AnyConnected)
            await Task.Delay(500);

        Log.Info(_bluetooth.AnyConnected
            ? $"  Headphones back after {stopwatch.Elapsed.TotalSeconds:0} s"
            : "  Headphones did not reconnect after adapter restart");

        await Task.Delay(TimeSpan.FromSeconds(Settings.SettleSeconds));
    }

    private Task<CheckResult> CheckAsync(bool setDefault) => Task.Run(() =>
    {
        var endpoints = AudioEndpoints.Find(Settings.DeviceNameMatch);
        var active = endpoints.Where(e => e.State == DeviceState.Active).ToList();

        if (active.Count == 0)
        {
            string detail = endpoints.Count == 0
                ? "no matching audio endpoint"
                : "endpoint not active (" + string.Join(", ", endpoints.Select(e => $"{e.Name}={e.State}")) + ")";
            var le = _bluetooth.LeDevice;
            detail += le is null ? ", no LE entry" : $", LE link {(le.Connected ? "up" : "DOWN")}";
            return new CheckResult(false, detail);
        }

        // Prefer the stereo "Headphones (...)" endpoint over a hands-free "Headset (...)" one.
        var target = active.FirstOrDefault(e => e.Name.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase))
                     ?? active.FirstOrDefault(e => e.IsDefault)
                     ?? active[0];

        string note = "";
        if (!target.IsDefault && setDefault)
        {
            if (Settings.MakeDefaultOutput)
            {
                var (ok, detail) = DefaultOutput.Set(target.Id);
                Log.Info($"Default output -> {target.Name}: {(ok ? "OK" : "failed")}: {detail}");
                note = ok ? " [made default output]" : " [warning: could not make default output]";
            }
            else
            {
                note = " [warning: not the default output device]";
            }
        }

        var result = AudioEndpoints.TestStream(target.Id, Settings.StreamTestSeconds);
        return result with { Detail = $"{target.Name}: {result.Detail}{note}" };
    });

    private void SetState(GuardState state, string detail)
    {
        var status = new GuardStatus(state, detail);
        _status = status;
        try
        {
            StateChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Log.Info($"State listener failed: {ex.Message}");
        }
    }
}
