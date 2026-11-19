using System.Drawing;
using System.Text;

namespace BtAudioGuard;

/// <summary>
/// Live view of the advertisements that may come from the headphones: one row per distinct
/// payload, so a byte that changes when the headphones are put on shows up as a new row.
/// Only filled while nothing is connected (the scan stops when the headphones connect).
/// </summary>
internal sealed class AdvertForm : Form
{
    private sealed class Row
    {
        public required AdvertSeen First { get; init; }
        public DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public short LastRssi { get; set; }
        public int Count { get; set; }
    }

    private const int MaxRows = 300;

    private readonly Guardian _guardian;
    private readonly object _gate = new();
    private readonly Dictionary<string, Row> _rows = [];
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
    };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private bool _dirty;
    private bool _allowClose;

    public AdvertForm(Guardian guardian)
    {
        _guardian = guardian;
        Text = "BtAudioGuard - advertisements";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(1100, 500);
        StartPosition = FormStartPosition.CenterScreen;

        _list.Columns.Add("First seen", 85);
        _list.Columns.Add("Last seen", 85);
        _list.Columns.Add("Count", 55);
        _list.Columns.Add("Device", 170);
        _list.Columns.Add("RSSI", 50);
        _list.Columns.Add("Payload (type:hex per section; FF = manufacturer data, 9E00 = Bose)", 900);

        var help = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(6),
            Text = "Shown only while nothing is connected (the scan stops when the headphones connect). " +
                   "Leave the headphones on the table until the log says \"LE link dropped\", watch for a minute, " +
                   "then put them on. Rows that appear then (highlighted for 10 s) may carry an on-head signal. Copy and paste the list.",
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(4) };
        var copy = new Button { Text = "Copy all", AutoSize = true };
        copy.Click += (_, _) => CopyAll();
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) =>
        {
            lock (_gate) _rows.Clear();
            _dirty = true;
        };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(clear);

        Controls.Add(_list);
        Controls.Add(help);
        Controls.Add(buttons);

        _guardian.AdvertObserved += OnAdvert;
        _refresh.Tick += (_, _) => Redraw();
        _refresh.Start();
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _guardian.AdvertObserved -= OnAdvert;
        _refresh.Stop();
        base.OnFormClosing(e);
    }

    // Background thread: only record; the timer redraws.
    private void OnAdvert(AdvertSeen seen)
    {
        string key = $"{seen.Address:X12}|{seen.DescribePayload()}";
        var now = DateTime.Now;
        lock (_gate)
        {
            if (_rows.TryGetValue(key, out var row))
            {
                row.LastSeen = now;
                row.LastRssi = seen.Rssi;
                row.Count++;
            }
            else if (_rows.Count < MaxRows)
            {
                _rows[key] = new Row { First = seen, FirstSeen = now, LastSeen = now, LastRssi = seen.Rssi, Count = 1 };
            }
        }
        _dirty = true;
    }

    private void Redraw()
    {
        if (!Visible || !_dirty)
            return;
        _dirty = false;

        List<Row> rows;
        lock (_gate) rows = _rows.Values.OrderBy(r => r.FirstSeen).ToList();

        var now = DateTime.Now;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows)
        {
            var item = new ListViewItem(new[]
            {
                r.FirstSeen.ToString("HH:mm:ss"),
                r.LastSeen.ToString("HH:mm:ss"),
                r.Count.ToString(),
                DeviceText(r.First),
                r.LastRssi.ToString(),
                r.First.DescribePayload(),
            });
            if (now - r.FirstSeen < TimeSpan.FromSeconds(10))
                item.BackColor = Color.LightYellow;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        // Keep highlights fading out even without new advertisements.
        _dirty = rows.Any(r => now - r.FirstSeen < TimeSpan.FromSeconds(11));
    }

    private static string DeviceText(AdvertSeen seen) =>
        $"{seen.Address:X12} {(seen.Strong ? "(yours)" : "(other Bose)")}";

    private void CopyAll()
    {
        List<Row> rows;
        lock (_gate) rows = _rows.Values.OrderBy(r => r.FirstSeen).ToList();

        var text = new StringBuilder();
        foreach (var r in rows)
            text.AppendLine($"{r.FirstSeen:HH:mm:ss} {r.LastSeen:HH:mm:ss} x{r.Count} {DeviceText(r.First)} {r.LastRssi} dBm  {r.First.DescribePayload()}");
        if (text.Length > 0)
            Clipboard.SetText(text.ToString());
    }
}
