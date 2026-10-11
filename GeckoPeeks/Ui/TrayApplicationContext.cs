using GeckoPeeks.App;
using Microsoft.Win32;

namespace GeckoPeeks.Ui;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly MainForm _form;
    private readonly ProbeScheduler _scheduler;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _clock;
    private bool _exiting;

    public TrayApplicationContext(SingleInstance instance)
    {
        _scheduler = new ProbeScheduler(LogPaths.Resolve());
        _form = new MainForm(_scheduler);
        _ = _form.Handle;

        _icon = new NotifyIcon
        {
            Icon = GeckoMark.CreateIcon(SystemInformation.SmallIconSize),
            Visible = true,
            Text = "Gecko Peeks — no route yet",
        };
        _icon.DoubleClick += (_, _) => _form.ShowFromTray();

        _pauseItem = new ToolStripMenuItem("Pause logging");
        _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _icon.ContextMenuStrip = BuildMenu();

        _scheduler.Updated += snapshot =>
        {
            _icon.Text = Fit(snapshot.Tooltip);
            _pauseItem.Text = snapshot.Paused ? "Resume logging" : "Pause logging";
        };
        _scheduler.RouteChanged += message =>
        {
            _icon.BalloonTipTitle = "Gecko Peeks";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(6000, "Gecko Peeks", message, ToolTipIcon.Info);
        };

        instance.Listen(() => _form.BeginInvoke(new Action(_form.ShowFromTray)));
        _scheduler.Start();
        _startupItem.Checked = StartupRegistration.IsEnabled();
        SystemEvents.SessionEnding += OnSessionEnding;

        _clock = new System.Windows.Forms.Timer { Interval = 15000 };
        _clock.Tick += (_, _) => _icon.Text = Fit(_scheduler.Capture().Tooltip);
        _clock.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.Cream,
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()),
            ShowImageMargin = false,
        };

        menu.Items.Add(Item("Run test now", (_, _) => _scheduler.RunNow()));
        menu.Items.Add(Item("Open log", (_, _) => _form.OpenLog()));
        menu.Items.Add(Item("Open log folder", (_, _) => _form.OpenLogFolder()));
        _pauseItem.ForeColor = Theme.Cream;
        _pauseItem.Click += (_, _) => _scheduler.TogglePause();
        menu.Items.Add(_pauseItem);
        _startupItem.ForeColor = Theme.Cream;
        _startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit", (_, _) => Exit()));
        return menu;
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!_startupItem.Checked);
            _startupItem.Checked = StartupRegistration.IsEnabled();
        }
        catch
        {
            _startupItem.Checked = false;
            _form.ShowFromTray();
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _clock.Stop();
        try
        {
            // Logoff/shutdown will kill the process; finish the runner synchronously.
            _scheduler.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown.
        }

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _scheduler.Dispose();
        }
        catch
        {
            // Process is ending.
        }
    }

    private async void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;

        SystemEvents.SessionEnding -= OnSessionEnding;
        _clock.Stop();
        try
        {
            await _scheduler.StopAsync().ConfigureAwait(true);
        }
        catch
        {
            // Still leave the tray.
        }

        _icon.Visible = false;
        _icon.Dispose();
        _scheduler.Dispose();
        _form.ExitApplication();
        ExitThread();
    }

    private static ToolStripMenuItem Item(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Theme.Cream };
        item.Click += onClick;
        return item;
    }

    private static string Fit(string text) => text.Length <= 127 ? text : text[..127];

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Charcoal;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Amber;
        public override Color MenuItemSelected => Theme.Jungle;
        public override Color MenuItemSelectedGradientBegin => Theme.Jungle;
        public override Color MenuItemSelectedGradientEnd => Theme.Jungle;
        public override Color MenuItemPressedGradientBegin => Theme.Panel;
        public override Color MenuItemPressedGradientEnd => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Charcoal;
        public override Color ImageMarginGradientMiddle => Theme.Charcoal;
        public override Color ImageMarginGradientEnd => Theme.Charcoal;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }
}
