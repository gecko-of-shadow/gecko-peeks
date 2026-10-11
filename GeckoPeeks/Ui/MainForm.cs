using System.Diagnostics;
using System.Reflection;
using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;

namespace GeckoPeeks.Ui;

internal sealed class MainForm : Form
{
    private readonly ProbeScheduler _scheduler;
    private readonly DataGridView _hopsGrid;
    private readonly DataGridView _logGrid;
    private readonly DataGridView _summaryGrid;
    private readonly Label _hopsEmpty;
    private readonly Label _logEmpty;
    private readonly Label _summaryEmpty;
    private readonly Label _summaryHint;
    private readonly Label _modeValue;
    private readonly Label _loggingValue;
    private readonly Label _leagueValue;
    private readonly Label _nextValue;
    private readonly Label _phaseValue;
    private readonly Label _activityValue;
    private readonly Label _hopsHint;
    private readonly Label _logPathValue;
    private readonly Label _logStatusValue;
    private readonly System.Windows.Forms.Timer _clock;
    private readonly SplitContainer _split;
    private bool _exitRequested;
    private int _hopsRevision = -1;
    private int _logRevision = -1;

    public MainForm(ProbeScheduler scheduler)
    {
        _scheduler = scheduler;
        var version = FormatVersion();

        // Everything below is laid out in 96-DPI pixels. One DPI pass at the end of the
        // constructor scales it all to the display. The pass runs the moment AutoScaleDimensions
        // is set unless layout is suspended, and it only scales controls that exist by then:
        // v1.0.10 set it first, so at 125% the text grew with the display while every fixed
        // height stayed at 96-DPI size and cut the chip captions and buttons short.
        SuspendLayout();
        Text = "Gecko Peeks " + version;
        Font = Theme.Body;
        BackColor = Theme.Jungle;
        ForeColor = Theme.Cream;
        StartPosition = FormStartPosition.CenterScreen;
        // Small enough to fit a 1080p screen at 150% scaling (FitToWorkingArea trims the rest).
        MinimumSize = new Size(880, 640);
        Size = new Size(1200, 860);
        Icon = GeckoMark.CreateIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Jungle,
            Padding = new Padding(12, 8, 12, 8),
        };
        // Compact chrome up top so traceroute + recent log own the window. The status panel and
        // the LOCAL LOG strip size themselves from their fonts, so nothing in them is clipped.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(version), 0, 0);
        root.Controls.Add(BuildStatus(out _modeValue, out _loggingValue, out _leagueValue, out _nextValue, out _phaseValue, out _activityValue), 0, 1);
        root.Controls.Add(BuildLogStrip(out _logPathValue, out _logStatusValue), 0, 2);

        _hopsGrid = MakeGrid();
        _hopsGrid.Columns.Add("Hop", "#");
        _hopsGrid.Columns.Add("IP", "IP");
        _hopsGrid.Columns.Add("Hostname", "Hostname");
        _hopsGrid.Columns.Add("Rtt", "RTT");
        _hopsGrid.Columns.Add("Network", "Network");
        StyleNumberColumn(_hopsGrid.Columns["Rtt"]);
        FillColumn(_hopsGrid.Columns["Hostname"], 160);
        _hopsEmpty = EmptyLabel("No traceroute yet. The first test will fill this in.");
        _hopsHint = new Label
        {
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            Text = "",
        };

