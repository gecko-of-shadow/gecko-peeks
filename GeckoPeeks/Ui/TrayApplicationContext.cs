using System.Drawing.Drawing2D;
using GeckoPeeks.App;
using GeckoPeeks.Text;
using Microsoft.Win32;

namespace GeckoPeeks.Ui;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly MainForm _form;
    private readonly ProbeScheduler _scheduler;
    private readonly StartupToggle _startup = new(StartupRegistration.IsEnabled, StartupRegistration.SetEnabled);
    private readonly UpdateClient _updates = new();
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly System.Windows.Forms.Timer _clock;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private UpdateOffer? _pendingOffer;
    private string? _downloadedPath;
    private bool _checkingUpdate;
    private bool _exiting;
    private bool _closedHintShown;

    public TrayApplicationContext(SingleInstance instance, bool showWindow)
    {
        AppUpdate.CleanupLeftovers(Environment.ProcessPath);

        _scheduler = new ProbeScheduler(LogPaths.Resolve());
        _form = new MainForm(_scheduler);
        _ = _form.Handle;

        _icon = new NotifyIcon
        {
            Icon = GeckoMark.CreateIcon(SystemInformation.SmallIconSize),
            Visible = true,
            Text = "Gecko Peeks — no route yet",
        };
        // Windows 11 tucks new tray icons under the ^ overflow, where a double-click is easy to
        // miss. A single left click opens the window too.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _form.ShowFromTray();
        };
        _icon.DoubleClick += (_, _) => _form.ShowFromTray();
        _icon.BalloonTipClicked += (_, _) => _form.ShowFromTray();
        _form.HiddenToTray += ShowClosedHint;

        _pauseItem = CommandItem(TrayCommand.TogglePause);
        _startupItem = CommandItem(TrayCommand.StartWithWindows);
        _updateItem = CommandItem(TrayCommand.CheckForUpdates);
        _icon.ContextMenuStrip = BuildMenu();

        _scheduler.Updated += snapshot =>
        {
            _icon.Text = Fit(snapshot.Tooltip);
            _pauseItem.Text = TrayMenu.Label(TrayCommand.TogglePause, snapshot.Paused);
        };
        _scheduler.RouteChanged += message =>
        {
            _icon.BalloonTipTitle = "Gecko Peeks";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(6000, "Gecko Peeks", message, ToolTipIcon.Info);
        };

        instance.Listen(() => _form.BeginInvoke(new Action(_form.ShowFromTray)));
        _scheduler.Start();
        StartupRegistration.RepairIfStale();
        _startupItem.Checked = _startup.Checked;
        SystemEvents.SessionEnding += OnSessionEnding;

        _clock = new System.Windows.Forms.Timer { Interval = 15000 };
        _clock.Tick += (_, _) => _icon.Text = Fit(_scheduler.Capture().Tooltip);
        _clock.Start();

        // Quiet check a few seconds after start: download in the background, no MessageBox (that
        // would block the CI smoke). The tray item becomes Install update when the file is ready.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 12000 };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Stop();
            _ = AutoCheckAsync();
        };
        _updateTimer.Start();

        if (showWindow)
            _form.ShowFromTray();
    }

    private ContextMenuStrip BuildMenu()
    {
        // ShowCheckMargin is required for ToolStripMenuItem.Checked glyphs.
        // ShowImageMargin=false alone hides the check column entirely.
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Charcoal,
            ForeColor = Theme.Cream,
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = true,
            ShowCheckMargin = true,
        };
        menu.Opening += (_, _) => _startupItem.Checked = _startup.Checked;

        foreach (var entry in TrayMenu.Layout)
        {
            if (entry is not { } command)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            var item = command switch
            {
                TrayCommand.TogglePause => _pauseItem,
                TrayCommand.StartWithWindows => _startupItem,
                TrayCommand.CheckForUpdates => _updateItem,
                _ => CommandItem(command),
            };
            if (command == TrayMenu.DefaultCommand)
                item.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(item);
        }

        return menu;
    }

    private ToolStripMenuItem CommandItem(TrayCommand command)
    {
        var item = new ToolStripMenuItem(TrayMenu.Label(command)) { ForeColor = Theme.Cream };
        item.Click += (_, _) => Run(command);
        return item;
    }

    private void Run(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenWindow:
                _form.ShowFromTray();
                break;
            case TrayCommand.RunTestNow:
                _scheduler.RunNow();
                break;
            case TrayCommand.CopyDiagnosis:
                _form.CopyDiagnosis();
                break;
            case TrayCommand.OpenLog:
                _form.OpenLog();
                break;
            case TrayCommand.OpenLogFolder:
                _form.OpenLogFolder();
                break;
            case TrayCommand.TogglePause:
                _scheduler.TogglePause();
                break;
            case TrayCommand.StartWithWindows:
                ToggleStartup();
                break;
            case TrayCommand.CheckForUpdates:
                _ = OnUpdateCommandAsync();
                break;
            case TrayCommand.Exit:
                Exit();
                break;
        }
    }

    private void RefreshUpdateMenu()
    {
        var version = _downloadedPath != null && _pendingOffer != null
            ? AppUpdate.FormatVersion(_pendingOffer.Version)
            : null;
        _updateItem.Text = TrayMenu.Label(TrayCommand.CheckForUpdates, updateVersion: version);
    }

    private void Balloon(string text)
    {
        _icon.BalloonTipTitle = "Gecko Peeks";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000, "Gecko Peeks", text, ToolTipIcon.Info);
    }

    /// <summary>
    /// Startup path: check GitHub, skip if the user already dismissed this version, download when
    /// newer, then point the tray item at Install. Never shows a modal dialog.
    /// </summary>
    private async Task AutoCheckAsync()
    {
        if (_exiting || _checkingUpdate)
            return;

        _checkingUpdate = true;
        try
        {
            var offer = await _updates.CheckAsync(AppUpdate.CurrentVersion()).ConfigureAwait(true);
            if (_exiting || offer == null || UpdateSkip.IsSkipped(offer.Version))
                return;

            _pendingOffer = offer;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;

            Balloon("Gecko Peeks v" + AppUpdate.FormatVersion(offer.Version) + " is available. Downloading…");
            _downloadedPath = await _updates.DownloadAsync(offer, exe).ConfigureAwait(true);
            if (_exiting)
                return;

            RefreshUpdateMenu();
            Balloon(
                "v" + AppUpdate.FormatVersion(offer.Version)
                + " is ready. Right-click the gecko > Install update to restart.");
        }
        catch
        {
            // Offline or GitHub unreachable: stay quiet; the tray Check for updates still works.
            _pendingOffer = null;
            _downloadedPath = null;
            RefreshUpdateMenu();
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>
    /// Tray click: install a ready download, or run a check and tell the user what happened.
    /// </summary>
    private async Task OnUpdateCommandAsync()
    {
        if (_exiting)
            return;

        if (_downloadedPath != null && _pendingOffer != null && File.Exists(_downloadedPath))
        {
            InstallReadyUpdate();
            return;
        }

        if (_checkingUpdate)
        {
            MessageBox.Show(
                _form,
                "Still checking for an update.",
                "Gecko Peeks",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _checkingUpdate = true;
        _updateItem.Enabled = false;
        try
        {
            var current = AppUpdate.CurrentVersion();
            var offer = await _updates.CheckAsync(current).ConfigureAwait(true);
            if (_exiting)
                return;

            if (offer == null)
            {
                MessageBox.Show(
                    _form,
                    "You're on v" + AppUpdate.FormatVersion(current) + " (latest).",
                    "Gecko Peeks",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _pendingOffer = offer;
            UpdateSkip.Clear();
            var answer = MessageBox.Show(
                _form,
                "Gecko Peeks v" + AppUpdate.FormatVersion(offer.Version) + " is available.\n\n"
                + "Download and install it now? The app will restart when it is done.",
                "Gecko Peeks",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                UpdateSkip.Skip(offer.Version);
                _pendingOffer = null;
                _downloadedPath = null;
                RefreshUpdateMenu();
                return;
            }

            if (answer != DialogResult.Yes)
                return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                MessageBox.Show(
                    _form,
                    "Could not find this exe's path, so the update cannot be installed.",
                    "Gecko Peeks",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Balloon("Downloading v" + AppUpdate.FormatVersion(offer.Version) + "…");
            _downloadedPath = await _updates.DownloadAsync(offer, exe).ConfigureAwait(true);
            if (_exiting)
                return;

            RefreshUpdateMenu();
            InstallReadyUpdate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                _form,
                "Could not update: " + ex.Message,
                "Gecko Peeks",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _checkingUpdate = false;
            _updateItem.Enabled = true;
            RefreshUpdateMenu();
        }
    }

    private void InstallReadyUpdate()
    {
        if (_pendingOffer == null || string.IsNullOrWhiteSpace(_downloadedPath))
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        try
        {
            UpdateInstaller.BeginReplace(exe, _downloadedPath, UpdateInstaller.RestartArguments());
            Balloon("Installing v" + AppUpdate.FormatVersion(_pendingOffer.Version) + " and restarting…");
            Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                _form,
                "Could not install the update: " + ex.Message,
                "Gecko Peeks",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ToggleStartup()
    {
        if (!_startup.Toggle())
            _form.ShowFromTray();

        // Reflect the real HKCU Run value, not the intended toggle.
        _startupItem.Checked = _startup.Checked;
    }

    private void ShowClosedHint()
    {
        if (_closedHintShown || _exiting)
            return;

        _closedHintShown = true;
        _icon.ShowBalloonTip(6000, TrayMenu.ClosedHintTitle, TrayMenu.ClosedHintText, ToolTipIcon.Info);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _clock.Stop();
        _updateTimer.Stop();
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
            _updates.Dispose();
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
        _updateTimer.Stop();
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
        _updates.Dispose();
        _scheduler.Dispose();
        _form.ExitApplication();
        ExitThread();
    }

    private static string Fit(string text) => text.Length <= 127 ? text : text[..127];

    /// <summary>
    /// The stock check glyph is black and vanishes on jungle green. Draw a cream tick in an
    /// amber-edged box so the checkmark reads on both the idle and the hover background.
    /// </summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new MenuColors())
        {
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var box = Rectangle.Inflate(e.ImageRectangle, 1, 1);
            using (var fill = new SolidBrush(Theme.Jungle))
                g.FillRectangle(fill, box);
            using (var border = new Pen(Theme.Amber))
                g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);

            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tick = new Pen(Theme.Cream, Math.Max(2f, box.Height / 7f)))
            {
                tick.StartCap = LineCap.Round;
                tick.EndCap = LineCap.Round;
                tick.LineJoin = LineJoin.Round;
                g.DrawLines(tick, new[] { At(box, 0.24f, 0.52f), At(box, 0.43f, 0.70f), At(box, 0.77f, 0.31f) });
            }

            g.SmoothingMode = smoothing;
        }

        private static PointF At(Rectangle box, float x, float y) =>
            new(box.X + box.Width * x, box.Y + box.Height * y);
    }

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
        public override Color CheckBackground => Theme.Jungle;
        public override Color CheckSelectedBackground => Theme.Jungle;
        public override Color CheckPressedBackground => Theme.Panel;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }
}
