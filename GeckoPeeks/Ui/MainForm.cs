using System.Diagnostics;
using System.Reflection;
using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Ui;

internal sealed class MainForm : Form
{
    private readonly ProbeScheduler _scheduler;
    private readonly DataGridView _summaryGrid;
    private readonly DataGridView _logGrid;
    private readonly DataGridView _hopsGrid;
    private readonly Label _modeValue;
    private readonly Label _loggingValue;
    private readonly Label _leagueValue;
    private readonly Label _nextValue;
    private readonly Label _phaseValue;
    private readonly Label _activityValue;
    private readonly ListBox _activityList;
    private readonly Label _hopsHint;
    private readonly Label _logFolderValue;
    private readonly Label _logFileValue;
    private readonly Label _logWriteValue;
    private readonly Label _logErrorValue;
    private readonly System.Windows.Forms.Timer _clock;
    private bool _exitRequested;
    private int _hopsRevision = -1;
    private int _logRevision = -1;
    private string _lastActivityLine = "\u0000";
    private string _lastLogWriteStatus = "\u0000";

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
        MinimumSize = new Size(920, 720);
        Size = new Size(1140, 820);
        ShowInTaskbar = false;
        Icon = GeckoMark.CreateIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Theme.Jungle,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

        root.Controls.Add(BuildHeader(version), 0, 0);
        root.Controls.Add(BuildStatus(out _modeValue, out _loggingValue, out _leagueValue, out _nextValue, out _phaseValue, out _activityValue, out _activityList), 0, 1);
        root.Controls.Add(BuildLogStrip(out _logFolderValue, out _logFileValue, out _logWriteValue, out _logErrorValue), 0, 2);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Jungle,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));

        _summaryGrid = MakeGrid();
        _summaryGrid.Columns.Add("Upstream", "Upstream");
        _summaryGrid.Columns.Add("Share", "Share");
        _summaryGrid.Columns.Add("Count", "Count");
        _summaryGrid.Columns.Add("Avg", "Avg ms");
        StyleColumns(_summaryGrid);

        _logGrid = MakeGrid();
        foreach (var title in new[] { "When", "League", "Upstream", "Handoff", "City", "Handoff ms", "LastHopAvg", "RiotAvg", "Path" })
            _logGrid.Columns.Add(title, title);
        StyleColumns(_logGrid);
        _logGrid.Columns["Path"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        left.Controls.Add(Wrap("Upstream summary (last 50)", _summaryGrid));
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
        right.Controls.Add(Wrap("Recent log (last 50)", _logGrid));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);
        root.Controls.Add(body, 0, 3);

        _hopsGrid = MakeGrid();
        foreach (var title in new[] { "#", "IP", "Host", "RTT", "OK" })
            _hopsGrid.Columns.Add(title, title);
        StyleColumns(_hopsGrid);
        _hopsHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.Mist,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var hopsPanel = new Panel { Dock = DockStyle.Fill };
        hopsHost.Controls.Add(_hopsGrid);
        hopsHost.Controls.Add(_hopsHint);
        root.Controls.Add(Wrap("Latest traceroute", hopsHost), 0, 4);

        Controls.Add(root);

        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => ApplyStatus(_scheduler.Capture());
        _clock.Start();

        _scheduler.Updated += OnUpdated;
        ApplySnapshot(_scheduler.Capture());
        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    public void OpenLog() => OpenPath(_scheduler.Log.MainPath);

    public void OpenLogFolder() => OpenPath(_scheduler.Paths.Folder);

    public void CopyLogPath()
    {
        try
        {
            Clipboard.SetText(_scheduler.Paths.Folder);
        }
        catch
        {
            // Clipboard can be busy.
        }
    }

    private void OnUpdated(ProbeSnapshot snapshot)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnUpdated(snapshot)));
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(ProbeSnapshot snapshot)
    {
        ApplyStatus(snapshot);
        ApplyLogStrip(snapshot);
        if (snapshot.LogRevision != _logRevision)
        {
            _logRevision = snapshot.LogRevision;
            BindLog();
        }

        if (snapshot.HopsRevision != _hopsRevision)
        {
            _hopsRevision = snapshot.HopsRevision;
            BindHops(snapshot);
        }

        if (!string.Equals(snapshot.ActivityLine, _lastActivityLine, StringComparison.Ordinal))
        {
            _lastActivityLine = snapshot.ActivityLine;
            _activityValue.Text = snapshot.ActivityLine;
            _activityList.BeginUpdate();
            _activityList.Items.Clear();
            foreach (var line in snapshot.Activity)
                _activityList.Items.Add(line);
            if (_activityList.Items.Count > 0)
                _activityList.TopIndex = _activityList.Items.Count - 1;
            _activityList.EndUpdate();
        }

        _phaseValue.Text = snapshot.Phase;
        _phaseValue.ForeColor = PhaseColor(snapshot);
    }

    private void ApplyStatus(ProbeSn