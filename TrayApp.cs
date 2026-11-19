using System.Drawing;
using System.Drawing.Drawing2D;

namespace BtAudioGuard;

/// <summary>Tray icon. Dot color shows the state; right-click for actions; double-click opens the window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly Guardian _guardian;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _autoFixItem;
    private readonly Dictionary<GuardState, Icon> _icons = new()
    {
        [GuardState.Starting] = MakeIcon(Color.Gray),
        [GuardState.Disconnected] = MakeIcon(Color.DimGray),
        [GuardState.Checking] = MakeIcon(Color.Goldenrod),
        [GuardState.Healthy] = MakeIcon(Color.SeaGreen),
        [GuardState.Stalled] = MakeIcon(Color.Crimson),
        [GuardState.Fixing] = MakeIcon(Color.DarkOrange),
    };
    private GuardState _lastState = GuardState.Starting;

    public TrayApp()
    {
        Log.Info("BtAudioGuard started");
        DevNode.RecoverPending(); // a device a previous run disabled and never re-enabled
        _settings = Settings.Load();
        _guardian = new Guardian(_settings);

        _form = new MainForm(_guardian);
        _ = _form.Handle; // create the handle now so BeginInvoke works before the window is shown

        _autoFixItem = new ToolStripMenuItem("Auto-fix", null, (_, _) => ToggleAutoFix())
        {
            Checked = _settings.AutoFix,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowForm());
        menu.Items.Add("Check now", null, (_, _) => _ = _guardian.RunAsync(fix: false, manual: true, "manual check"));
        menu.Items.Add("Fix now", null, (_, _) => _ = _guardian.RunAsync(fix: true, manual: true, "manual fix"));
        menu.Items.Add(_autoFixItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _icons[GuardState.Starting],
            Text = "BtAudioGuard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        _guardian.StateChanged += status =>
        {
            try
            {
                if (_form.IsHandleCreated && !_form.IsDisposed)
                    _form.BeginInvoke(() => UpdateTray(status));
            }
            catch (InvalidOperationException)
            {
                // Window closing.
            }
        };
        _form.SettingsSaved += () => _autoFixItem.Checked = _settings.AutoFix;

        _ = Task.Run(async () =>
            Log.Info($"Bluetooth adapter: {await AdapterControl.GetInstanceIdAsync() ?? "not found"}"));
    }

    private void UpdateTray(GuardStatus status)
    {
        var (state, detail) = status;
        _tray.Icon = _icons[state];

        string text = $"BtAudioGuard: {state}";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        bool fixedNow = state == GuardState.Healthy && _lastState == GuardState.Fixing;
        // Stalled is only set once no fix is going to run (fix failed, auto-fix off, cooldown,
        // watchdog), so every entry into Stalled deserves a warning.
        bool failedNow = state == GuardState.Stalled && _lastState != GuardState.Stalled;

        if (fixedNow)
            _tray.ShowBalloonTip(3000, "Headphone audio fixed", detail, ToolTipIcon.Info);
        else if (failedNow)
            _tray.ShowBalloonTip(5000, "Headphone audio stalled", detail, ToolTipIcon.Warning);

        _lastState = state;
    }

    private void ToggleAutoFix()
    {
        _settings.AutoFix = !_settings.AutoFix;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Info($"Settings save failed: {ex.Message}");
        }
        _autoFixItem.Checked = _settings.AutoFix;
        _form.LoadSettingsIntoUi();
        Log.Info($"Auto-fix {(_settings.AutoFix ? "on" : "off")}");
    }

    private void ShowForm()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    protected override void ExitThreadCore()
    {
        Log.Info("BtAudioGuard exiting");
        _tray.Visible = false;
        _tray.Dispose();
        _guardian.Dispose();
        _form.AllowClose();
        _form.Dispose();
        base.ExitThreadCore();
    }

    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