        _summaryGrid = MakeGrid();
        _summaryGrid.Columns.Add("Upstream", "Upstream");
        _summaryGrid.Columns.Add("Share", "Share");
        _summaryGrid.Columns.Add("LastHop", "Last hop ms");
        _summaryGrid.Columns.Add("Riot", "Riot ICMP");
        StyleNumberColumn(_summaryGrid.Columns["LastHop"]);
        StyleNumberColumn(_summaryGrid.Columns["Riot"]);
        FillColumn(_summaryGrid.Columns["Upstream"], 80);
        _summaryEmpty = EmptyLabel("No samples yet.");
        _summaryHint = new Label
        {
            ForeColor = Theme.CreamMuted,
            BackColor = Theme.Charcoal,
            Font = Theme.Small,
            Padding = new Padding(6, 2, 6, 2),
            Text = "Last hop ms = ping to the last router that answered the traceroute "
                + "(path latency, not in-game ping). Riot ICMP \"no reply\" = Riot filters ping.",
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Jungle,
            Margin = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        body.Controls.Add(Section("Latest traceroute", Wrap(_hopsGrid, _hopsEmpty, _hopsHint)), 0, 0);
        body.Controls.Add(Section("Upstream summary", Wrap(_summaryGrid, _summaryEmpty, _summaryHint)), 1, 0);

        _logGrid = MakeGrid();
        _logGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        foreach (var column in CsvFormat.Columns)
            _logGrid.Columns.Add(column, LatencyLabels.ColumnHeader(column));
        ConfigureLogColumns(_logGrid);
        _logEmpty = EmptyLabel("No rows yet. A finished test appends the first line to the log.");

        // Traceroute above, recent log below, with a divider the user can drag.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Jungle,
            SplitterWidth = 6,
            TabStop = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        // Keeps the log's right edge level with the Upstream summary, which has the same margin.
        _split.Panel2.Padding = new Padding(0, 0, 8, 0);
        _split.Panel1.Controls.Add(body);
        _split.Panel2.Controls.Add(Section("Recent log (last 50)", Wrap(_logGrid, _logEmpty, null)));
        root.Controls.Add(_split, 0, 3);

        Controls.Add(root);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        ApplyGridMetrics();
        PlaceSplitter();

        _scheduler.Updated += OnUpdated;
        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => ApplyClock();
        _clock.Start();
        OnUpdated(_scheduler.Capture());
    }

    /// <summary>Raised when the window closes to the tray (the app keeps logging).</summary>
    public event Action? HiddenToTray;

    /// <summary>
    /// Tray click / double-click, tray Open window, and a second launch all land here.
    /// ShowInTaskbar stays true: toggling it recreates the window handle, and a hidden
    /// window has no taskbar button anyway.
    /// </summary>
    public void ShowFromTray()
    {
        if (IsDisposed)
            return;

        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        // Activate() can lose to the foreground lock and leave the window behind other
        // apps. A TopMost toggle still raises it to the top of the z-order.
        TopMost = true;
        TopMost = false;
        Activate();
        BindLog();
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        _clock.Stop();
        Close();
    }

