using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;

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
    private readonly Label _modeValue;
    private readonly Label _loggingValue;
    private readonly Label _leagueValue;
    private readonly Label _nextValue;
    private readonly Label _phaseValue;
    private readonly Label _activityValue;
    private readonly ListBox _activityList;
    private readonly Label _hopsHint;
    private readonly System.Windows.Forms.Timer _clock;
    private bool _exitRequested;
    private int _hopsRevision = -1;
    private int _logRevision = -1;

    public MainForm(ProbeScheduler scheduler)
    {
        _scheduler = scheduler;
        Text = "Gecko Peeks";
        Font = Theme.Body;
        BackColor = Theme.Jungle;
        ForeColor = Theme.Cream;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 640);
        Size = new Size(1120, 760);
        ShowInTaskbar = false;
        Icon = GeckoMark.CreateIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Jungle,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatus(out _modeValue, out _loggingValue, out _leagueValue, out _nextValue, out _phaseValue, out _activityValue, out _activityList), 0, 1);

        _hopsGrid = MakeGrid();
        _hopsGrid.Columns.Add("Hop", "#");
        _hopsGrid.Columns.Add("IP", "IP");
        _hopsGrid.Columns.Add("Hostname", "Hostname");
        _hopsGrid.Columns.Add("Rtt", "RTT");
        _hopsGrid.Columns.Add("Network", "Network");
        _hopsEmpty = EmptyLabel("No traceroute yet. The first test will fill this in.");
        _hopsHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Theme.CreamMuted,
            Font = Theme.Small,
            Text = "",
        };

        _summaryGrid = MakeGrid();
        _summaryGrid.Columns.Add("Upstream", "Upstream");
        _summaryGrid.Columns.Add("Share", "Share");
        _summaryGrid.Columns.Add("Avg", "Avg Riot ms");
        _summaryEmpty = EmptyLabel("No samples yet.");

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Jungle,
            Margin = new Padding(0, 8, 0, 0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        body.Controls.Add(Section("Latest traceroute", Wrap(_hopsGrid, _hopsEmpty, _hopsHint)), 0, 0);
        body.Controls.Add(Section("Upstream summary", Wrap(_summaryGrid, _summaryEmpty, null)), 1, 0);
        root.Controls.Add(body, 0, 2);

        _logGrid = MakeGrid();
        foreach (var column in CsvFormat.Columns)
            _logGrid.Columns.Add(column, column);
        _logEmpty = EmptyLabel("No rows yet. A finished test appends the first line to the log.");
        root.Controls.Add(Section("Recent log (last 50)", Wrap(_logGrid, _logEmpty, null)), 0, 3);

        Controls.Add(root);
        _scheduler.Updated += OnUpdated;
        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => ApplyClock();
        _clock.Start();
        OnUpdated(_scheduler.Capture());
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        BindLog();
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        _clock.Stop();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        WindowState = FormWindowState.Normal;
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
        ApplyStatus(_scheduler.Capture());
    }

    private void ApplyStatus(ProbeSnapshot snapshot)
    {
        _modeValue.Text = snapshot.Mode;
        _loggingValue.Text = snapshot.Paused ? "Paused" : "On";
        _loggingValue.ForeColor = snapshot.Paused ? Theme.Amber : Theme.Leaf;
        _leagueValue.Text = snapshot.LeagueRunning ? "Running" : "Not running";
        _leagueValue.ForeColor = snapshot.LeagueRunning ? Theme.Amber : Theme.Cream;
        _nextValue.Text = Countdown(snapshot);
        _phaseValue.Text = string.IsNullOrWhiteSpace(snapshot.Phase) ? "Idle" : snapshot.Phase;
        _phaseValue.ForeColor = PhaseColor(snapshot);
        _activityValue.Text = string.IsNullOrWhiteSpace(snapshot.ActivityLine)
            ? "No activity yet."
            : snapshot.ActivityLine;

        _activityList.BeginUpdate();
        _activityList.Items.Clear();
        foreach (var line in snapshot.Activity)
            _activityList.Items.Add(line);
        _activityList.EndUpdate();
    }

    private void BindHops(ProbeSnapshot snapshot)
    {
        _hopsGrid.Rows.Clear();
        foreach (var hop in snapshot.Hops)
        {
            var rtt = hop.RttMs?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _hopsGrid.Rows.Add(
                hop.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                hop.Responded ? hop.Ip : "*",
                hop.Hostname,
                string.IsNullOrEmpty(rtt) ? "" : rtt + " ms",
                RouteClassifier.DescribeHop(hop.Hostname));
        }

        var empty = snapshot.Hops.Count == 0;
        _hopsEmpty.Visible = empty;
        _hopsGrid.Visible = !empty;
        _hopsHint.Text = snapshot.HopsAreAddressesOnly
            ? "IPs restored from the log. Hostnames come back on the next test."
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
            _logGrid.Rows.Add(
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
                row.RiotHopAvg,
                row.RiotHopLossPct,
                row.FullPath);
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
            _summaryGrid.Rows.Add(
                row.Upstream,
                UpstreamSummary.SharePercent(row.Count, rows.Count),
                row.AvgMs);
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

    private static Control BuildHeader()
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
            Text = "Gecko Peeks",
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
            Text = "Traceroute to Riot. Closes to the tray - Exit quits.",
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

    private static Control BuildStatus(
        out Label mode,
        out Label logging,
        out Label league,
        out Label next,
        out Label phase,
        out Label activity,
        out ListBox activityList)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Theme.Charcoal,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0),
        };
        for (var i = 0; i < 4; i++)
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
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

        var activityHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Theme.Charcoal,
        };
        activityHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        activityHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activity = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Amber,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "No activity yet.",
        };
        activityList = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.JungleDeep,
            ForeColor = Theme.CreamMuted,
            BorderStyle = BorderStyle.None,
            Font = Theme.Small,
            IntegralHeight = false,
        };
        activityHost.Controls.Add(activity, 0, 0);
        activityHost.Controls.Add(activityList, 0, 1);
        panel.Controls.Add(activityHost, 0, 2);
        panel.SetColumnSpan(activityHost, 4);
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
            ColumnHeadersHeight = 30,
            ScrollBars = ScrollBars.Both,
        };
        grid.RowTemplate.Height = 26;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Amber,
            Font = Theme.Small,
            SelectionBackColor = Theme.Panel,
            SelectionForeColor = Theme.Amber,
            Padding = new Padding(4, 0, 4, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.Cream,
            SelectionBackColor = Theme.JungleDeep,
            SelectionForeColor = Theme.Cream,
            Font = Theme.Small,
            Padding = new Padding(4, 0, 4, 0),
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Cream,
            SelectionBackColor = Theme.JungleDeep,
            SelectionForeColor = Theme.Cream,
        };
        return grid;
    }
}
