using System.ServiceProcess;

namespace BtAudioGuard;

/// <summary>Restarts Windows Audio and its endpoint builder. Last resort; drops all audio briefly. Needs admin.</summary>
internal static class AudioServices
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static Task<(bool Ok, string Detail)> RestartAsync() => Task.Run<(bool Ok, string Detail)>(() =>
    {
        try
        {
            using var audioService = new ServiceController("Audiosrv");
            using var endpointBuilder = new ServiceController("AudioEndpointBuilder");

            Stop(audioService);
            Stop(endpointBuilder);
            Start(endpointBuilder);
            Start(audioService);

            return (true, "Audiosrv and AudioEndpointBuilder restarted");
        }
        catch (Exception ex)
        {
            // Never leave Windows without audio: try to bring both back up.
            string recovery = "";
            foreach (var name in new[] { "AudioEndpointBuilder", "Audiosrv" })
            {
                try
                {
                    using var service = new ServiceController(name);
                    Start(service);
                }
                catch (Exception startEx)
                {
                    recovery += $"; {name} not running: {startEx.Message}";
                }
            }
            return (false, ex.Message + recovery);
        }
    });

    private static void Stop(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
            return;
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    private static void Start(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running)
            return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }
}
