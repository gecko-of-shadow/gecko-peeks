using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;

namespace GeckoPeeks.App;

public sealed class ProbeSnapshot
{
    public string Mode { get; init; } = "Every 15 min";
    public bool Paused { get; init; }
    public bool LeagueRunning { get; init; }
    public bool TestRunning { get; init; }
    public string Phase { get; init; } = "Idle";
    public DateTime? NextDueLocal { get; init; }
    public string ActivityLine { get; init; } = "";
    public IReadOnlyList<string> Activity { get; init; } = Array.Empty<string>();
    public IReadOnlyList<HopObservation> Hops { get; init; } = Array.Empty<HopObservation>();
    public bool HopsAreAddressesOnly { get; init; }
    public string LastError { get; init; } = "";
    public string ReadProblem { get; init; } = "";
    public int HopsRevision { get; init; }
    public int LogRevision { get; init; }
    public string Tooltip { get; init; } = "Gecko Peeks \u2014 no route yet";

    public string LogFolder { get; init; } = "";
    public string LogFileName { get; init; } = CsvLog.MainFileName;
    public string LogWriteStatus { get; init; } = LogStatusText.Waiting(null);

    /// <summary>Short reason while the log is blocked, else empty. Never raw exception text.</summary>
    public string LogLastError { get; init; } = "";
    public LogHealth LogHealth { get; init; } = LogHealth.Waiting;

    /// <summary>Why the log is in AppData instead of Documents, else empty.</summary>
    public string LogLocation { get; init; } = "";
    public int LogUnsavedRows { get; init; }
}

/// <summary>
/// One runner task, so ICMP never overlaps. The clock task only checks the
/// League process every 30 seconds and sleeps one second between due checks.
/// </summary>
public sealed class ProbeScheduler : IDisposable
{
    private const string SettingsFileName = "gecko-peeks-settings.json";
    private const string ActivityFileName = "activity.log";

    private readonly CsvLog _csv;
    private readonly ActivityLog _activity;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private SynchronizationContext? _ui;

    private bool _paused;
    private bool _league;
    private bool _testRunning;
    private bool _acceptRuns = true;
    private bool _hasPrevious;
    private bool _hopsAreAddressesOnly;
    private bool _riotNoReplyNoted;
    private int _runRequested;
    private int _hopsRevision;
    private int _logRevision = 1;
    private string _phase = "Idle";
    private string _lastError = "";
    private string _activityLine = "Waiting to start.";
    private string _prevUpstream = "";
    private string _prevCity = "";
    private string _prevNetwork = "";
    private string _tipUpstream = "";
    private string _tipNetwork = "";
    private string _tipCity = "";
    private string _tipRiot = "";
    private string _tipLastHop = "";
    private string _tipHandoffRtt = "";
    private string _logWriteStatus = LogStatusText.Waiting(null);
    private string _activityFolder;
    private DateTime? _tipTimestamp;
    private DateTime? _nextDueUtc;
    private DateTime? _lastTestUtc;
    private List<HopObservation> _hops = new();
    private Task? _runner;
    private Task? _clock;

    public ProbeScheduler(LogPaths paths)
    {
        _csv = new CsvLog(paths.Folder, fallbackFolder: paths.FallbackFolder);
        _csv.PrepareFolder();
        if (_csv.MovedThisSession)
        {
            CarryOver(SettingsFileName);
            CarryOver(ActivityFileName);
        }

        _activityFolder = _csv.Folder;
        _activity = new ActivityLog(Path.Combine(_activityFolder, ActivityFileName));
        CrashLog.Folder = _activityFolder;
    }

    /// <summary>Folder is where the log is now: AppData once Controlled folder access moved it there.</summary>
    public LogPaths Paths => new(_csv.Folder, _csv.FallbackFolder);
    public CsvLog Log => _csv;

    private string SettingsPath => Path.Combine(_csv.Folder, SettingsFileName);

    public event Action<ProbeSnapshot>? Updated;
    public event Action<string>? RouteChanged;

    public void Start()
    {
        _ui = SynchronizationContext.Current;
        LoadFromDisk();
        if (_csv.MovedThisSession)
            Note(LogStatusText.MovedNote(_csv.CopiedRows));
        else if (_csv.Health == LogHealth.Blocked)
            Note(LogStatusText.BlockedAtStartNote);
        Publish();
        var token = _cts.Token;
        _runner = Task.Run(() => RunnerLoop(token), token);
        _clock = Task.Run(() => ClockLoop(token), token);
    }

