using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;
using LocalAppManager.Core.Projects;

namespace LocalAppManager.Core.Runtime;

public enum ProjectState
{
    Stopped,
    Starting,
    Running,
    /// <summary>The start command ended on its own with exit code 0.</summary>
    Exited,
    /// <summary>The start command ended on its own with a non-zero exit code.</summary>
    Crashed,
}

public sealed record CommandRunInfo(string Key, string DisplayName, string ShellCommand, bool IsRunning, int? LastExitCode, DateTime StartedAt);

/// <summary>
/// Live state of one project: the start command's process, its log, extra command runs,
/// and auto-restart. All public members are thread-safe; <see cref="Changed"/> is raised on
/// whatever thread caused the change (the UI marshals it itself).
/// </summary>
public sealed class ProjectRuntime
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Func<IReadOnlyDictionary<string, string>> _baseEnvironment;
    private readonly Dictionary<string, (ProcessSession Session, CommandRunInfo Info)> _commandRuns = new();
    private readonly Dictionary<string, int> _lastCommandExit = new();
    private ProcessSession? _session;
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

    /// <summary>Raised when the start command crashes (not when the user stops it).</summary>
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

    /// <summary>Every process currently in the start command's job (cmd, npm, node, …).</summary>
    public IReadOnlyList<int> ProcessIds
    {
        get
        {
            ProcessSession? s;
            lock (_gate) s = _session;
            return s?.ProcessIds ?? [];
        }
    }

    public IReadOnlyList<CommandRunInfo> CommandRuns
    {
        get { lock (_gate) return _commandRuns.Values.Select(v => v.Info).ToList(); }
    }

    public int? GetLastCommandExit(string key)
    {
        lock (_gate) return _lastCommandExit.TryGetValue(key, out var c) ? c : null;
    }

    // ------------------------------------------------------------------ start / stop

    internal async Task StartAsync(bool isAutoRestart = false)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!isAutoRestart) CancelPendingRestart(resetAttempts: true);
            if (_session is not null) return;

            var def = Definition;
            if (string.IsNullOrWhiteSpace(def.StartCommand))
            {
                SetError("시작 명령이 비어 있습니다. 프로젝트 설정에서 시작 명령을 입력하세요.", conflict: false);
                return;
            }
            if (!Directory.Exists(def.Path))
            {
                SetError($"프로젝트 폴더가 없습니다: {def.Path}", conflict: false);
                return;
            }

            // Port check right before launching.
            if (def.Port is int port && UsesPort(def) && !PortProbe.IsFree(port))
            {
                var owners = PortProbe.FindOwners(port);
                string who = owners.Count > 0
                    ? string.Join(", ", owners.Select(o => $"{o.ProcessName ?? "?"} (PID {o.ProcessId})").Distinct())
                    : "다른 프로그램 또는 시스템 예약";
                SetError($"포트 {port}이(가) 이미 사용 중입니다: {who}", conflict: true);
                Log.Error($"포트 {port}이(가) 이미 사용 중이라 시작하지 않았습니다 ({who}).");
                return;
            }

            var built = CommandBuilder.BuildStart(def, _baseEnvironment());
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
                RunPort = UsesPort(def) ? def.Port : null;
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
            _ = MonitorAsync(session, _readinessCts.Token);
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
        CancelPendingRestart(resetAttempts: true);
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

    internal async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>Kills everything immediately (app shutdown). Doesn't wait.</summary>
    internal void KillAll()
    {
        CancelPendingRestart(resetAttempts: true);
        lock (_gate)
        {
            _session?.Kill();
            foreach (var (s, _) in _commandRuns.Values) s.Kill();
        }
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
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            State = newState;
            LastExitCode = userStop ? null : code;
            ProcessId = null;
            ListeningPorts = [];
        }

        switch (newState)
        {
            case ProjectState.Stopped:
                Log.System("■ 정지했습니다.");
                break;
            case ProjectState.Exited:
                Log.System("■ 종료했습니다 (코드 0).");
                break;
            default:
                Log.Error($"✖ 비정상 종료했습니다 (코드 {FormatExitCode(code)}, 실행 시간 {FormatDuration(uptime)}).");
                break;
        }

        if (newState == ProjectState.Crashed && CommandNotFoundHint(session) is { } hint)
        {
            Log.Error(hint);
            lock (_gate) LastError = hint;
        }

        if (newState == ProjectState.Crashed)
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
            string alt = cmd is "npm" or "npx" ? " pnpm/yarn을 쓰는 프로젝트라면 시작 명령을 바꾸고," : "";
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

    private async Task MonitorAsync(ProcessSession session, CancellationToken ct)
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
                }

                if (State == ProjectState.Starting)
                {
                    var elapsed = DateTime.Now - started;
                    bool ready = RunPort is int p
                        ? ports.Contains(p) || await PortProbe.IsListeningAsync(p, ct).ConfigureAwait(false)
                        : ports.Count > 0 || elapsed > TimeSpan.FromSeconds(2);
                    if (ready)
                    {
                        MarkRunning(session);
                    }
                    else if (RunPort is int waiting && elapsed > PortWaitLimit)
                    {
                        // Alive but not listening: a watcher/worker, or a server that ignores the port. Don't spin forever.
                        Log.Error(ports.Count > 0
                            ? $"⚠ {PortWaitLimit.TotalSeconds:0}초가 지나도 포트 {waiting}이(가) 열리지 않았고, 대신 {string.Join(", ", ports)}에서 리슨 중입니다. 포트 전달 방식(설정)을 확인하세요."
                            : $"⚠ {PortWaitLimit.TotalSeconds:0}초가 지나도 포트 {waiting}이(가) 열리지 않았습니다. 서버가 아니라면 설정에서 포트 배정을 끄세요.");
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
        if (url.IsLocal && State == ProjectState.Starting) MarkRunning(session);
        else if (changed) RaiseChanged();
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
                lock (_gate) NextRestartAt = null;
                await StartAsync(isAutoRestart: true).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ extra commands

    /// <summary>Runs a script/command next to the start command. Key identifies it (one run per key at a time).</summary>
    internal void RunCommand(string key, string displayName, string command)
    {
        var def = Definition;
        lock (_gate)
            if (_commandRuns.ContainsKey(key)) return;

        var built = CommandBuilder.BuildOther(def, command, _baseEnvironment());
        ProcessSession session;
        try
        {
            session = ProcessSession.Start(built.CommandLine, built.ShellCommand, def.Path, built.Environment, Log, source: displayName);
        }
        catch (Exception e)
        {
            Log.Error($"[{displayName}] 실행 실패: {e.Message}", displayName);
            return;
        }
        var info = new CommandRunInfo(key, displayName, built.ShellCommand, true, null, session.StartedAt);
        lock (_gate) _commandRuns[key] = (session, info);
        Log.System($"▶ {built.ShellCommand}", displayName);
        RaiseChanged();

        _ = Task.Run(async () =>
        {
            int code = await session.Completion.ConfigureAwait(false);
            bool stopped = session.StopRequested;
            var elapsed = DateTime.Now - session.StartedAt;
            session.Dispose();
            lock (_gate)
            {
                _commandRuns.Remove(key);
                _lastCommandExit[key] = stopped ? -1 : code;
            }
            if (stopped) Log.System("■ 중지했습니다.", displayName);
            else if (code == 0) Log.System($"✔ 완료 ({FormatDuration(elapsed)})", displayName);
            else Log.Error($"✖ 실패 (코드 {FormatExitCode(code)}, {FormatDuration(elapsed)})", displayName);
            RaiseChanged();
        });
    }

    internal void StopCommand(string key)
    {
        ProcessSession? session;
        lock (_gate) session = _commandRuns.TryGetValue(key, out var run) ? run.Session : null;
        if (session is not null) _ = session.StopGracefullyAsync(GracefulStopTimeout);
    }

    public bool IsCommandRunning(string key)
    {
        lock (_gate) return _commandRuns.ContainsKey(key);
    }

    // ------------------------------------------------------------------ misc

    internal void OnDefinitionChanged(ProjectDefinition old, ProjectDefinition updated)
    {
        Definition = updated;
        bool needsRestart = IsActive && (old.StartCommand != updated.StartCommand || old.Port != updated.Port || old.PortMode != updated.PortMode
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

    internal static bool UsesPort(ProjectDefinition def) =>
        def.Port is not null && (def.PortMode != PortMode.None || def.StartCommand.Contains("{port}", StringComparison.Ordinal));

    private void RaiseChanged() => Changed?.Invoke(this);

    internal static string FormatExitCode(int code) =>
        code is < 0 or > 0xFFFF ? $"0x{unchecked((uint)code):X8}" : code.ToString();

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}초" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{(int)t.TotalHours}시간 {t.Minutes}분";
}
