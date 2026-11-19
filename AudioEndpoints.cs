using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BtAudioGuard;

public sealed record EndpointInfo(string Id, string Name, DataFlow Flow, DeviceState State, bool IsDefault);

/// <param name="Hung">The audio stack did not return from a WASAPI call. Fix steps cannot help
/// while that thread is stuck; only an audio service restart or an app restart can.</param>
public sealed record CheckResult(bool Ok, string Detail, bool Hung = false);

/// <summary>
/// Core Audio side: finds the headphones' endpoints, forwards endpoint notifications,
/// tests whether a stream to the endpoint actually runs, and reports which apps hold streams.
/// </summary>
public sealed class AudioEndpoints : IMMNotificationClient, IDisposable
{
    private static readonly object HungGate = new();
    private static Task? _hungProbe;

    private readonly MMDeviceEnumerator _enumerator = new();

    public AudioEndpoints()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>
    /// Raised on any endpoint change in the system. Never call Core Audio from inside
    /// the callback itself (deadlock risk) – subscribers must hop to another thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>Raised when Windows changes a default device. Same threading rule as <see cref="Changed"/>.</summary>
    public event Action<DataFlow, Role, string>? DefaultChanged;

    /// <summary>Matching endpoints of one direction, in every state (NotPresent included).</summary>
    public static List<EndpointInfo> Find(string match, DataFlow flow = DataFlow.Render)
    {
        var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch (COMException)
        {
            // No default device for this direction.
        }

        var result = new List<EndpointInfo>();
        // Include NotPresent: with the headphones off or the LE link down the endpoint is usually
        // Unplugged, but after some failures (seen after adapter restarts) it drops to NotPresent.
        // Listing both tells "endpoint gone" apart from "endpoint there, link down".
        var states = DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled | DeviceState.NotPresent;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, states))
        {
            string name;
            try
            {
                name = device.FriendlyName;
            }
            catch
            {
                continue;
            }

            if (name.Contains(match, StringComparison.OrdinalIgnoreCase))
                result.Add(new EndpointInfo(device.ID, name, flow, device.State, device.ID == defaultId));
        }
        return result;
    }

    public static string NameOf(string endpointId)
    {
        try
        {
            return new MMDeviceEnumerator().GetDevice(endpointId).FriendlyName;
        }
        catch
        {
            return endpointId;
        }
    }

    /// <summary>
    /// Opens a shared-mode stream, fills the buffer with silence and starts it.
    /// A working endpoint drains the buffer within milliseconds. A stalled Bluetooth
    /// endpoint never drains it (or the WASAPI call itself hangs) – the same condition
    /// that freezes video players waiting for the audio clock.
    /// </summary>
    public static CheckResult TestStream(string endpointId, int seconds)
    {
        lock (HungGate)
        {
            // A hung WASAPI call never returns. Starting another one would only pile up threads
            // (and open streams that may keep the headphones awake).
            if (_hungProbe is { IsCompleted: false })
                return new CheckResult(false, "previous WASAPI call is still hung", Hung: true);
            _hungProbe = null;
        }

        var window = TimeSpan.FromSeconds(seconds);
        var task = Task.Run(() => TestStreamCore(endpointId, window));

        // A hung WASAPI call is itself a failure. The worker thread is abandoned in that case.
        if (!task.Wait(window + TimeSpan.FromSeconds(5)))
        {
            lock (HungGate) _hungProbe = task;
            return new CheckResult(false, "WASAPI call hung, no return", Hung: true);
        }

        return task.Result;
    }

    private static CheckResult TestStreamCore(string endpointId, TimeSpan window)
    {
        string stage = "get device";
        try
        {
            var device = new MMDeviceEnumerator().GetDevice(endpointId);
            if (device.State != DeviceState.Active)
                return new CheckResult(false, $"endpoint state {device.State}");

            stage = "initialize";
            using var client = device.AudioClient;
            var format = client.MixFormat;
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                2_000_000, // 200 ms buffer, in 100 ns units
                0,
                format,
                Guid.Empty);

            stage = "fill buffer";
            int bufferFrames = client.BufferSize;
            var render = client.AudioRenderClient;
            render.GetBuffer(bufferFrames);
            render.ReleaseBuffer(bufferFrames, AudioClientBufferFlags.Silent);

            stage = "audio clock";
            using var clock = client.AudioClockClient;
            ulong frequency = clock.Frequency;

            stage = "start";
            var stopwatch = Stopwatch.StartNew();
            client.Start();
            try
            {
                stage = "drain";
                ulong clockStart = ReadClock(clock);
                int paddingStart = client.CurrentPadding;
                int padding = paddingStart;
                while (stopwatch.Elapsed < window)
                {
                    Thread.Sleep(50);
                    padding = client.CurrentPadding;
                    if (padding < bufferFrames)
                        return new CheckResult(true, $"stream OK, draining after {stopwatch.ElapsedMilliseconds} ms");
                }

                // Stalled. Record what the engine did, so the log tells "clock frozen" (device
                // never pulls data) apart from "clock running but buffer stuck" and similar.
                ulong clockEnd = ReadClock(clock);
                double clockMs = frequency > 0 && clockEnd >= clockStart
                    ? (clockEnd - clockStart) * 1000.0 / frequency
                    : -1;
                return new CheckResult(false,
                    $"stream stalled, buffer not drained in {window.TotalSeconds:0} s " +
                    $"[padding {paddingStart}->{padding} of {bufferFrames} frames, " +
                    $"audio clock advanced {clockMs:0} ms, " +
                    $"format {format.SampleRate} Hz {format.Channels} ch {format.BitsPerSample} bit, " +
                    $"period {client.DefaultDevicePeriod / 10_000.0:0.#} ms]");
            }
            finally
            {
                client.Stop();
            }
        }
        catch (COMException ex)
        {
            return new CheckResult(false, $"WASAPI error 0x{ex.HResult:X8} at '{stage}'");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"{ex.GetType().Name} at '{stage}': {ex.Message}");
        }
    }

    private static ulong ReadClock(AudioClockClient clock)
    {
        try
        {
            return clock.GetPosition(out ulong position, out _) ? position : 0;
        }
        catch
        {
            return 0;
        }
    }

    private sealed record SessionInfo(string Process, bool Active, float Peak);

    private sealed record EndpointSessions(string Endpoint, DataFlow Flow, List<SessionInfo>? Sessions, string? Error);

    /// <summary>
    /// One entry per active matching endpoint (output and microphone) listing the other apps that
    /// hold an audio session on it. This app's own probe session is left out.
    /// Bounded by a timeout: a stalled audio stack must not hang the caller. Null on timeout.
    /// </summary>
    private static List<EndpointSessions>? ScanSessions(string match)
    {
        var task = Task.Run(() =>
        {
            var result = new List<EndpointSessions>();
            int ownPid = Environment.ProcessId;
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                List<EndpointInfo> endpoints;
                try
                {
                    endpoints = Find(match, flow).Where(e => e.State == DeviceState.Active).ToList();
                }
                catch
                {
                    return null; // endpoint enumeration failing (e.g. endpoint builder restarting)
                }

                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        // Disposing the device disposes its session manager, which unregisters the
                        // session notification NAudio registers on creation. Without it, every scan
                        // leaks one registration.
                        using var device = new MMDeviceEnumerator().GetDevice(endpoint.Id);
                        var sessions = device.AudioSessionManager.Sessions;
                        var list = new List<SessionInfo>();
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            var state = session.State;
                            if (state == AudioSessionState.AudioSessionStateExpired || session.GetProcessID == ownPid)
                                continue;
                            bool active = state == AudioSessionState.AudioSessionStateActive;
                            if (session.IsSystemSoundsSession && !active)
                                continue;
                            float peak = 0;
                            try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }
                            list.Add(new SessionInfo(ProcessName(session.GetProcessID), active, peak));
                        }
                        result.Add(new EndpointSessions(endpoint.Name, flow, list, null));
                    }
                    catch (Exception ex)
                    {
                        result.Add(new EndpointSessions(endpoint.Name, flow, null, ex.Message));
                    }
                }
            }
            return result;
        });

        try
        {
            return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One line per active matching endpoint listing the apps that hold an audio session on it,
    /// with state and current peak level. Shows who keeps a stream open, and whether an app
    /// (e.g. a call app opening the mic) changed the LE Audio setup.
    /// </summary>
    public static string DescribeSessions(string match)
    {
        var scan = ScanSessions(match);
        if (scan is null)
            return "session scan failed or hung";
        if (scan.Count == 0)
            return "no active endpoint";

        return string.Join(" | ", scan.Select(e =>
        {
            string kind = e.Flow == DataFlow.Render ? "out" : "mic";
            string body = e.Error is not null ? $"session scan failed: {e.Error}"
                : e.Sessions!.Count == 0 ? "no sessions"
                : string.Join(", ", e.Sessions.Select(x =>
                    $"{x.Process} {(x.Active ? "active" : "idle")}{(x.Peak > 0 ? $" peak {x.Peak:0.00}" : "")}").Order());
            return $"{e.Endpoint} [{kind}]: {body}";
        }));
    }

    /// <summary>
    /// True when another app has an active session on a matching output. With
    /// <paramref name="audible"/>, the session must also have a level above silence right now:
    /// games and browsers keep sessions active while silent.
    /// </summary>
    public static bool HasActivePlayback(string match, bool audible = false)
    {
        var scan = ScanSessions(match);
        return scan is not null && scan.Any(e =>
            e.Flow == DataFlow.Render && e.Sessions is not null &&
            e.Sessions.Any(x => x.Active && (!audible || x.Peak > 0.0005f)));
    }

    private static string ProcessName(uint pid)
    {
        if (pid == 0)
            return "system";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {pid}";
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();

    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();

    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        DefaultChanged?.Invoke(flow, role, defaultDeviceId);
        if (flow == DataFlow.Render && role == Role.Multimedia)
            Changed?.Invoke();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Fires constantly (volume etc.). Not useful here.
    }

    public void Dispose()
    {
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch
        {
            // Ignore on shutdown.
        }
    }
}
