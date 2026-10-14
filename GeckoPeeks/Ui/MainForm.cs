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
    private readonly DataGridView _historyGrid;
    private readonly Label _hopsEmpty;
    private readonly Label _historyEmpty;
    private readonly Label _loggingValue;
    private readonly Label _rhythmValue;
    private readonly Label _routeValue;
    private readonly Label _lastHopValue;
    private readonly Label _riotValue;
    private readonly Label _contextValue;
    private readonly Label _changeValue;
    private readonly Label _copyNote;
    private readonly Label _summaryValue;
    private readonly TableLayoutPanel _routeTable;
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
        // Short enough for a 864-tall laptop. FitToWorkingArea trims anything still past the taskbar.
        MinimumSize = new Size(920, 600);
        Size = new Size(1080, 700);
        Icon = GeckoMark.CreateIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Jungle,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildMasthead(version, out _loggingValue, out _rhythmValue), 0, 0);

        _hopsGrid = MakeGrid();
        _hopsGrid.Columns.Add("Hop", "#");
        _hopsGrid.Columns.Add("IP", "IP");
        _hopsGrid.Columns.Add("Hostname", "Hostname");
        _hopsGrid.Columns.Add("Rtt", "RTT");
        _hopsGrid.Columns.Add("Network", "Network");
        StyleNumberColumn(_hopsGrid.Columns["Rtt"]);
        FillColumn(_hopsGrid.Columns["Hostname"], 180);
        _hopsEmpty = EmptyLabel("The first test fills this in.");
        _hopsHint = new Label
        {
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            Text = "",
        };

        _historyGrid = MakeGrid();
        _historyGrid.Columns.Add("Time", "Time");
        _historyGrid.Columns.Add("Route", "Route");
        _historyGrid.Columns.Add("LastHop", "Last hop");
        _historyGrid.Columns.Add("Riot", "Riot");
        StyleNumberColumn(_historyGrid.Columns["LastHop"]);
        StyleNumberColumn(_historyGrid.Columns["Riot"]);
        FillColumn(_historyGrid.Columns["Route"], 160);
        _historyEmpty = EmptyLabel("No tests yet.");

        var stage = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Charcoal,
            Padding = new Padding(16, 14, 16, 10),
            Margin = new Padding(0, 4, 0, 8),
        };
        stage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stage.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        stage.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        stage.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
        // Three recent rows. The path list takes whatever height is left.
        stage.RowStyles.Add(new RowStyle(SizeType.Absolute, GridHeaderLogical() + 3 * GridRowLogical() + 8));
        stage.Controls.Add(BuildRouteCard(out _routeTable, out _routeValue, out _lastHopValue, out _riotValue, out _contextValue, out _changeValue, out _copyNote), 0, 0);
        stage.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BackColor = Theme.Line,
            Margin = new Padding(0, 8, 0, 8),
        }, 0, 1);
        stage.Controls.Add(BuildList("Path", Wrap(_hopsGrid, _hopsEmpty, _hopsHint), out _summaryValue), 0, 2);
        stage.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BackColor = Theme.Line,
            Margin = new Padding(0, 4, 0, 4),
        }, 0, 3);
        stage.Controls.Add(BuildList(null, Wrap(_historyGrid, _historyEmpty, null), out _), 0, 4);
        root.Controls.Add(stage, 0, 1);
        root.Controls.Add(BuildLogStrip(out _logPathValue, out _logStatusValue), 0, 2);

        Controls.Add(root);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        ApplyGridMetrics();

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
        BindHistory();
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

    public void CopyDiagnosis()
    {
        var snapshot = _scheduler.Capture();
        var hops = new List<DiagnosisHop>(snapshot.Hops.Count);
        foreach (var hop in snapshot.Hops)
            hops.Add(new DiagnosisHop(hop.Number, hop.Responded ? hop.Ip : "*", hop.Hostname));

        var text = DiagnosisText.Format(new DiagnosisInput(
            VersionNumber(),
            snapshot.HasRoute,
            snapshot.TestTimestamp,
            snapshot.TestUpstream,
            snapshot.TestHandoffNetwork,
            snapshot.TestHandoffCity,
            snapshot.TestLastHopAvg,
            snapshot.TestLastHopLoss,
            snapshot.TestRiotAvg,
            snapshot.TestRiotLoss,
            snapshot.TestLeague,
            snapshot.TestAdapter,
            snapshot.TestWifi,
            snapshot.TestGateway,
            snapshot.TestVpn,
            snapshot.ChangeLine,
            hops));

        try
        {
            Clipboard.SetText(text);
            _copyNote.Text = "Copied.";
            _copyNote.ForeColor = Theme.Leaf;
        }
        catch
        {
            _copyNote.Text = "Could not copy.";
            _copyNote.ForeColor = Theme.Coral;
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

    private static int ListHeaderLogical() => LineHeight(Theme.Small) + 4;

    private static int GridHeaderLogical() => LineHeight(Theme.Small) + 8;

    private static int GridRowLogical() => LineHeight(Theme.Small) + 2;

    /// <summary>
    /// Row and header heights are device pixels the DPI pass does not touch, so they come from
    /// the grid font at the window's DPI.
    /// </summary>
    private void ApplyGridMetrics()
    {
        var rowHeight = LogicalToDeviceUnits(GridRowLogical());
        var headerHeight = LogicalToDeviceUnits(GridHeaderLogical());
        foreach (var grid in new[] { _hopsGrid, _historyGrid })
        {
            grid.ColumnHeadersHeight = headerHeight;
            grid.RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in grid.Rows)
                row.Height = rowHeight;
        }

        SizeColumn(_hopsGrid.Columns["Hop"], 44);
        SizeColumn(_hopsGrid.Columns["IP"], 148);
        SizeColumn(_hopsGrid.Columns["Rtt"], 72);
        SizeColumn(_hopsGrid.Columns["Network"], 108);
        SizeColumn(_historyGrid.Columns["Time"], 168);
        SizeColumn(_historyGrid.Columns["LastHop"], 96);
        SizeColumn(_historyGrid.Columns["Riot"], 96);
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

        ApplyMasthead(snapshot);
        ApplyRouteCard(snapshot);
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
                BindHistory();
        }
    }

    private void ApplyClock()
    {
        if (!Visible)
            return;
        // Per-second tick: only the countdown changes every second. Full status
        // (and the activity ListBox) refresh on scheduler Updated events.
        ApplyMasthead(_scheduler.Capture());
    }

    private void ApplyMasthead(ProbeSnapshot snapshot)
    {
        var (logging, loggingTone) = LogStatusText.LoggingChip(snapshot.Paused, snapshot.LogHealth);
        _loggingValue.Text = logging;
        _loggingValue.ForeColor = ToneColor(loggingTone);
        _rhythmValue.Text = Rhythm(snapshot);
        _rhythmValue.ForeColor = snapshot.TestRunning || !string.IsNullOrWhiteSpace(snapshot.LastError)
            || snapshot.Phase.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || snapshot.Phase.StartsWith("Last test problem", StringComparison.Ordinal)
            ? PhaseColor(snapshot)
            : Theme.CreamMuted;
    }

    private void ApplyRouteCard(ProbeSnapshot snapshot)
    {
        _routeValue.Text = string.IsNullOrWhiteSpace(snapshot.RouteLine) ? "No route yet" : snapshot.RouteLine;
        _lastHopValue.Text = string.IsNullOrWhiteSpace(snapshot.LastHopFigure) ? "\u2014" : snapshot.LastHopFigure;
        _lastHopValue.ForeColor = snapshot.LastHopFigure == LatencyLabels.NoReply ? Theme.CreamMuted : Theme.Cream;
        _riotValue.Text = string.IsNullOrWhiteSpace(snapshot.RiotFigure) ? "\u2014" : snapshot.RiotFigure;
        _riotValue.ForeColor = snapshot.RiotFigure == LatencyLabels.NoReply ? Theme.CreamMuted : Theme.Cream;
        _contextValue.Text = string.IsNullOrWhiteSpace(snapshot.ContextLine)
            ? "The first test fills this in."
            : snapshot.ContextLine;
        var change = snapshot.ChangeLine?.Trim() ?? "";
        _changeValue.Text = change;
        _changeValue.Visible = change.Length > 0;
        _changeValue.ForeColor = snapshot.ChangedHops.Count > 0 ? Theme.Amber : Theme.CreamMuted;
        if (_routeTable.RowStyles.Count > 3)
            _routeTable.RowStyles[3].Height = change.Length > 0 ? LogicalToDeviceUnits(LineHeight(Theme.Small) + 6) : 0;
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
        FitLogStatus();
    }

    /// <summary>
    /// The status row is one line for a normal save and two when the text wraps, which is the
    /// Controlled folder access sentence. Taller than two lines is ellipsized.
    /// </summary>
    private void FitLogStatus()
    {
        if (_logStatusValue == null || _logStatusValue.IsDisposed)
            return;

        var parent = _logStatusValue.Parent;
        var width = (parent?.ClientSize.Width ?? 0) - (parent?.Padding.Horizontal ?? 0);
        if (width < 40)
            width = LogicalToDeviceUnits(640);

        var text = string.IsNullOrWhiteSpace(_logStatusValue.Text) ? " " : _logStatusValue.Text;
        var measured = TextRenderer.MeasureText(
            text,
            _logStatusValue.Font,
            new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var one = LogicalToDeviceUnits(LineHeight(Theme.Body) + 4);
        var two = LogicalToDeviceUnits(2 * LineHeight(Theme.Body) + 6);
        var height = Math.Clamp(measured.Height + LogicalToDeviceUnits(2), one, two);
        if (_logStatusValue.Height == height)
            return;

        _logStatusValue.Height = height;
        parent?.PerformLayout();
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
        var changed = new HashSet<int>(snapshot.ChangedHops);
        _hopsGrid.Rows.Clear();
        foreach (var hop in snapshot.Hops)
        {
            var index = _hopsGrid.Rows.Add(
                hop.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                hop.Responded ? hop.Ip : "*",
                hop.Hostname,
                IcmpTiming.FormatHopRtt(hop.Responded, hop.RttMs),
                RouteClassifier.DescribeHop(hop.Hostname));
            if (!changed.Contains(hop.Number))
                continue;

            foreach (DataGridViewCell cell in _hopsGrid.Rows[index].Cells)
                cell.Style.ForeColor = Theme.Amber;
        }

        // Adding rows selects the first one; a highlighted row the user never picked reads as a bug.
        _hopsGrid.ClearSelection();
        var empty = snapshot.Hops.Count == 0;
        _hopsEmpty.Visible = empty;
        _hopsGrid.Visible = !empty;
        _hopsHint.Text = snapshot.HopsAreAddressesOnly
            ? "IPs restored from the log (no per-hop RTT in CSV). Hostnames and RTTs return on the next test."
            : "";
        ApplyGridMetrics();
    }

    private void BindHistory()
    {
        IReadOnlyList<RouteRow> rows;
        try
        {
            rows = _scheduler.Log.ReadAll();
        }
        catch (Exception ex)
        {
            rows = Array.Empty<RouteRow>();
            _historyEmpty.Text = "Could not read the log: " + ex.Message;
        }

        var problem = _scheduler.Log.ReadProblem;
        _historyGrid.Rows.Clear();
        var start = Math.Max(0, rows.Count - 12);
        for (var i = rows.Count - 1; i >= start; i--)
        {
            var row = rows[i];
            var riot = RouteCardText.RiotFigure(row.RiotHopAvg, row.RiotHopLossPct);
            var index = _historyGrid.Rows.Add(
                row.Timestamp,
                RouteCardText.RouteLine(row.Upstream, row.HandoffNetwork, row.HandoffCity),
                RouteCardText.LastHopFigure(row.LastHopAvg, row.LastHopLossPct),
                riot);
            if (riot == LatencyLabels.NoReply)
                _historyGrid.Rows[index].Cells["Riot"].Style.ForeColor = Theme.CreamMuted;
        }

        _historyGrid.ClearSelection();
        var noRows = rows.Count == 0;
        _historyEmpty.Visible = noRows;
        _historyGrid.Visible = !noRows;
        if (noRows && !string.IsNullOrWhiteSpace(problem))
            _historyEmpty.Text = problem;
        else if (noRows)
            _historyEmpty.Text = "No tests yet.";

        _summaryValue.Text = SummaryLine(rows);
        ApplyGridMetrics();
    }

    /// <summary>One quiet line, not a second grid. Last hop and Riot stay named apart.</summary>
    private static string SummaryLine(IReadOnlyList<RouteRow> rows)
    {
        var built = UpstreamSummary.Build(rows);
        if (built.Count == 0)
            return "";

        var parts = new List<string>();
        foreach (var row in built.Take(3))
        {
            var text = row.Upstream + " " + UpstreamSummary.SharePercent(row.Count, rows.Count);
            if (row.LastHopMs != "\u2014")
                text += " · " + row.LastHopMs + " ms";
            text += row.RiotMs == LatencyLabels.NoReply
                ? " · Riot no reply"
                : " · Riot " + row.RiotMs + " ms";
            parts.Add(text);
        }

        if (built.Count > 3)
            parts.Add("+" + (built.Count - 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join("    ", parts);
    }

    private static string Rhythm(ProbeSnapshot snapshot)
    {
        var phase = string.IsNullOrWhiteSpace(snapshot.Phase) ? "Idle" : snapshot.Phase.Trim();
        if (snapshot.TestRunning || snapshot.Paused)
            return phase;
        if (phase.Equals("Idle", StringComparison.OrdinalIgnoreCase))
            return "Next test " + Countdown(snapshot);
        return "Next test " + Countdown(snapshot) + "  ·  " + phase;
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

    private static string FormatVersion() => "v" + VersionNumber();

    private static string VersionNumber()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
            return "?";
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Control BuildMasthead(string version, out Label logging, out Label rhythm)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Theme.Jungle,
            Margin = new Padding(0, 0, 0, 2),
            Padding = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Section) + 6));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var mark = new PictureBox
        {
            Image = GeckoMark.CreateBitmap(40),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            BackColor = Theme.Jungle,
            Margin = new Padding(0, 2, 10, 2),
        };
        header.Controls.Add(mark, 0, 0);
        header.SetRowSpan(mark, 2);

        var nameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Jungle,
            Margin = new Padding(0),
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        nameRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        nameRow.Controls.Add(new Label
        {
            Text = "Gecko Peeks",
            Font = Theme.Section,
            ForeColor = Theme.Cream,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 10, 0),
            Padding = new Padding(0, 0, 0, 2),
        }, 0, 0);
        nameRow.Controls.Add(new Label
        {
            Text = version,
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 1),
        }, 1, 0);

        rhythm = new Label
        {
            Text = "Next test —",
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 1, 12, 2),
        };
        header.Controls.Add(nameRow, 1, 0);
        header.Controls.Add(rhythm, 1, 1);
        var chip = Chip("Logging", out logging);
        header.Controls.Add(chip, 2, 0);
        header.SetRowSpan(chip, 2);
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
            BackColor = Theme.Jungle,
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
            BackColor = Theme.Jungle,
            Margin = new Padding(0),
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "Log",
            Dock = DockStyle.Fill,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
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
            BackColor = Theme.Jungle,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        buttons.Controls.Add(MakeButton("Copy path", (_, _) => CopyLogPath(), Theme.JungleDeep));
        buttons.Controls.Add(MakeButton("Open folder", (_, _) => OpenLogFolder(), Theme.JungleDeep));
        buttons.Controls.Add(MakeButton("Open log", (_, _) => OpenLog(), Theme.JungleDeep));
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(buttons, 1, 0);

        path = StripLine(out var pathHost, "File");
        status = new Label
        {
            Dock = DockStyle.Fill,
            // One line until the text wraps. FitLogStatus grows it to two for a long status.
            Height = LineHeight(Theme.Body) + 4,
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
        panel.Resize += (_, _) => FitLogStatus();
        return panel;
    }

    private static Label StripLine(out Control host, string caption)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Jungle,
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

    private static Button MakeButton(string text, EventHandler onClick, Color back)
    {
        var textSize = TextRenderer.MeasureText(text, Theme.Small);
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(textSize.Width + 28, LineHeight(Theme.Small) + 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Theme.Cream,
            Font = Theme.Small,
            Margin = new Padding(6, 0, 0, 0),
            Padding = new Padding(8, 1, 8, 1),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Theme.Line;
        button.FlatAppearance.MouseOverBackColor = Theme.Jungle;
        button.Click += onClick;
        return button;
    }

    private Control BuildRouteCard(
        out TableLayoutPanel card,
        out Label route,
        out Label lastHop,
        out Label riot,
        out Label context,
        out Label change,
        out Label copyNote)
    {
        card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Charcoal,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Title) + 8));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Title) + 8));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Small) + 8));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        route = new Label
        {
            Text = "No route yet",
            Dock = DockStyle.Fill,
            Font = Theme.Title,
            ForeColor = Theme.Cream,
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 2),
        };

        var figures = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        figures.Controls.Add(Figure("Last hop", out lastHop));
        figures.Controls.Add(Figure("Riot ICMP", out riot));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0, 0, 0, 2),
            Padding = new Padding(0),
        };
        actions.Controls.Add(MakeButton("Run test now", (_, _) => _scheduler.RunNow(), Theme.Panel));
        actions.Controls.Add(MakeButton("Copy diagnosis", (_, _) => CopyDiagnosis(), Theme.Panel));

        var mid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0),
        };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        mid.Controls.Add(figures, 0, 0);
        mid.Controls.Add(actions, 1, 0);

        var contextRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0),
        };
        contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        contextRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        context = new Label
        {
            Text = "The first test fills this in.",
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 8, 1),
        };
        copyNote = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.Leaf,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 1),
        };
        contextRow.Controls.Add(context, 0, 0);
        contextRow.Controls.Add(copyNote, 1, 0);

        change = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.Amber,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Visible = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 1),
        };

        card.Controls.Add(route, 0, 0);
        card.Controls.Add(mid, 0, 1);
        card.Controls.Add(contextRow, 0, 2);
        card.Controls.Add(change, 0, 3);
        return card;
    }

    private static Control Figure(string caption, out Label value)
    {
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0, 0, 32, 0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, LineHeight(Theme.Title) + 4));
        stack.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            BackColor = Theme.Charcoal,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(0, 0, 0, 2),
        }, 0, 0);
        value = new Label
        {
            Text = "\u2014",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.Cream,
            Font = Theme.Title,
            BackColor = Theme.Charcoal,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 2),
        };
        stack.Controls.Add(value, 1, 0);
        return stack;
    }

    private static Control BuildList(string? title, Control body, out Label aside)
    {
        if (string.IsNullOrEmpty(title))
        {
            aside = new Label { Text = "", Visible = false };
            body.Dock = DockStyle.Fill;
            return body;
        }

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, ListHeaderLogical()));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Charcoal,
            Margin = new Padding(0),
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        head.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 1),
        }, 0, 0);
        aside = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.CreamMuted,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            Margin = new Padding(0),
            Padding = new Padding(8, 0, 0, 1),
        };
        head.Controls.Add(aside, 1, 0);
        body.Dock = DockStyle.Fill;
        panel.Controls.Add(head, 0, 0);
        panel.Controls.Add(body, 0, 1);
        return panel;
    }

    private static Control Chip(string caption, out Label value)
    {
        var stack = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Jungle };
        var cap = new Label
        {
            Text = caption.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = LineHeight(Theme.Small) + 1,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            BackColor = Theme.Jungle,
            Padding = new Padding(0, 0, 0, 1),
        };
        value = new Label
        {
            Text = "\u2014",
            Dock = DockStyle.Top,
            Height = LineHeight(Theme.Body) + 4,
            ForeColor = Theme.Cream,
            Font = Theme.Body,
            BackColor = Theme.Jungle,
            Padding = new Padding(0, 0, 0, 2),
        };
        stack.Controls.Add(value);
        stack.Controls.Add(cap);
        return stack;
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
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            // Row and header heights: ApplyGridMetrics, from the font at the window's DPI.
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.Vertical,
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            SelectionBackColor = Theme.Charcoal,
            SelectionForeColor = Theme.CreamMuted,
            Padding = new Padding(4, 0, 8, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.Cream,
            SelectionBackColor = Color.FromArgb(32, 52, 42),
            SelectionForeColor = Theme.Cream,
            Font = Theme.Small,
            Padding = new Padding(4, 0, 6, 0),
        };
        // Background only. A font here would outrank the number columns' bold font.
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(22, 30, 26),
        };
        return grid;
    }

    private void StyleNumberColumn(DataGridViewColumn? column)
    {
        if (column == null)
            return;
        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
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
    private void SizeColumn(DataGridViewColumn? column, int logicalWidth)
    {
        if (column == null)
            return;
        var width = LogicalToDeviceUnits(logicalWidth);
        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        column.MinimumWidth = width;
        column.Width = width;
    }

    private void FillColumn(DataGridViewColumn? column, int minimumWidth)
    {
        if (column == null)
            return;
        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        column.MinimumWidth = LogicalToDeviceUnits(minimumWidth);
    }
}