    public void OpenLog()
    {
        var path = _scheduler.Log.MainPath;
        if (!File.Exists(path))
        {
            OpenLogFolder();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            OpenLogFolder();
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_scheduler.Paths.Folder);
            Process.Start(new ProcessStartInfo(_scheduler.Paths.Folder) { UseShellExecute = true });
        }
        catch
        {
            ShowFromTray();
        }
    }

    public void CopyLogPath()
    {
        try
        {
            Clipboard.SetText(_scheduler.Paths.Folder);
            _logStatusValue.Text = "Copied folder path to clipboard.";
            _logStatusValue.ForeColor = Theme.Leaf;
        }
        catch
        {
            _logStatusValue.Text = "Could not copy the folder path.";
            _logStatusValue.ForeColor = Theme.Coral;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkingArea();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Moving to a monitor with another scale rescales the controls, not the grids' internals.
        ApplyGridMetrics();
    }

    /// <summary>
    /// One line of a font in 96-DPI pixels, plus a pixel so descenders never touch the edge.
    /// Heights built from this scale in the DPI pass together with everything else, while the
    /// fonts scale on their own, so text and the boxes it sits in grow at the same rate.
    /// </summary>
    private static int LineHeight(Font font) => (int)Math.Ceiling(font.GetHeight(96f)) + 1;

    /// <summary>
    /// Row and header heights are device pixels the DPI pass does not touch, so they come from
    /// the body font at the window's DPI (28 and 32 pixels at 100%).
    /// </summary>
    private void ApplyGridMetrics()
    {
        var rowHeight = LogicalToDeviceUnits(LineHeight(Theme.Body) + 7);
        var headerHeight = LogicalToDeviceUnits(LineHeight(Theme.Body) + 11);
        foreach (var grid in new[] { _hopsGrid, _summaryGrid, _logGrid })
        {
            grid.ColumnHeadersHeight = headerHeight;
            grid.RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in grid.Rows)
                row.Height = rowHeight;
        }
    }

    /// <summary>
    /// The traceroute starts with the larger share; the divider stays draggable and keeps its
    /// ratio when the window resizes. Needs the container's real height, because the panel
    /// minimums are checked against it.
    /// </summary>
    private void PlaceSplitter()
    {
        var minHops = LogicalToDeviceUnits(140);
        var minLog = LogicalToDeviceUnits(120);
        var usable = _split.Height - _split.SplitterWidth;
        if (usable <= minHops + minLog)
            return;

        _split.Panel1MinSize = minHops;
        _split.Panel2MinSize = minLog;
        _split.SplitterDistance = Math.Clamp((int)(usable * 0.58), minHops, usable - minLog);
    }

    /// <summary>
    /// Close (X) hides to the tray. Minimize stays on the taskbar like any window. A bare
    /// WM_CLOSE from outside the app arrives as TaskManagerClosing; it hides too, because a
    /// disposed form would leave tray Open window with nothing to show.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested
            && e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing or CloseReason.None)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }

        base.OnFormClosing(e);
    }

    private void FitToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        // A 768-pixel laptop screen at 125% is shorter than the scaled minimum height. Let the
        // window shrink to the screen instead of running off the bottom behind the taskbar.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        if (width == Width && height == Height && area.Contains(Bounds))
            return;

        Bounds = new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    private void OnUpdated(ProbeSnapshot snapshot)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action<ProbeSnapshot>(OnUpdated), snapshot);
            return;
        }

        ApplyStatus(snapshot);
        ApplyLogStrip(snapshot);
        if (snapshot.HopsRevision != _hopsRevision)
        {
            _hopsRevision = snapshot.HopsRevision;
            BindHops(snapshot);
        }

        if (snapshot.LogRevision != _logRevision)
        {
            _logRevision = snapshot.LogRevision;
            // Skip full CSV parse while hidden; refresh when the window is shown.
            if (Visible)
                BindLog();
        }
    }

    private void ApplyClock()
    {
        if (!Visible)
            return;
        // Per-second tick: only the countdown changes every second. Full status
        // (and the activity ListBox) refresh on scheduler Updated events.
        var snapshot = _scheduler.Capture();
        _nextValue.Text = Countdown(snapshot);
        _phaseValue.Text = string.IsNullOrWhiteSpace(snapshot.Phase) ? "Idle" : snapshot.Phase;
        _phaseValue.ForeColor = PhaseColor(snapshot);
    }

    private void ApplyStatus(ProbeSnapshot snapshot)
    {
        _modeValue.Text = snapshot.Mode;
        var (logging, loggingTone) = LogStatusText.LoggingChip(snapshot.Paused, snapshot.LogHealth);
        _loggingValue.Text = logging;
        _loggingValue.ForeColor = ToneColor(loggingTone);
        _leagueValue.Text = snapshot.LeagueRunning ? "Running" : "Not running";
        _leagueValue.ForeColor = snapshot.LeagueRunning ? Theme.Amber : Theme.Cream;
        _nextValue.Text = Countdown(snapshot);
        _phaseValue.Text = string.IsNullOrWhiteSpace(snapshot.Phase) ? "Idle" : snapshot.Phase;
        _phaseValue.ForeColor = PhaseColor(snapshot);
        _activityValue.Text = string.IsNullOrWhiteSpace(snapshot.ActivityLine)
            ? "No activity yet."
            : snapshot.ActivityLine;
    }

    private void ApplyLogStrip(ProbeSnapshot snapshot)
    {
        _logPathValue.Text = string.IsNullOrWhiteSpace(snapshot.LogFolder)
            ? "\u2014"
            : Path.Combine(snapshot.LogFolder, snapshot.LogFileName);

        var (status, tone) = LogStatusText.StripLine(
            snapshot.LogHealth,
            snapshot.LogWriteStatus,
            snapshot.ReadProblem,
            snapshot.LogLocation,
            snapshot.LogUnsavedRows);
        _logStatusValue.Text = status;
        _logStatusValue.ForeColor = ToneColor(tone);
    }

    private static Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Good => Theme.Leaf,
        StatusTone.Warn => Theme.Amber,
        StatusTone.Bad => Theme.Coral,
        _ => Theme.Cream,
    };

    private void BindHops(ProbeSnapshot snapshot)
    {
        _hopsGrid.Rows.Clear();
        foreach (var hop in snapshot.Hops)
        {
            _hopsGrid.Rows.Add(
                hop.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                hop.Responded ? hop.Ip : "*",
                hop.Hostname,
                IcmpTiming.FormatHopRtt(hop.Responded, hop.RttMs),
                RouteClassifier.DescribeHop(hop.Hostname));
        }

        // Adding rows selects the first one; a highlighted row the user never picked reads as a bug.
        _hopsGrid.ClearSelection();
        var empty = snapshot.Hops.Count == 0;
        _hopsEmpty.Visible = empty;
        _hopsGrid.Visible = !empty;
        _hopsHint.Text = snapshot.HopsAreAddressesOnly
            ? "IPs restored from the log (no per-hop RTT in CSV). Hostnames and RTTs return on the next test."
            : "";
    }

    private void BindLog()
    {
        IReadOnlyList<RouteRow> rows;
        try
        {
            rows = _scheduler.Log.ReadAll();
        }
        catch (Exception ex)
        {
            rows = Array.Empty<RouteRow>();
            _logEmpty.Text = "Could not read the log: " + ex.Message;
        }

        var problem = _scheduler.Log.ReadProblem;
        _logGrid.Rows.Clear();
        var start = Math.Max(0, rows.Count - 50);
        for (var i = rows.Count - 1; i >= start; i--)
        {
            var row = rows[i];
            var riot = LatencyLabels.RiotCell(row.RiotHopAvg, row.RiotHopLossPct);
            var index = _logGrid.Rows.Add(
                row.Timestamp,
                row.LeagueRunning,
                row.Upstream,
                row.HandoffNetwork,
                row.HandoffHost,
                row.HandoffIP,
                row.HandoffCity,
                row.HandoffRttMs,
                row.LastHopIP,
                row.LastHopHost,
                row.LastHopMin,
                row.LastHopAvg,
                row.LastHopMax,
                row.LastHopLossPct,
                riot,
                row.RiotHopLossPct,
                row.FullPath);
            if (riot == LatencyLabels.NoReply)
                _logGrid.Rows[index].Cells["RiotHopAvg"].Style.ForeColor = Theme.CreamMuted;
        }

        _logGrid.ClearSelection();
        var noRows = rows.Count == 0;
        _logEmpty.Visible = noRows;
        _logGrid.Visible = !noRows;
        if (noRows && !string.IsNullOrWhiteSpace(problem))
            _logEmpty.Text = problem;
        else if (noRows)
            _logEmpty.Text = "No rows yet. A finished test appends the first line to the log.";

        BindSummary(rows, problem);
    }

    private void BindSummary(IReadOnlyList<RouteRow> rows, string? problem)
    {
        _summaryGrid.Rows.Clear();
        if (rows.Count == 0)
        {
            _summaryEmpty.Visible = true;
            _summaryGrid.Visible = false;
            _summaryEmpty.Text = string.IsNullOrWhiteSpace(problem)
                ? "No samples yet."
                : problem;
            return;
        }

        foreach (var row in UpstreamSummary.Build(rows))
        {
            var index = _summaryGrid.Rows.Add(
                row.Upstream,
                UpstreamSummary.SharePercent(row.Count, rows.Count),
                row.LastHopMs,
                row.RiotMs);
            if (row.RiotMs == LatencyLabels.NoReply)
                _summaryGrid.Rows[index].Cells["Riot"].Style.ForeColor = Theme.CreamMuted;
        }

        _summaryGrid.ClearSelection();
        _summaryEmpty.Visible = false;
        _summaryGrid.Visible = true;
    }

    private static string Countdown(ProbeSnapshot snapshot)
    {
        if (snapshot.Paused && !snapshot.TestRunning)
            return "Paused";
        if (snapshot.TestRunning)
            return "Running now";
        if (snapshot.NextDueLocal == null)
            return "\u2014";

        var delta = snapshot.NextDueLocal.Value - DateTime.Now;
        if (delta <= TimeSpan.Zero)
            return "Due now";
        if (delta.TotalHours >= 1)
            return $"{(int)delta.TotalHours} h {delta.Minutes:00} m";
        if (delta.TotalMinutes >= 1)
            return $"{(int)delta.TotalMinutes} min {delta.Seconds:00} s";
        return $"{Math.Max(0, delta.Seconds)} s";
    }

    private static Color PhaseColor(ProbeSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LastError) || snapshot.Phase.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || snapshot.Phase.StartsWith("Last test problem", StringComparison.Ordinal))
            return Theme.Coral;
        if (snapshot.TestRunning)
            return Theme.Amber;
        return Theme.Cream;
    }

    private static string FormatVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
            return "v?";
        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Control BuildHeader(string version)
    {
        // Table layout + GDI/ClearType labels avoid the AutoSize glyph overlap seen at 125%/150% DPI.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Jungle,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));

        var mark = new PictureBox
        {
            Image = GeckoMark.CreateBitmap(56),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            BackColor = Theme.Jungle,
            Margin = new Padding(0, 4, 8, 4),
        };
        header.Controls.Add(mark, 0, 0);
        header.SetRowSpan(mark, 2);

        var title = new Label
        {
            Text = "Gecko Peeks  " + version,
            Font = Theme.Title,
            ForeColor = Theme.Cream,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            AutoSize = false,
            UseCompatibleTextRendering = false,
            Margin = new Padding(0),
            // Bottom-aligned text otherwise sits on the label's last pixel row.
            Padding = new Padding(0, 0, 0, 2),
        };
        var subtitle = new Label
        {
            Text = "Traceroute to Riot. Close (X) keeps logging in the tray: click the gecko tray icon "
                + "or right-click > Open window to come back. Exit quits.",
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            AutoSize = false,
            UseCompatibleTextRendering = false,
            Margin = new Padding(0, 2, 0, 0),
        };
        header.Controls.Add(title, 1, 0);
        header.Controls.Add(subtitle, 1, 1);
        return header;
    }

    /// <summary>
    /// Title + buttons, the full CSV path, then one status line with room for two lines
    /// of text (a long Controlled folder access error must not be clipped away).
    /// Every row here is sized from its content. The old fixed 30-pixel title row measured its
    /// non-AutoSize button panel at the 200x100 default, so the inner row grew to 100 pixels:
    /// the caption was pushed out of view and the buttons lost their bottom edge.
    /// </summary>
    private Control BuildLogStrip(out Label path, out Label status)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Panel,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 4, 0, 0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Small) + 5));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "LOCAL LOG (CSV)",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Amber,
            Font = Theme.Section,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0),
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        buttons.Controls.Add(MakeStripButton("Copy path", (_, _) => CopyLogPath()));
        buttons.Controls.Add(MakeStripButton("Open folder", (_, _) => OpenLogFolder()));
        buttons.Controls.Add(MakeStripButton("Open log", (_, _) => OpenLog()));
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(buttons, 1, 0);

        path = StripLine(out var pathHost, "File");
        status = new Label
        {
            Dock = DockStyle.Fill,
            // Two lines of body text. The auto-sized row reserves exactly this height.
            Height = 2 * LineHeight(Theme.Body) + 2,
            ForeColor = Theme.Amber,
            Font = Theme.Body,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 2, 0, 0),
            Text = "Waiting for first write",
        };

        panel.Controls.Add(titleRow, 0, 0);
        panel.Controls.Add(pathHost, 0, 1);
        panel.Controls.Add(status, 0, 2);
        return panel;
    }

    private static Label StripLine(out Control host, string caption)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        var cap = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        var value = new Label
        {
            Text = "\u2014",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Cream,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
        };
        row.Controls.Add(cap, 0, 0);
        row.Controls.Add(value, 1, 0);
        host = row;
        return value;
    }

    private static Button MakeStripButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.JungleDeep,
            ForeColor = Theme.Cream,
            Font = Theme.Small,
            Margin = new Padding(4, 0, 0, 0),
            Padding = new Padding(10, 2, 10, 2),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Theme.Line;
        button.FlatAppearance.MouseOverBackColor = Theme.Jungle;
        button.Click += onClick;
        return button;
    }

    private static Control BuildStatus(
        out Label mode,
        out Label logging,
        out Label league,
        out Label next,
        out Label phase,
        out Label activity)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Theme.Charcoal,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0),
        };
        for (var i = 0; i < 4; i++)
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        // Chips, then the phase line, then the activity line: each row is as tall as its font needs.
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, ChipHeight));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Body) + 3));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Body) + 5));

        panel.Controls.Add(Chip("Schedule", out mode), 0, 0);
        panel.Controls.Add(Chip("Logging", out logging), 1, 0);
        panel.Controls.Add(Chip("League", out league), 2, 0);
        panel.Controls.Add(Chip("Next test", out next), 3, 0);

        phase = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Cream,
            Font = Theme.Body,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Idle",
            Margin = new Padding(0),
        };
        panel.Controls.Add(phase, 0, 1);
        panel.SetColumnSpan(phase, 4);

        // Latest activity only; the full history is in activity.log beside the CSV.
        activity = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Cream,
            Font = Theme.Body,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Text = "No activity yet.",
        };
        panel.Controls.Add(activity, 0, 2);
        panel.SetColumnSpan(activity, 4);
        return panel;
    }

    /// <summary>A small caption line over a body-font value line.</summary>
    private static int ChipHeight => LineHeight(Theme.Small) + LineHeight(Theme.Body) + 3;

    private static Control Chip(string caption, out Label value)
    {
        var stack = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), BackColor = Theme.Charcoal };
        var cap = new Label
        {
            Text = caption.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = LineHeight(Theme.Small),
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
        };
        value = new Label
        {
            Text = "\u2014",
            Dock = DockStyle.Top,
            Height = LineHeight(Theme.Body) + 3,
            ForeColor = Theme.Cream,
            Font = Theme.Body,
        };
        stack.Controls.Add(value);
        stack.Controls.Add(cap);
        return stack;
    }

    private static Control Section(string title, Control body)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Jungle, Margin = new Padding(0, 0, 8, 0) };
        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = LineHeight(Theme.Section) + 1,
            ForeColor = Theme.Cream,
            Font = Theme.Section,
        };
        body.Dock = DockStyle.Fill;
        panel.Controls.Add(body);
        panel.Controls.Add(label);
        return panel;
    }

    private static Control Wrap(DataGridView grid, Label empty, Label? hint)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Charcoal, Padding = new Padding(1) };
        grid.Dock = DockStyle.Fill;
        empty.Dock = DockStyle.Fill;
        host.Controls.Add(grid);
        host.Controls.Add(empty);
        if (hint != null)
        {
            // Docked at the bottom, as tall as its text wraps to at the panel's width. Not
            // AutoSize: an auto-sized label takes its own unwrapped preferred size and ignores
            // the dock width, which left this a one-line sliver at 125%.
            hint.Dock = DockStyle.Bottom;
            hint.AutoSize = false;
            host.Layout += (_, _) => FitHint(host, hint);
            hint.TextChanged += (_, _) => host.PerformLayout();
            host.Controls.Add(hint);
        }

        return host;
    }

    /// <summary>
    /// Runs before the host docks its children, so the dock pass uses the measured height. An
    /// empty hint takes no room. The measurement is in the window's device pixels, like the
    /// text it is for.
    /// </summary>
    private static void FitHint(Panel host, Label hint)
    {
        if (string.IsNullOrEmpty(hint.Text))
        {
            hint.Height = 0;
            return;
        }

        var width = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal);
        hint.Height = hint.GetPreferredSize(new Size(width, 0)).Height;
    }

    private static Label EmptyLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.CreamMuted,
        BackColor = Theme.Charcoal,
        Font = Theme.Body,
        Visible = false,
    };

    private static DataGridView MakeGrid()
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = Theme.Charcoal,
            BorderStyle = BorderStyle.None,
            GridColor = Theme.Line,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            // Row and header heights: ApplyGridMetrics, from the font at the window's DPI.
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.Both,
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Amber,
            Font = Theme.Body,
            SelectionBackColor = Theme.Panel,
            SelectionForeColor = Theme.Amber,
            Padding = new Padding(6, 0, 6, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.Cream,
            SelectionBackColor = Theme.Jungle,
            SelectionForeColor = Theme.Cream,
            Font = Theme.Body,
            Padding = new Padding(6, 0, 6, 0),
        };
        // Only the background alternates. A font here would outrank the number columns' bold
        // font (row styles win over column styles), making every other row's numbers regular.
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Panel };
        return grid;
    }

    private void StyleNumberColumn(DataGridViewColumn? column)
    {
        if (column == null)
            return;
        column.DefaultCellStyle = new DataGridViewCellStyle(column.DefaultCellStyle)
        {
            Font = Theme.GridNumber,
            ForeColor = Theme.Cream,
            Alignment = DataGridViewContentAlignment.MiddleRight,
        };
        column.MinimumWidth = LogicalToDeviceUnits(64);
    }

    /// <summary>
    /// The column that takes the width the others leave over, so the grid runs up to its
    /// scrollbar instead of ending in an empty band.
    /// </summary>
    private void FillColumn(DataGridViewColumn? column, int minimumWidth)
    {
        if (column == null)
            return;
        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        column.MinimumWidth = LogicalToDeviceUnits(minimumWidth);
    }

    /// <summary>
    /// Puts Timestamp / Upstream / city / last-hop latency up front, freezes the first two,
    /// and keeps horizontal scroll for the long FullPath tail. Riot ICMP sits after the
    /// path numbers because Riot usually filters ping.
    /// </summary>
    private void ConfigureLogColumns(DataGridView grid)
    {
        var front = new[]
        {
            "Timestamp",
            "Upstream",
            "HandoffCity",
            "LastHopAvg",
            "LastHopLossPct",
            "HandoffRttMs",
            "RiotHopAvg",
            "RiotHopLossPct",
            "LeagueRunning",
            "HandoffNetwork",
            "HandoffHost",
            "HandoffIP",
            "LastHopIP",
            "LastHopHost",
            "LastHopMin",
            "LastHopMax",
            "FullPath",
        };

        var order = 0;
        foreach (var name in front)
        {
            if (!grid.Columns.Contains(name))
                continue;
            var column = grid.Columns[name];
            column.DisplayIndex = order++;
            column.AutoSizeMode = name == "FullPath"
                ? DataGridViewAutoSizeColumnMode.None
                : DataGridViewAutoSizeColumnMode.DisplayedCells;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        if (grid.Columns.Contains("Timestamp"))
        {
            grid.Columns["Timestamp"].Frozen = true;
            grid.Columns["Timestamp"].MinimumWidth = LogicalToDeviceUnits(140);
        }

        if (grid.Columns.Contains("Upstream"))
        {
            grid.Columns["Upstream"].Frozen = true;
            grid.Columns["Upstream"].MinimumWidth = LogicalToDeviceUnits(72);
        }

        foreach (var name in new[] { "HandoffRttMs", "LastHopAvg", "RiotHopAvg" })
        {
            if (grid.Columns.Contains(name))
                StyleNumberColumn(grid.Columns[name]);
        }

        if (grid.Columns.Contains("FullPath"))
        {
            grid.Columns["FullPath"].MinimumWidth = LogicalToDeviceUnits(220);
            grid.Columns["FullPath"].Width = LogicalToDeviceUnits(320);
        }

        grid.ScrollBars = ScrollBars.Both;
    }
}
