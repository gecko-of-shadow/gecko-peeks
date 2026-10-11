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
    private bool _exitRequested;
    private int _hopsRevision = -1;
    private int _logRevision = -1;

    public MainForm(ProbeScheduler scheduler)
    {
        _scheduler = scheduler;
        var version = FormatVersion();
        Text = "Gecko Peeks " + version;
        Font = Theme.Body;
        BackColor = Theme.Jungle;
        ForeColor = Theme.Cream;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        // Small enough to fit a 1080p screen at 150% scaling.
        MinimumSize = new Size(880, 640);
        Size = new Size(1200, 860);
        Icon = GeckoMark.CreateIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Jungle,
            Padding = new Padding(12, 8, 12, 8),
        };
        // Compact chrome up top so traceroute + recent log own the window.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

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
        _hopsEmpty = EmptyLabel("No traceroute yet. The first test will fill this in.");
        _hopsHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
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
        _summaryEmpty = EmptyLabel("No samples yet.");
        _summaryHint = new Label
        {
            Height = 56,
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
            Margin = new Padding(0, 4, 0, 0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        body.Controls.Add(Section("Latest traceroute", Wrap(_hopsGrid, _hopsEmpty, _hopsHint)), 0, 0);
        body.Controls.Add(Section("Upstream summary", Wrap(_summaryGrid, _summaryEmpty, _summaryHint)), 1, 0);
        root.Controls.Add(body, 0, 3);

        _logGrid = MakeGrid();
        _logGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        foreach (var column in CsvFormat.Columns)
            _logGrid.Columns.Add(column, LatencyLabels.ColumnHeader(column));
        ConfigureLogColumns(_logGrid);
        _logEmpty = EmptyLabel("No rows yet. A finished test appends the first line to the log.");
        root.Controls.Add(Section("Recent log (last 50)", Wrap(_logGrid, _logEmpty, null)), 0, 4);

        Controls.Add(root);
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
    /// </summary>
    private Control BuildLogStrip(out Label path, out Label status)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Panel,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 4, 0, 0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        var title = new Label
        {
            Text = "LOCAL LOG (CSV)",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Amber,
            Font = Theme.Section,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
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
        };
        var value = new Label
        {
            Text = "\u2014",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Cream,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
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
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Theme.Charcoal,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0),
        };
        for (var i = 0; i < 4; i++)
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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

    private static Control Chip(string caption, out Label value)
    {
        var stack = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), BackColor = Theme.Charcoal };
        var cap = new Label
        {
            Text = caption.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = 16,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
        };
        value = new Label
        {
            Text = "\u2014",
            Dock = DockStyle.Top,
            Height = 24,
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
            Height = 24,
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
            hint.Dock = DockStyle.Bottom;
            host.Controls.Add(hint);
        }

        return host;
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
            ColumnHeadersHeight = 32,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.Both,
        };
        grid.RowTemplate.Height = 28;
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
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Cream,
            SelectionBackColor = Theme.Jungle,
            SelectionForeColor = Theme.Cream,
            Font = Theme.Body,
        };
        return grid;
    }

    private static void StyleNumberColumn(DataGridViewColumn? column)
    {
        if (column == null)
            return;
        column.DefaultCellStyle = new DataGridViewCellStyle(column.DefaultCellStyle)
        {
            Font = Theme.GridNumber,
            ForeColor = Theme.Cream,
            Alignment = DataGridViewContentAlignment.MiddleRight,
        };
        column.MinimumWidth = 64;
    }

    /// <summary>
    /// Puts Timestamp / Upstream / city / last-hop latency up front, freezes the first two,
    /// and keeps horizontal scroll for the long FullPath tail. Riot ICMP sits after the
    /// path numbers because Riot usually filters ping.
    /// </summary>
    private static void ConfigureLogColumns(DataGridView grid)
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
            grid.Columns["Timestamp"].MinimumWidth = 140;
        }

        if (grid.Columns.Contains("Upstream"))
        {
            grid.Columns["Upstream"].Frozen = true;
            grid.Columns["Upstream"].MinimumWidth = 72;
        }

        foreach (var name in new[] { "HandoffRttMs", "LastHopAvg", "RiotHopAvg" })
        {
            if (grid.Columns.Contains(name))
                StyleNumberColumn(grid.Columns[name]);
        }

        if (grid.Columns.Contains("FullPath"))
        {
            grid.Columns["FullPath"].MinimumWidth = 220;
            grid.Columns["FullPath"].Width = 320;
        }

        grid.ScrollBars = ScrollBars.Both;
    }
}
