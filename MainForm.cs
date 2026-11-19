using System.Diagnostics;
using System.Drawing;

namespace BtAudioGuard;

/// <summary>Status, device list, manual actions, settings and live log. Closing hides to tray.</summary>
internal sealed class MainForm : Form
{
    private const int MaxLogChars = 200_000;

    private readonly Guardian _guardian;
    private AdvertForm? _adverts;
    private readonly Label _status = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 14f, FontStyle.Bold),
        Margin = new Padding(3, 3, 3, 0),
    };
    private readonly Label _detail = new() { AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
    private readonly ListView _devices = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
    };
    private readonly TextBox _match = new() { Width = 170 };
    private readonly NumericUpDown _settle = new() { Minimum = 1, Maximum = 30, Width = 50 };
    private readonly NumericUpDown _streamTest = new() { Minimum = 1, Maximum = 15, Width = 50 };
    private readonly NumericUpDown _watchdog = new() { Minimum = 0, Maximum = 3600, Width = 60 };
    private readonly ComboBox _advertMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120,
        Items = { "Off", "Log only", "Wake LE link" },
    };
    private readonly CheckBox _autoFix = new() { Text = "Auto-fix on connect", AutoSize = true };
    private readonly CheckBox _makeDefault = new() { Text = "Make headphones default output", AutoSize = true };
    private readonly CheckBox _stepLe = new() { Text = "Wake LE link", AutoSize = true };
    private readonly CheckBox _stepKs = new() { Text = "Reconnect headphones (KS)", AutoSize = true };
    private readonly CheckBox _stepLeAudio = new() { Text = "Restart LE Audio driver", AutoSize = true };
    private readonly CheckBox _stepAdapter = new() { Text = "Restart adapter", AutoSize = true };
    private readonly CheckBox _stepServices = new() { Text = "Restart audio services", AutoSize = true };
    private bool _allowClose;

    public MainForm(Guardian guardian)
    {
        _guardian = guardian;

        Text = "BtAudioGuard";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1000, 720);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _devices.Columns.Add("Type", 110);
        _devices.Columns.Add("Name", 330);
        _devices.Columns.Add("State", 140);
        _devices.Columns.Add("Default output", 100);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(MakeButton("Check now", () => _guardian.RunAsync(fix: false, manual: true, "manual check")));
        actions.Controls.Add(MakeButton("Fix now", () => _guardian.RunAsync(fix: true, manual: true, "manual fix")));
        actions.Controls.Add(MakeButton("Wake LE link", () => _guardian.RunManualStepAsync("Wake LE link", _guardian.WakeLeLinkAsync)));
        actions.Controls.Add(MakeButton("Reconnect headphones (KS)", () => _guardian.RunManualStepAsync("Reconnect headphones (KS)", _guardian.ReconnectEndpointsAsync)));
        actions.Controls.Add(MakeButton("Restart LE Audio driver", () => _guardian.RunManualStepAsync("Restart LE Audio driver", _guardian.RestartLeAudioDriverAsync)));
        actions.Controls.Add(MakeButton("Restart adapter", () => _guardian.RunManualStepAsync("Restart adapter", AdapterControl.RestartAsync)));
        actions.Controls.Add(MakeButton("Restart audio services", () => _guardian.RunManualStepAsync("Restart audio services", AudioServices.RestartAsync)));
        actions.Controls.Add(MakeButton("Open log file", OpenLogFile));
        actions.Controls.Add(MakeButton("Advertisements...", () =>
        {
            _adverts ??= new AdvertForm(_guardian);
            _adverts.Show();
            _adverts.Activate();
            return Task.CompletedTask;
        }));

        var settings = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        settings.Controls.Add(FieldLabel("Device name contains"));
        settings.Controls.Add(_match);
        settings.Controls.Add(FieldLabel("Wait after connect (s)"));
        settings.Controls.Add(_settle);
        settings.Controls.Add(FieldLabel("Stream test (s)"));
        settings.Controls.Add(_streamTest);
        settings.Controls.Add(FieldLabel("Watchdog (s, 0 = off)"));
        settings.Controls.Add(_watchdog);
        settings.Controls.Add(_autoFix);
        settings.Controls.Add(_makeDefault);
        settings.Controls.Add(FieldLabel("Headphones on, nothing connected:"));
        settings.Controls.Add(_advertMode);
        settings.SetFlowBreak(_advertMode, true);
        settings.Controls.Add(FieldLabel("Fix steps, in order:"));
        settings.Controls.Add(_stepLe);
        settings.Controls.Add(_stepKs);
        settings.Controls.Add(_stepLeAudio);
        settings.Controls.Add(_stepAdapter);
        settings.Controls.Add(_stepServices);
        settings.Controls.Add(MakeButton("Save settings", () =>
        {
            SaveSettings();
            return Task.CompletedTask;
        }));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_detail, 0, 1);
        root.Controls.Add(_devices, 0, 2);
        root.Controls.Add(actions, 0, 3);
        root.Controls.Add(settings, 0, 4);
        root.Controls.Add(_log, 0, 5);
        Controls.Add(root);

        LoadSettingsIntoUi();
        _log.Text = string.Join(Environment.NewLine, Log.Tail(300)) + Environment.NewLine;

        Log.Line += line => InvokeSafe(() => AppendLog(line));
        _guardian.StateChanged += _ => InvokeSafe(UpdateStatus);
        _guardian.DevicesChanged += () => InvokeSafe(UpdateDevices);
        Shown += (_, _) =>
        {
            UpdateStatus();
            UpdateDevices();
            ScrollLogToEnd();
        };
    }

    public event Action? SettingsSaved;

    public void AllowClose()
    {
        _allowClose = true;
        _adverts?.AllowClose();
        _adverts?.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _adverts?.Dispose();
        base.Dispose(disposing);
    }

    public void LoadSettingsIntoUi()
    {
        var s = _guardian.Settings;
        _match.Text = s.DeviceNameMatch;
        _settle.Value = Math.Clamp(s.SettleSeconds, (int)_settle.Minimum, (int)_settle.Maximum);
        _streamTest.Value = Math.Clamp(s.StreamTestSeconds, (int)_streamTest.Minimum, (int)_streamTest.Maximum);
        _watchdog.Value = Math.Clamp(s.WatchdogSeconds, (int)_watchdog.Minimum, (int)_watchdog.Maximum);
        _autoFix.Checked = s.AutoFix;
        _makeDefault.Checked = s.MakeDefaultOutput;
        _advertMode.SelectedIndex = (int)s.AdvertScanMode;
        _stepLe.Checked = s.StepLeWake;
        _stepKs.Checked = s.StepKsReconnect;
        _stepLeAudio.Checked = s.StepLeAudioRestart;
        _stepAdapter.Checked = s.StepAdapterRestart;
        _stepServices.Checked = s.StepAudioServiceRestart;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void SaveSettings()
    {
        string match = _match.Text.Trim();
        if (match.Length == 0)
        {
            MessageBox.Show(this, "Enter part of the headphones' name, for example \"Bose QC Ultra\".",
                "BtAudioGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var s = _guardian.Settings;
        bool matchChanged = !string.Equals(s.DeviceNameMatch, match, StringComparison.Ordinal);

        s.DeviceNameMatch = match;
        s.SettleSeconds = (int)_settle.Value;
        s.StreamTestSeconds = (int)_streamTest.Value;
        s.WatchdogSeconds = (int)_watchdog.Value;
        s.AutoFix = _autoFix.Checked;
        s.MakeDefaultOutput = _makeDefault.Checked;
        s.AdvertScanMode = (AdvertScanModes)Math.Max(0, _advertMode.SelectedIndex);
        s.StepLeWake = _stepLe.Checked;
        s.StepKsReconnect = _stepKs.Checked;
        s.StepLeAudioRestart = _stepLeAudio.Checked;
        s.StepAdapterRestart = _stepAdapter.Checked;
        s.StepAudioServiceRestart = _stepServices.Checked;
        s.Normalized().Save();
        LoadSettingsIntoUi(); // show values as clamped
        Log.Info("Settings saved");

        if (matchChanged)
            _ = _guardian.RebuildAsync();
        else
            _guardian.UpdateScanner();

        SettingsSaved?.Invoke();
    }

    private void UpdateStatus()
    {
        var status = _guardian.Status;
        (_status.Text, _status.ForeColor) = status.State switch
        {
            GuardState.Healthy => ("Audio OK", Color.SeaGreen),
            GuardState.Stalled => ("Audio stalled", Color.Crimson),
            GuardState.Fixing => ("Fixing…", Color.DarkOrange),
            GuardState.Checking => ("Checking…", Color.DarkGoldenrod),
            GuardState.Disconnected => ("Headphones off", SystemColors.GrayText),
            _ => ("Starting…", SystemColors.GrayText),
        };
        _detail.Text = status.Detail;
    }

    private void UpdateDevices()
    {
        _devices.BeginUpdate();
        _devices.Items.Clear();

        foreach (var device in _guardian.BluetoothDevices)
            _devices.Items.Add(new ListViewItem(new[] { $"Bluetooth {device.Protocol}", device.Name,
                device.Connected ? "connected" : "not connected", "" }));

        foreach (var endpoint in _guardian.Endpoints)
            _devices.Items.Add(new ListViewItem(new[] { endpoint.Flow == NAudio.CoreAudioApi.DataFlow.Render ? "Audio output" : "Audio input", endpoint.Name,
                endpoint.State.ToString(), endpoint.IsDefault ? "yes" : "" }));

        if (_devices.Items.Count == 0)
            _devices.Items.Add(new ListViewItem(new[] { "", $"No device matches \"{_guardian.Settings.DeviceNameMatch}\". Check the name below.", "", "" }));

        _devices.EndUpdate();
    }

    private void AppendLog(string line)
    {
        if (_log.TextLength > MaxLogChars)
            _log.Text = _log.Text[^(MaxLogChars / 2)..];
        _log.AppendText(line + Environment.NewLine);
    }

    private void ScrollLogToEnd()
    {
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void InvokeSafe(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Window closed between the check and the call.
        }
    }

    private static Task OpenLogFile()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    private static Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Info($"{text} failed: {ex.Message}");
            }
            finally
            {
                button.Enabled = true;
            }
        };
        return button;
    }
}