    public void RunNow()
    {
        Note("Run test now");
        RequestRun();
    }

    public void TogglePause()
    {
        bool paused;
        lock (_gate)
        {
            _paused = !_paused;
            paused = _paused;
            // The resumed run is requested explicitly. Leave the due time empty
            // until that test finishes so the clock does not queue a second one.
            _nextDueUtc = null;
        }

        SaveSettings();
        Note(paused ? "Logging paused. Run test now still works." : "Logging resumed.");
        if (!paused)
            RequestRun();
    }

    public ProbeSnapshot Capture()
    {
        lock (_gate)
            return BuildSnapshot(DateTime.Now);
    }

    public async Task StopAsync()
    {
        lock (_gate)
            _acceptRuns = false;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Already disposed.
        }

        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            bool running;
            lock (_gate)
                running = _testRunning;
            if (!running)
                break;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore a second dispose.
        }

        _cts.Dispose();
        _wake.Dispose();
    }

    private async Task ClockLoop(CancellationToken cancellationToken)
    {
        UpdateLeague(logChange: false);
        lock (_gate)
        {
            if (!_paused)
                _nextDueUtc = DateTime.UtcNow.AddSeconds(5);
        }

        Publish();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool paused;
        lock (_gate)
        {
            paused = _paused;
            if (!paused)
            {
                var interval = _league ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
                _nextDueUtc = DateTime.UtcNow.Add(interval);
            }
        }

        if (!paused)
        {
            Note("Starting the first test.");
            RequestRun();
        }

        var nextLeagueCheck = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (DateTime.UtcNow >= nextLeagueCheck)
            {
                nextLeagueCheck = DateTime.UtcNow.AddSeconds(30);
                UpdateLeague(logChange: true);
            }

            bool due;
            lock (_gate)
                due = !_paused && !_testRunning && _nextDueUtc is DateTime when && DateTime.UtcNow >= when;

            if (due)
                RequestRun();
        }
    }

    private void UpdateLeague(bool logChange)
    {
        var running = LeagueProcess.IsRunning();
        var changed = false;
        lock (_gate)
        {
            if (_league != running)
            {
                _league = running;
                changed = true;
                RecomputeDue();
            }
        }

        if (!changed)
            return;

        if (logChange)
            Note(running
                ? "League of Legends is running. Tests switch to every 5 min."
                : "League of Legends closed. Tests switch back to every 15 min.");
        else
            Publish();
    }

    private void RequestRun()
    {
        lock (_gate)
        {
            if (!_acceptRuns)
                return;
            _runRequested = 1;
        }

        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task RunnerLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_wake.Wait(0))
            {
                // Collapse extra wakes. One follow-up run is enough.
            }

            var shouldRun = false;
            lock (_gate)
            {
                if (_runRequested == 1)
                {
                    _runRequested = 0;
                    shouldRun = true;
                    _testRunning = true;
                    _phase = "Starting test";
                }
            }

            if (!shouldRun)
                continue;

            Publish();
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Note("Test stopped: " + ex.Message);
            }
            finally
            {
                bool again;
                lock (_gate)
                {
                    _testRunning = false;
                    // Every finished run moves the due time, saved or not. Otherwise a run that
                    // cannot write (or gets no replies) leaves it in the past and tests run back to back.
                    _lastTestUtc = DateTime.UtcNow;
                    if (!_paused)
                        RecomputeDue();
                    again = _runRequested == 1 && _acceptRuns;
                    if (string.IsNullOrWhiteSpace(_lastError))
                        _phase = _paused ? "Paused" : "Idle";
                }

                Publish();
                if (again)
                    RequestRun();
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var league = false;
        lock (_gate)
            league = _league;

        // Progress<T> captures SynchronizationContext at construction. This runs on
        // the thread pool, so use the UI context saved in Start() instead.
        var progress = new ContextProgress<ProbeProgress>(_ui, update =>
        {
            lock (_gate)
            {
                _phase = update.Phase;
                if (update.Hops != null)
                {
                    _hops = update.Hops.ToList();
                    _hopsAreAddressesOnly = false;
                    _hopsRevision++;
                }
            }

            Publish();
        });

        var row = await TestRunner.RunAsync(league, progress, cancellationToken).ConfigureAwait(false);
        if (row.Cancelled)
        {
            // Cancelled probes are not finished samples. Do not append a partial
            // row (blank Riot stats) that would skew the upstream summary.
            lock (_gate)
            {
                _phase = "Idle";
                _lastError = "";
                if (row.Hops.Count > 0)
                {
                    _hops = row.Hops.ToList();
                    _hopsAreAddressesOnly = false;
                    _hopsRevision++;
                }
            }

            Note(row.HasMeasuredData()
                ? "Test cancelled; partial result was not written to the CSV."
                : "Test cancelled before it recorded anything.");
            Publish();
            return;
        }

        if (row.Hops.Count > 0 && row.Hops.All(hop => !hop.Responded))
        {
            lock (_gate)
            {
                _phase = "Idle";
                _lastError = string.IsNullOrWhiteSpace(row.Error) ? "No hops responded." : row.Error;
                _hops = row.Hops.ToList();
                _hopsAreAddressesOnly = false;
                _hopsRevision++;
            }

            Note("No hops responded; nothing was written to the CSV.");
            Publish();
            return;
        }

        var folderBefore = _csv.Folder;
        AppendResult where;
        try
        {
            where = _csv.Append(row);
        }
        catch (Exception ex)
        {
            // The LOCAL LOG strip carries the reason; the status panel only points there,
            // so the same error is not shown (and clipped) twice.
            lock (_gate)
            {
                _lastError = row.Error;
                _phase = string.IsNullOrWhiteSpace(row.Error) ? "Idle" : "Last test problem: " + row.Error;
                _hops = row.Hops.ToList();
                _hopsAreAddressesOnly = false;
                _hopsRevision++;
            }

            Note(_csv.Health == LogHealth.Blocked ? LogStatusText.NotSavedNote : "Row not saved: " + ex.Message);
            SaveSettings(row);
            return;
        }

        if (!string.Equals(folderBefore, _csv.Folder, StringComparison.OrdinalIgnoreCase))
        {
            FollowLogFolder();
            Note(LogStatusText.MovedNote(_csv.CopiedRows));
        }

        var savedAt = _csv.LastSuccessfulWriteLocal ?? DateTime.Now;
        var saved = LogStatusText.Saved(savedAt, where);

        lock (_gate)
        {
            _lastError = row.Error;
            _phase = string.IsNullOrWhiteSpace(row.Error) ? "Idle" : "Last test problem: " + row.Error;
            _hops = row.Hops.ToList();
            _hopsAreAddressesOnly = false;
            _hopsRevision++;
            _logRevision++;
            _logWriteStatus = saved;
            Remember(row);
        }

        SaveSettings(row);
        if (!string.IsNullOrWhiteSpace(_csv.BackupProblem))
            Note("Row saved. Backup copy could not be refreshed: " + _csv.BackupProblem);
        else if (where == AppendResult.Pending)
            Note("Main log is locked. The row is in riot-route-log.pending.csv and will merge later.");
        else
            Note(string.IsNullOrWhiteSpace(row.Error)
                ? saved
                : "Appended a row with blanks after a problem: " + row.Error);

        if (!_riotNoReplyNoted && LatencyLabels.RiotNoReply(row.RiotHopAvg, row.RiotHopLossPct))
        {
            _riotNoReplyNoted = true;
            Note($"Riot ICMP ({TestRunner.RiotPingTarget}) gave no reply (Riot filters ping). Latency shown is the last hop (path), not in-game ping.");
        }

        if (!string.IsNullOrWhiteSpace(row.Error))
        {
            lock (_gate)
                _phase = "Last test problem: " + row.Error;
        }
    }

    private void Remember(RouteRow row)
    {
        var had = _hasPrevious;
        var prevUp = _prevUpstream;
        var prevCity = _prevCity;
        var prevNet = _prevNetwork;

        _hasPrevious = true;
        _prevUpstream = row.Upstream;
        _prevCity = row.HandoffCity;
        _prevNetwork = row.HandoffNetwork;
        _tipUpstream = row.Upstream;
        _tipNetwork = row.HandoffNetwork;
        _tipCity = row.HandoffCity;
        _tipRiot = row.RiotHopAvg;
        _tipLastHop = row.LastHopAvg;
        _tipHandoffRtt = row.HandoffRttMs;
        if (DateTime.TryParseExact(
                row.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
        }
        else
        {
            _tipTimestamp = DateTime.Now;
        }

        if (!had)
            return;

        var message = RouteChange.Describe(true, prevUp, prevCity, prevNet, row.Upstream, row.HandoffCity, row.HandoffNetwork);
        if (!string.IsNullOrWhiteSpace(message))
            Post(() => RouteChanged?.Invoke(message));
    }

    private void LoadFromDisk()
    {
        var settings = SettingsStore.Load(SettingsPath);
        IReadOnlyList<RouteRow> rows;
        try
        {
            rows = _csv.ReadAll();
        }
        catch
        {
            rows = Array.Empty<RouteRow>();
        }

        RouteRow? last = null;
        for (var i = 0; i < rows.Count; i++)
            last = rows[i];

        lock (_gate)
        {
            _paused = settings.Paused;
            _phase = _paused ? "Paused" : "Idle";
            if (last != null)
                ApplyRemembered(last, settings);
            else if (settings.Last != null)
                ApplySettingsResult(settings);

            var recent = _activity.Recent();
            if (recent.Count > 0)
                _activityLine = recent[^1];
            else
                _activityLine = _paused ? "Logging is paused." : "No tests yet.";

            _logWriteStatus = LogStatusText.Waiting(last?.Timestamp);
        }
    }

    /// <summary>Settings, activity history and crash.log go wherever the CSV now lives.</summary>
    private void FollowLogFolder()
    {
        var folder = _csv.Folder;
        if (string.Equals(folder, _activityFolder, StringComparison.OrdinalIgnoreCase))
            return;

        CarryOver(SettingsFileName);
        CarryOver(ActivityFileName);
        _activityFolder = folder;
        _activity.MoveTo(Path.Combine(folder, ActivityFileName));
        CrashLog.Folder = folder;
    }

    private void CarryOver(string fileName)
    {
        try
        {
            var from = Path.Combine(_csv.PrimaryFolder, fileName);
            var to = Path.Combine(_csv.Folder, fileName);
            if (File.Exists(from) && !File.Exists(to))
                File.Copy(from, to);
        }
        catch
        {
            // Settings and activity history are conveniences; the CSV already moved.
        }
    }

    private void ApplyRemembered(RouteRow last, AppSettings settings)
    {
        _hasPrevious = true;
        _prevUpstream = last.Upstream;
        _prevCity = last.HandoffCity;
        _prevNetwork = last.HandoffNetwork;
        _tipUpstream = last.Upstream;
        _tipNetwork = last.HandoffNetwork;
        _tipCity = last.HandoffCity;
        _tipRiot = last.RiotHopAvg;
        _tipLastHop = last.LastHopAvg;
        _tipHandoffRtt = last.HandoffRttMs;
        if (DateTime.TryParseExact(
                last.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
            _lastTestUtc = local.ToUniversalTime();
        }

        if (settings.Hops.Count > 0 && string.Equals(settings.Last?.Timestamp, last.Timestamp, StringComparison.Ordinal))
        {
            _hops = settings.Hops.Select(ToHop).ToList();
            _hopsAreAddressesOnly = false;
        }
        else
        {
            _hops = RouteClassifier.HopsFromFullPath(last.FullPath);
            _hopsAreAddressesOnly = _hops.Count > 0;
        }

        _hopsRevision++;
    }

    private void ApplySettingsResult(AppSettings settings)
    {
        var last = settings.Last;
        if (last == null)
            return;

        _hasPrevious = true;
        _prevUpstream = last.Upstream;
        _prevCity = last.HandoffCity;
        _prevNetwork = last.HandoffNetwork;
        _tipUpstream = last.Upstream;
        _tipNetwork = last.HandoffNetwork;
        _tipCity = last.HandoffCity;
        _tipRiot = last.RiotHopAvg;
        _tipLastHop = last.LastHopAvg;
        _tipHandoffRtt = last.HandoffRttMs;
        if (DateTime.TryParseExact(
                last.Timestamp,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var local))
        {
            _tipTimestamp = local;
            _lastTestUtc = local.ToUniversalTime();
        }

        _hops = settings.Hops.Select(ToHop).ToList();
        _hopsRevision++;
    }

    private static HopObservation ToHop(SavedHop hop) =>
        new(hop.Number, hop.Ip, hop.Hostname, hop.RttMs, hop.Responded, hop.ReachedTarget);

    private void RecomputeDue()
    {
        if (_paused)
        {
            _nextDueUtc = null;
            return;
        }

        var interval = _league ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
        var from = _lastTestUtc ?? DateTime.UtcNow;
        _nextDueUtc = from + interval;
    }

    private void SaveSettings(RouteRow? row = null)
    {
        AppSettings settings;
        lock (_gate)
        {
            settings = new AppSettings { Paused = _paused };
            if (row != null)
            {
                settings.Last = new SavedResult
                {
                    Timestamp = row.Timestamp,
                    Upstream = row.Upstream,
                    HandoffNetwork = row.HandoffNetwork,
                    HandoffCity = row.HandoffCity,
                    HandoffRttMs = row.HandoffRttMs,
                    RiotHopAvg = row.RiotHopAvg,
                    LastHopAvg = row.LastHopAvg,
                    FullPath = row.FullPath,
                };
                settings.Hops = row.Hops.Select(hop => new SavedHop
                {
                    Number = hop.Number,
                    Ip = hop.Ip,
                    Hostname = hop.Hostname,
                    RttMs = hop.RttMs,
                    Responded = hop.Responded,
                    ReachedTarget = hop.ReachedTarget,
                }).ToList();
            }
            else if (_tipTimestamp != null)
            {
                settings.Last = new SavedResult
                {
                    Timestamp = CsvFormat.FormatTimestamp(_tipTimestamp.Value),
                    Upstream = _tipUpstream,
                    HandoffNetwork = _tipNetwork,
                    HandoffCity = _tipCity,
                    HandoffRttMs = _tipHandoffRtt,
                    RiotHopAvg = _tipRiot,
                    LastHopAvg = _tipLastHop,
                    FullPath = RouteClassifier.BuildFullPath(_hops),
                };
                settings.Hops = _hops.Select(hop => new SavedHop
                {
                    Number = hop.Number,
                    Ip = hop.Ip,
                    Hostname = hop.Hostname,
                    RttMs = hop.RttMs,
                    Responded = hop.Responded,
                    ReachedTarget = hop.ReachedTarget,
                }).ToList();
            }
        }

        try
        {
            SettingsStore.Save(SettingsPath, settings);
        }
        catch
        {
            // The CSV row is the durable record. Settings only restore the window.
        }
    }

    private void Note(string message)
    {
        var line = _activity.Write(message);
        lock (_gate)
            _activityLine = line;
        Publish();
    }

    private void Publish()
    {
        ProbeSnapshot snapshot;
        lock (_gate)
            snapshot = BuildSnapshot(DateTime.Now);

        Post(() => Updated?.Invoke(snapshot));
    }

    private ProbeSnapshot BuildSnapshot(DateTime now)
    {
        return new ProbeSnapshot
        {
            Mode = _league ? "Every 5 min" : "Every 15 min",
            Paused = _paused,
            LeagueRunning = _league,
            TestRunning = _testRunning,
            Phase = _phase,
            NextDueLocal = _nextDueUtc?.ToLocalTime(),
            ActivityLine = _activityLine,
            Activity = _activity.Recent(),
            Hops = _hops.ToArray(),
            HopsAreAddressesOnly = _hopsAreAddressesOnly,
            LastError = _lastError,
            ReadProblem = _csv.ReadProblem ?? "",
            HopsRevision = _hopsRevision,
            LogRevision = _logRevision,
            Tooltip = TooltipText.Format(_tipUpstream, _tipNetwork, _tipCity, _tipRiot, _tipLastHop, _tipHandoffRtt, _tipTimestamp, now),
            LogFolder = _csv.Folder,
            LogFileName = CsvLog.MainFileName,
            LogWriteStatus = _logWriteStatus,
            LogLastError = _csv.LastWriteError ?? "",
            LogHealth = _csv.Health,
            LogLocation = LogStatusText.Location(_csv.UsingFallback, _csv.MovedThisSession, _csv.CopiedRows),
            LogUnsavedRows = _csv.UnsavedRows,
        };
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui == null)
        {
            try
            {
                action();
            }
            catch
            {
                // UI updates are optional.
            }

            return;
        }

        ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch
            {
                // A drawing failure must not kill the runner.
            }
        }, null);
    }

    /// <summary>
    /// Like Progress&lt;T&gt;, but uses a captured UI SynchronizationContext even when
    /// constructed on a thread-pool thread (where SynchronizationContext.Current is null).
    /// </summary>
    private sealed class ContextProgress<T> : IProgress<T>
    {
        private readonly SynchronizationContext? _context;
        private readonly Action<T> _handler;

        public ContextProgress(SynchronizationContext? context, Action<T> handler)
        {
            _context = context;
            _handler = handler;
        }

        public void Report(T value)
        {
            if (_context == null)
            {
                _handler(value);
                return;
            }

            _context.Post(_ =>
            {
                try
                {
                    _handler(value);
                }
                catch
                {
                    // Progress must not fail the probe.
                }
            }, null);
        }
    }
}
