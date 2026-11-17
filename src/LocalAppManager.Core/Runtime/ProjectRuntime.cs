using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;
using LocalAppManager.Core.Projects;

namespace LocalAppManager.Core.Runtime;

public enum ProjectState
{
    Stopped,
    Starting,
    Running,
    /// <summary>The run ended on its own with exit code 0.</summary>
    Exited,
    /// <summary>The run ended on its own with a non-zero exit code.</summary>
    Crashed,
}

/// <summary>
/// Live state of one project: the one command it runs at a time (any chip), its log, readiness and ports,
/// and auto-restart. All public members are thread-safe; <see cref="Changed"/> is raised on
/// whatever thread caused the change (the UI marshals it itself).
/// </summary>
public sealed class ProjectRuntime
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Func<IReadOnlyDictionary<string, string>> _baseEnvironment;
    private readonly Dictionary<string, int> _lastExit = new();
    private ProcessSession? _session;
    /// <summary>The command of the current (or last) run; auto-restart runs it again.</summary>
    private ResolvedCommand? _command;
    private bool _serverRun;
    private CancellationTokenSource? _readinessCts;
    private CancellationTokenSource? _restartCts;
    private int _restartAttempt;

    internal ProjectRuntime(ProjectDefinition definition, LogBuffer log, Func<IReadOnlyDictionary<string, string>> baseEnvironment)
    {
        Definition = definition;
        Log = log;
        _baseEnvironment = baseEnvironment;
    }

    public string Id => Definition.Id;

    /// <summary>Current definition. Replaced (never mutated) by <see cref="ProjectManager"/>.</summary>
    public ProjectDefinition Definition { get; internal set; }

    public LogBuffer Log { get; }

    public ProjectState State { get; private set; } = ProjectState.Stopped;
    public int? LastExitCode { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public int? ProcessId { get; private set; }

    /// <summary>The port the current run was started with (the definition may have changed since).</summary>
    public int? RunPort { get; private set; }

    /// <summary>URL printed by the dev server ("Local: http://localhost:4100/").</summary>
    public string? DetectedUrl { get; private set; }

    /// <summary>Ports the process tree actually listens on.</summary>
    public IReadOnlyList<int> ListeningPorts { get; private set; } = [];

    /// <summary>Last problem worth showing (port conflict, launch failure…). Cleared on a successful start.</summary>
    public string? LastError { get; private set; }

    public bool IsPortConflict { get; private set; }

    /// <summary>When the next automatic restart is due, if one is scheduled.</summary>
    public DateTime? NextRestartAt { get; private set; }

    public bool IsActive => State is ProjectState.Starting or ProjectState.Running;

    /// <summary>The definition changed in a way that needs a restart to apply (port, command, env).</summary>
    public bool RestartRequired { get; private set; }

    /// <summary>Raised after any state/property change.</summary>
    public event Action<ProjectRuntime>? Changed;

    /// <summary>Raised when <see cref="ProjectDefinition.LastPort"/> was updated from what the run serves on (the manager saves it).</summary>
    public event Action<ProjectRuntime>? LastPortChanged;

    /// <summary>
    /// The port the current run serves on: the one in the URL it printed if it listens there, else the pinned port,
    /// else the lowest port it listens on. Null while nothing listens.
    /// </summary>
    public int? LivePort
    {
        get
        {
            IReadOnlyList<int> ports;
            string? url;
            int? pinned;
            lock (_gate) { ports = ListeningPorts; url = DetectedUrl; pinned = RunPort; }
            if (url is not null && UrlDetector.Find(url) is { IsLocal: true } u && ports.Contains(u.Port)) return u.Port;
            if (pinned is int p && ports.Contains(p)) return p;
            return ports.Count > 0 ? ports[0] : null;
        }
    }

    /// <summary>Chip key of the current or last run ("script:dev"); null before the first run.</summary>
    public string? RunKey { get; private set; }

    /// <summary>Chip name of the current or last run ("dev", "build").</summary>
    public string? RunName { get; private set; }

    /// <summary>The command line of the current or last run, with the port applied.</summary>
    public string? RunShellCommand { get; private set; }

    /// <summary>The current or last run is a server (crash toast, auto-restart, waits for its port).</summary>
    public bool IsServerRun { get { lock (_gate) return _serverRun; } }

    /// <summary>How each chip's last run ended: 0 success, -1 stopped by the user, else the exit code.</summary>
    public int? GetLastExit(string key)
    {
        lock (_gate) return _lastExit.TryGetValue(key, out var c) ? c : null;
    }

    /// <summary>Raised when a server run crashes (not when the user stops it, and not for a failed build or lint).</summary>
    public event Action<ProjectRuntime, int>? Crashed;

    /// <summary>The URL to open in a browser: the detected one, else localhost:port.</summary>
    public string? BrowserUrl
    {
        get
        {
            string path = string.IsNullOrWhiteSpace(Definition.BrowserPath) ? "/" : Definition.BrowserPath.Trim();
            if (!path.StartsWith('/')) path = "/" + path;
            if (DetectedUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri))
                return path == "/" ? u : $"{uri.Scheme}://{uri.Authority}{path}";
            int? port = RunPort ?? Definition.Port ?? (ListeningPorts.Count > 0 ? ListeningPorts[0] : null);
            return port is int p ? $"http://localhost:{p}{path}" : null;
        }
    }

    /// <summary>Every process currently in the run's job (cmd, npm, node, …).</summary>
    public IReadOnlyList<int> ProcessIds
    {
        get
        {
            ProcessSession? s;
            lock (_gate) s = _session;
            return s?.ProcessIds ?? [];
        }
    }

    // ------------------------------------------------------------------ start / stop

    /// <summary>Bumped by every stop; an auto-restart scheduled before a stop must not start afterwards.</summary>
    private int _stopGeneration;

    private Task StartAsync(ResolvedCommand command) => StartAsync(command, isAutoRestart: false, stopGeneration: -1);

    private async Task StartAsync(ResolvedCommand command, bool isAutoRestart, int stopGeneration)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isAutoRestart && stopGeneration != Volatile.Read(ref _stopGeneration)) return;
            if (!isAutoRestart) CancelPendingRestart(resetAttempts: true);
            if (_session is not null) return;

            var def = Definition;
            if (!Directory.Exists(def.Path))
            {
                SetError($"프로젝트 폴더가 없습니다: {def.Path}", conflict: false);
                return;
            }

            bool isServer = command.IsServer;
            var built = CommandBuilder.Build(def, command.ShellCommand, isServer, _baseEnvironment());

            // Port check right before launching, when the pinned port is actually handed to a server.
            if (built.PortPassed && def.Port is int port && !PortProbe.IsFree(port))
            {
                var owners = PortProbe.FindOwners(port);
                string who = owners.Count > 0
                    ? string.Join(", ", owners.Select(o => $"{o.ProcessName ?? "?"} (PID {o.ProcessId})").Distinct())
                    : "다른 프로그램 또는 시스템 예약";
                SetError($"포트 {port}이(가) 이미 사용 중입니다: {who}", conflict: true);
                Log.Error($"포트 {port}이(가) 이미 사용 중이라 시작하지 않았습니다 ({who}).");
                return;
            }

            ProcessSession session;
            try
            {
                session = ProcessSession.Start(built.CommandLine, built.ShellCommand, def.Path, built.Environment, Log, source: null);
            }
            catch (Exception e)
            {
                SetError($"실행 실패: {e.Message}", conflict: false);
                Log.Error($"실행 실패: {e.Message}");
                return;
            }

            lock (_gate)
            {
                _session = session;
                State = ProjectState.Starting;
                StartedAt = session.StartedAt;
                ProcessId = session.ProcessId;
                RunPort = built.PortPassed ? def.Port : null;
                _command = command;
                _serverRun = isServer || RunPort is not null;
                RunKey = command.Key;
                RunName = command.Name;
                RunShellCommand = built.ShellCommand;
                DetectedUrl = null;
                ListeningPorts = [];
                LastError = null;
                IsPortConflict = false;
                LastExitCode = null;
                NextRestartAt = null;
                RestartRequired = false;
            }
            string portNote = RunPort is int rp ? $"  (포트 {rp})" : "";
            Log.System($"▶ {built.ShellCommand}{portNote}");

            session.UrlDetected += OnUrlDetected;
            _readinessCts = new CancellationTokenSource();
            // A server isn't "running" until it listens; anything else is once it has stayed up briefly.
            _ = MonitorAsync(session, _serverRun, _readinessCts.Token);
            _ = WaitForExitAsync(session);
            RaiseChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task StopAsync()
    {
        Interlocked.Increment(ref _stopGeneration);
        CancelPendingRestart(resetAttempts: true);
        // Let a start that is already launching finish, so its session is the one we stop.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        _lifecycle.Release();
        ProcessSession? session;
        lock (_gate) session = _session;
        if (session is null)
        {
            if (State is ProjectState.Crashed or ProjectState.Exited) RaiseChanged();
            return;
        }
        if (GracefulStopTimeout > TimeSpan.Zero && !session.StopRequested)
        {
            bool clean = await session.StopGracefullyAsync(GracefulStopTimeout).ConfigureAwait(false);
            if (!clean) Log.System($"{GracefulStopTimeout.TotalSeconds:0}초 안에 끝나지 않아 강제로 종료했습니다.");
        }
        else
        {
            session.Kill();
        }
        await session.Completion.ConfigureAwait(false);
        // WaitForExitAsync finishes the bookkeeping; give it a moment so callers see Stopped.
        for (int i = 0; i < 50 && ReferenceEquals(_session, session); i++) await Task.Delay(10).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ one run at a time
    //
    // A project runs one command at a time (any chip). Running a command first stops whatever is running
    // ("switching"); running the one that already runs restarts it. A stop the user asks for while a switch
    // is still stopping the old run wins: the new command isn't started.

    private readonly SemaphoreSlim _switch = new(1, 1);
    private int _userStops;

    /// <summary>Stops whatever runs and cancels a pending auto-restart.</summary>
    internal Task StopEverythingAsync()
    {
        Interlocked.Increment(ref _userStops);
        return StopAsync();
    }

    private async Task SwitchAsync(ResolvedCommand command)
    {
        int stops = Volatile.Read(ref _userStops);
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (stops != Volatile.Read(ref _userStops)) return;
            await StartAsync(command).ConfigureAwait(false);
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>Runs <paramref name="command"/> unless something already runs (then nothing happens).</summary>
    internal Task StartIfIdleAsync(ResolvedCommand command) => IsActive ? Task.CompletedTask : SwitchAsync(command);

    /// <summary>Stops whatever runs (including an earlier run of the same command) and runs this command.</summary>
    internal Task RunAsync(ResolvedCommand command) => SwitchAsync(command);

    /// <summary>The command of the current or last run, so restart can run it again.</summary>
    internal ResolvedCommand? LastCommand
    {
        get { lock (_gate) return _command; }
    }

    /// <summary>Kills everything immediately (app shutdown). Doesn't wait.</summary>
    internal void KillAll()
    {
        Interlocked.Increment(ref _stopGeneration);
        CancelPendingRestart(resetAttempts: true);
        lock (_gate) _session?.Kill();
    }

    public void SendInput(string text)
    {
        ProcessSession? s;
        lock (_gate) s = _session;
        s?.SendInput(text);
    }

    private async Task WaitForExitAsync(ProcessSession session)
    {
        int code = await session.Completion.ConfigureAwait(false);
        _readinessCts?.Cancel();
        bool userStop = session.StopRequested;
        TimeSpan uptime = DateTime.Now - session.StartedAt;
        session.UrlDetected -= OnUrlDetected;
        session.Dispose();

        ProjectState newState = userStop ? ProjectState.Stopped : code == 0 ? ProjectState.Exited : ProjectState.Crashed;
        bool server;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            State = newState;
            LastExitCode = userStop ? null : code;
            ProcessId = null;
            // A run that listened on a port was a server even if we didn't recognize its command.
            if (ListeningPorts.Count > 0) _serverRun = true;
            server = _serverRun;
            ListeningPorts = [];
            if (RunKey is { } key) _lastExit[key] = userStop ? -1 : code;
        }

        switch (newState)
        {
            case ProjectState.Stopped:
                Log.System("■ 정지했습니다.");
                break;
            case ProjectState.Exited:
                Log.System(server ? "■ 종료했습니다 (코드 0)." : $"✔ 완료 ({FormatDuration(uptime)})");
                break;
            default:
                Log.Error(server
                    ? $"✖ 비정상 종료했습니다 (코드 {FormatExitCode(code)}, 실행 시간 {FormatDuration(uptime)})."
                    : $"✖ 실패 (코드 {FormatExitCode(code)}, {FormatDuration(uptime)})");
                break;
        }

        if (newState == ProjectState.Crashed && CommandNotFoundHint(session) is { } hint)
        {
            Log.Error(hint);
            lock (_gate) LastError = hint;
        }

        // A failed build or lint is just a result; only a server that dies is a crash worth a toast or a restart.
        if (newState == ProjectState.Crashed && server)
        {
            // A run that stayed up for a while counts as healthy: start the backoff over.
            if (uptime > TimeSpan.FromMinutes(1)) _restartAttempt = 0;
            Crashed?.Invoke(this, code);
            if (Definition.AutoRestart) ScheduleRestart();
        }
        RaiseChanged();
    }

    /// <summary>cmd's "'x' is not recognized…" (in English or Korean) means the tool isn't on PATH.</summary>
    private string? CommandNotFoundHint(ProcessSession session)
    {
        var recent = Log.GetSince(Math.Max(0, Log.NextSeq - 40)).Where(l => l.Time >= session.StartedAt.AddSeconds(-1) && l.Source is null);
        foreach (var line in recent)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Text, @"^'(?<cmd>[^']+)'(?:은\(는\) 내부 또는 외부 명령| is not recognized as an internal or external command)");
            if (!m.Success) continue;
            string cmd = m.Groups["cmd"].Value;
            string alt = cmd is "npm" or "npx" ? " pnpm/yarn을 쓰는 프로젝트라면 명령을 바꾸고," : "";
            return $"'{cmd}' 명령을 찾지 못했습니다.{alt} 설치 여부와 PATH를 확인하세요. (PATH는 실행할 때마다 레지스트리에서 다시 읽습니다)";
        }
        return null;
    }

    // ------------------------------------------------------------------ readiness / ports

    /// <summary>
    /// Stop sends Ctrl+C and waits this long before killing the job. Zero kills right away.
    /// Only effective in a process without a console (the GUI app).
    /// </summary>
    public static TimeSpan GracefulStopTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a run may stay "Starting" waiting for its port before it's treated as running anyway.</summary>
    public static TimeSpan PortWaitLimit { get; set; } = TimeSpan.FromSeconds(20);

    private async Task MonitorAsync(ProcessSession session, bool expectServer, CancellationToken ct)
    {
        try
        {
            var started = DateTime.Now;
            while (!ct.IsCancellationRequested)
            {
                bool starting = State == ProjectState.Starting;
                await Task.Delay(starting ? 300 : 3000, ct).ConfigureAwait(false);

                var pids = session.ProcessIds;
                var ports = PortProbe.ListeningPortsOf(pids);
                bool portsChanged = !ports.SequenceEqual(ListeningPorts);
                if (portsChanged)
                {
                    lock (_gate) if (ReferenceEquals(_session, session)) ListeningPorts = ports;
                    RememberLivePort();
                }

                if (State == ProjectState.Starting)
                {
                    var elapsed = DateTime.Now - started;
                    bool ready = RunPort is int p
                        ? ports.Contains(p) || await PortProbe.IsListeningAsync(p, ct).ConfigureAwait(false)
                        : ports.Count > 0 || (!expectServer && elapsed > TimeSpan.FromSeconds(2));
                    if (ready)
                    {
                        MarkRunning(session);
                    }
                    else if (RunPort is int waiting && elapsed > PortWaitLimit)
                    {
                        // Alive but not listening: a watcher/worker, or a server that ignores the port. Don't spin forever.
                        Log.Error(ports.Count > 0
                            ? $"⚠ {PortWaitLimit.TotalSeconds:0}초가 지나도 포트 {waiting}이(가) 열리지 않았고, 대신 {string.Join(", ", ports)}에서 리슨 중입니다. 포트 전달 방식(설정)을 확인하세요."
                            : $"⚠ {PortWaitLimit.TotalSeconds:0}초가 지나도 포트 {waiting}이(가) 열리지 않았습니다. 서버가 아니라면 포트 고정을 푸세요.");
                        MarkRunning(session);
                    }
                    else if (expectServer && elapsed > PortWaitLimit)
                    {
                        Log.System($"{PortWaitLimit.TotalSeconds:0}초가 지나도 리슨하는 포트가 없어 실행 중으로 표시합니다.");
                        MarkRunning(session);
                    }
                    else if (portsChanged)
                    {
                        RaiseChanged();
                    }
                }
                else if (portsChanged)
                {
                    RaiseChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnUrlDetected(UrlDetector.DetectedUrl url)
    {
        ProcessSession? session;
        bool changed = false;
        lock (_gate)
        {
            session = _session;
            if (session is null) return;
            // Prefer a local URL over a LAN one, and the first local one over later ones.
            if (DetectedUrl is null || (url.IsLocal && !IsLocalUrl(DetectedUrl)))
            {
                DetectedUrl = url.Url;
                changed = true;
            }
        }
        if (RunPort is int p && url.IsLocal && url.Port != p)
            Log.Error($"⚠ 배정한 포트는 {p}인데 서버가 {url.Port}에서 열렸습니다. 포트 전달 방식(설정)을 확인하세요.");
        if (changed) RememberLivePort();
        if (url.IsLocal && State == ProjectState.Starting) MarkRunning(session);
        else if (changed) RaiseChanged();
    }

    /// <summary>Copies <see cref="LivePort"/> into the definition so a stopped project can still show where it was.</summary>
    private void RememberLivePort()
    {
        if (LivePort is not int live) return;
        lock (_gate)
        {
            if (_session is null || Definition.LastPort == live) return;
            var updated = Definition.Clone();
            updated.LastPort = live;
            Definition = updated;
        }
        LastPortChanged?.Invoke(this);
    }

    private static bool IsLocalUrl(string url) => UrlDetector.Find(url) is { IsLocal: true };

    private void MarkRunning(ProcessSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || State != ProjectState.Starting) return;
            State = ProjectState.Running;
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ auto-restart

    /// <summary>1s, 2s, 4s … capped at 60s.</summary>
    public static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(attempt, 0, 10))));

    private void ScheduleRestart()
    {
        var delay = BackoffDelay(_restartAttempt);
        _restartAttempt++;
        int generation = Volatile.Read(ref _stopGeneration);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _restartCts?.Cancel();
            _restartCts = cts;
            NextRestartAt = DateTime.Now + delay;
        }
        Log.System($"↻ {delay.TotalSeconds:0}초 후 자동 재시작합니다 (시도 {_restartAttempt}).");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                ResolvedCommand? command;
                lock (_gate)
                {
                    NextRestartAt = null;
                    command = _command;
                }
                if (command is not null) await StartAsync(command, isAutoRestart: true, stopGeneration: generation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelPendingRestart(bool resetAttempts)
    {
        bool had;
        lock (_gate)
        {
            had = _restartCts is not null;
            _restartCts?.Cancel();
            _restartCts = null;
            NextRestartAt = null;
        }
        if (resetAttempts) _restartAttempt = 0;
        if (had) RaiseChanged();
    }

    // ------------------------------------------------------------------ misc

    internal void OnDefinitionChanged(ProjectDefinition old, ProjectDefinition updated)
    {
        Definition = updated;
        // The running custom command was edited (a script's body lives in package.json, not here).
        string? runningName = RunKey is { } k && k.StartsWith("cmd:", StringComparison.Ordinal) ? k[4..] : null;
        bool commandChanged = runningName is not null
            && old.Commands.FirstOrDefault(c => c.Script is null && c.Name.Trim() == runningName)?.Command
            != updated.Commands.FirstOrDefault(c => c.Script is null && c.Name.Trim() == runningName)?.Command;
        bool needsRestart = IsActive && (commandChanged || old.Port != updated.Port || old.PortMode != updated.PortMode
            || old.PortArgs != updated.PortArgs || old.PortEnvVar != updated.PortEnvVar || old.Path != updated.Path
            || !old.Environment.OrderBy(k => k.Key).SequenceEqual(updated.Environment.OrderBy(k => k.Key)));
        lock (_gate)
        {
            if (needsRestart) RestartRequired = true;
            if (!updated.AutoRestart && _restartCts is not null)
            {
                _restartCts.Cancel();
                _restartCts = null;
                NextRestartAt = null;
            }
            if (!IsActive) { LastError = null; IsPortConflict = false; }
        }
        RaiseChanged();
    }

    internal void ClearError()
    {
        lock (_gate)
        {
            LastError = null;
            IsPortConflict = false;
        }
        RaiseChanged();
    }

    private void SetError(string message, bool conflict)
    {
        lock (_gate)
        {
            LastError = message;
            IsPortConflict = conflict;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this);

    internal static string FormatExitCode(int code) =>
        code is < 0 or > 0xFFFF ? $"0x{unchecked((uint)code):X8}" : code.ToString();

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}초" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{(int)t.TotalHours}시간 {t.Minutes}분";
}
