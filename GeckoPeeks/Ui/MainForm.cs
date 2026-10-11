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
        _summaryGrid.Columns.Add