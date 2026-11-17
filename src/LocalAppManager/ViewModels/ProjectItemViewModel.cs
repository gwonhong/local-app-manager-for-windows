using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Services;

namespace LocalAppManager.ViewModels;

/// <summary>
/// Read-only projection of a <see cref="ProjectRuntime"/> for binding. It never holds state of its own:
/// <see cref="Refresh"/> copies everything from the runtime, on the UI thread, whenever the runtime changes.
/// </summary>
public sealed partial class ProjectItemViewModel : ObservableObject
{
    private readonly ProjectManager _manager;
    private int _refreshQueued;

    public ProjectItemViewModel(ProjectManager manager, ProjectRuntime runtime)
    {
        _manager = manager;
        Runtime = runtime;
        Refresh();
        RefreshScripts();
    }

    public ProjectRuntime Runtime { get; }
    public string Id => Runtime.Id;

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Path { get; private set; } = "";
    [ObservableProperty] public partial string FrameworkName { get; private set; } = "";
    [ObservableProperty] public partial string Subtitle { get; private set; } = "";
    [ObservableProperty] public partial ProjectState State { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial bool IsActive { get; private set; }
    [ObservableProperty] public partial bool IsStarting { get; private set; }
    /// <summary>The pinned port (":4100"), empty when the project isn't pinned.</summary>
    [ObservableProperty] public partial string PortText { get; private set; } = "";

    /// <summary>Port shown in the list: what the run listens on while active, else the last-seen port (or the pinned one if it never ran).</summary>
    [ObservableProperty] public partial string PortBadge { get; private set; } = "";
    [ObservableProperty] public partial string PortBadgeTooltip { get; private set; } = "";
    /// <summary>The badge shows a port the process is listening on right now.</summary>
    [ObservableProperty] public partial bool IsPortLive { get; private set; }
    /// <summary>The badge shows a remembered port (last seen, or pinned), not a live one.</summary>
    [ObservableProperty] public partial bool IsPortIdle { get; private set; }
    [ObservableProperty] public partial bool IsPortPinned { get; private set; }
    /// <summary>The header's pin button is offered: a dev server we know how to hand a port to, or one already pinned.</summary>
    [ObservableProperty] public partial bool CanPinPort { get; private set; }
    /// <summary>The definition pins a port (what the next start uses; may differ from the running one).</summary>
    [ObservableProperty] public partial bool HasPinnedPort { get; private set; }
    [ObservableProperty] public partial string PinButtonText { get; private set; } = "";
    /// <summary>The idle badge is a last-seen port of an unpinned project (history icon instead of the pin).</summary>
    [ObservableProperty] public partial bool IsPortLastSeen { get; private set; }
    [ObservableProperty] public partial string? Url { get; private set; }
    [ObservableProperty] public partial string? ListeningText { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial bool IsPortConflict { get; private set; }
    [ObservableProperty] public partial bool RestartRequired { get; private set; }
    [ObservableProperty] public partial string? RestartNote { get; private set; }
    [ObservableProperty] public partial string UptimeText { get; private set; } = "";
    [ObservableProperty] public partial string StartCommandText { get; private set; } = "";
    [ObservableProperty] public partial bool AutoRestart { get; private set; }
    [ObservableProperty] public partial bool HasRunningCommands { get; private set; }

    // Plain bools for visibility: x:Bind doesn't call functions with a null argument,
    // so "VisibleIf(Url)" would never collapse again once Url went back to null.
    [ObservableProperty] public partial bool HasPort { get; private set; }
    [ObservableProperty] public partial bool HasUrl { get; private set; }
    [ObservableProperty] public partial bool HasListening { get; private set; }
    [ObservableProperty] public partial bool HasUptime { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial bool HasRestartNote { get; private set; }

    partial void OnPortTextChanged(string value) => HasPort = !string.IsNullOrEmpty(value);
    partial void OnUrlChanged(string? value) => HasUrl = !string.IsNullOrEmpty(value);
    partial void OnListeningTextChanged(string? value) => HasListening = !string.IsNullOrEmpty(value);
    partial void OnUptimeTextChanged(string value) => HasUptime = !string.IsNullOrEmpty(value);
    partial void OnErrorMessageChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnRestartNoteChanged(string? value) => HasRestartNote = !string.IsNullOrEmpty(value);

    /// <summary>package.json scripts and custom commands, as chips.</summary>
    public ObservableCollection<CommandChipViewModel> Chips { get; } = [];

    /// <summary>Hidden package.json scripts (offered in the chip row's "…" menu).</summary>
    public ObservableCollection<string> HiddenScripts { get; } = [];

    /// <summary>Re-evaluates theme-dependent bindings (state brushes are resolved in code).</summary>
    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DisplayState));
    }

    /// <summary>Called from any thread; coalesces bursts into one UI update.</summary>
    public void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    public void Refresh()
    {
        var r = Runtime;
        var def = r.Definition;
        var fw = FrameworkDetector.ById(def.Framework);
        Name = def.Name;
        Path = def.Path;
        FrameworkName = fw.Id == "unknown" ? "기타" : fw.DisplayName;
        State = r.State;
        IsActive = r.IsActive;
        IsStarting = r.State == ProjectState.Starting;
        StateText = r.State switch
        {
            ProjectState.Stopped when r.NextRestartAt is not null => "재시작 대기",
            ProjectState.Crashed when r.NextRestartAt is not null => "재시작 대기",
            ProjectState.Stopped => "정지됨",
            ProjectState.Starting => "시작 중",
            ProjectState.Running => "실행 중",
            ProjectState.Exited => "종료됨",
            ProjectState.Crashed => $"크래시 ({ProjectRuntimeFormat.ExitCode(r.LastExitCode)})",
            _ => r.State.ToString(),
        };

        int? pinned = r.IsActive ? r.RunPort : def.Port;
        bool usesPort = def.Port is not null && (def.PortMode != PortMode.None || def.StartCommand.Contains("{port}"));
        PortText = usesPort && pinned is int p ? $":{p}" : "";
        IsPortPinned = PortText.Length > 0;
        CanPinPort = fw.CanPinPort || def.Port is not null;
        HasPinnedPort = def.Port is not null;
        PinButtonText = def.Port is int dp ? $":{dp}" : "포트 고정";
        Subtitle = FrameworkName;
        UpdatePortBadge(r, usesPort ? pinned : null);
        Url = r.IsActive ? r.BrowserUrl : null;
        ListeningText = r.IsActive && r.ListeningPorts.Count > 0 ? string.Join(", ", r.ListeningPorts.Select(x => ":" + x)) : null;
        ErrorMessage = r.LastError;
        IsPortConflict = r.IsPortConflict;
        RestartRequired = r.RestartRequired;
        RestartNote = r.NextRestartAt is DateTime at ? $"{Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalSeconds))}초 후 자동 재시작" : null;
        StartCommandText = def.StartCommand;
        AutoRestart = def.AutoRestart;
        UpdateUptime();

        HasRunningCommands = r.CommandRuns.Count > 0;
        UpdateChips();
    }

    private void UpdatePortBadge(ProjectRuntime r, int? pinned)
    {
        if (r.IsActive && r.LivePort is int live)
        {
            int extra = r.ListeningPorts.Count - 1;
            PortBadge = extra > 0 ? $":{live} +{extra}" : $":{live}";
            PortBadgeTooltip = "지금 리슨 중: " + string.Join(", ", r.ListeningPorts.Select(x => ":" + x)) + (pinned == live ? " (고정)" : "");
            IsPortLive = true;
            IsPortIdle = false;
            IsPortLastSeen = false;
            return;
        }
        // A pinned port is what the next start uses, so it wins over a (possibly older) last-seen one.
        int? remembered = pinned ?? r.Definition.LastPort;
        PortBadge = remembered is int rp ? $":{rp}" : "";
        PortBadgeTooltip = remembered is null ? "" : pinned is not null ? "고정 포트" : "마지막으로 쓴 포트";
        IsPortLive = false;
        IsPortIdle = remembered is not null;
        IsPortLastSeen = IsPortIdle && pinned is null;
    }

    /// <summary>
    /// The start command's chip mirrors the project itself; every other chip mirrors its own run.
    /// Only one of them runs at a time (see ProjectRuntime).
    /// </summary>
    private void UpdateChips()
    {
        var r = Runtime;
        var def = r.Definition;
        var runs = r.CommandRuns.OrderBy(x => x.StartedAt).ToList();
        var now = DateTime.Now;
        foreach (var chip in Chips)
        {
            if (chip.IsStartScript)
            {
                int? exit = r.State switch
                {
                    ProjectState.Crashed => r.LastExitCode ?? 1,
                    ProjectState.Exited => 0,
                    _ => null,
                };
                chip.Update(r.IsActive, exit);
            }
            else
            {
                chip.Update(runs.Any(x => x.Key == chip.Key), r.GetLastCommandExit(chip.Key));
            }
        }

        // The run bar: what is running (one thing at a time), or else the last state.
        var other = r.IsActive ? null : runs.FirstOrDefault();
        IsRunningAny = r.IsActive || other is not null;
        CanStop = IsRunningAny || r.NextRestartAt is not null;
        DisplayState = other is not null ? ProjectState.Running : r.State;
        RunLabel = r.IsActive ? StartLabel(def) : other?.DisplayName ?? "";
        HasRunLabel = RunLabel.Length > 0;
        RunStateText = other is not null ? "실행 중" : StateText;
        RunCommandLine = r.IsActive ? def.StartCommand : other?.ShellCommand ?? "";
        _runStartedAt = r.IsActive ? r.StartedAt : other?.StartedAt;
        RunElapsed = _runStartedAt is DateTime t ? FormatElapsed(now - t) : "";
    }

    private DateTime? _runStartedAt;

    /// <summary>The start command or any other command is running.</summary>
    [ObservableProperty] public partial bool IsRunningAny { get; private set; }

    /// <summary>Something is running, or an automatic restart is pending (Stop cancels it).</summary>
    [ObservableProperty] public partial bool CanStop { get; private set; }

    /// <summary>The project state for display: "running" also while another command than the start command runs.</summary>
    [ObservableProperty] public partial ProjectState DisplayState { get; private set; }

    /// <summary>Name of what is running ("dev", "build"); empty when nothing is.</summary>
    [ObservableProperty] public partial string RunLabel { get; private set; } = "";
    [ObservableProperty] public partial bool HasRunLabel { get; private set; }
    [ObservableProperty] public partial string RunStateText { get; private set; } = "";
    [ObservableProperty] public partial string RunCommandLine { get; private set; } = "";
    [ObservableProperty] public partial string RunElapsed { get; private set; } = "";

    /// <summary>What to call the start command: its script name ("dev"), else the command itself.</summary>
    private static string StartLabel(ProjectDefinition def) => StartScriptName(def) ?? def.StartCommand;

    public void UpdateUptime()
    {
        if (Runtime.IsActive && Runtime.StartedAt is DateTime started)
        {
            UptimeText = FormatElapsed(DateTime.Now - started);
        }
        else
        {
            UptimeText = "";
        }
        if (Runtime.NextRestartAt is DateTime at)
            RestartNote = $"{Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalSeconds))}초 후 자동 재시작";
        if (_runStartedAt is DateTime t) RunElapsed = FormatElapsed(DateTime.Now - t);
    }

    internal static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Re-reads package.json scripts and custom commands into <see cref="Chips"/>.</summary>
    public void RefreshScripts()
    {
        var def = Runtime.Definition;
        var chips = new List<CommandChipViewModel>();
        var scripts = FrameworkDetector.ReadScripts(def.Path);
        string? startScript = StartScriptName(def);
        HiddenScripts.Clear();
        foreach (var (name, body) in scripts)
        {
            if (def.HiddenScripts.Contains(name))
            {
                HiddenScripts.Add(name);
                continue;
            }
            chips.Add(new CommandChipViewModel(this, ProjectManager.ScriptKey(name), name, body, isScript: true, isStartScript: name == startScript));
        }
        foreach (var cmd in def.Commands.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            chips.Add(new CommandChipViewModel(this, ProjectManager.CommandKey(cmd.Name), cmd.Name, cmd.Command, isScript: false, isStartScript: false));

        // Chips are the only way to start something here, so the start command always has one,
        // even when it isn't a package.json script (python manage.py runserver …).
        if (!chips.Any(c => c.IsStartScript) && !string.IsNullOrWhiteSpace(def.StartCommand))
            chips.Insert(0, new CommandChipViewModel(this, "main", def.StartCommand, def.StartCommand, isScript: false, isStartScript: true));

        Chips.Clear();
        foreach (var c in chips) Chips.Add(c);
        Refresh();
    }

    /// <summary>The script the start command runs ("pnpm run dev" → "dev"), so its chip can be marked.</summary>
    private static string? StartScriptName(ProjectDefinition def)
    {
        var parts = def.StartCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[1] == "run") return parts[2];
        if (parts.Length == 2 && parts[0] is "npm" or "pnpm" or "yarn" or "bun" && parts[1] is "start" or "dev") return parts[1];
        if (parts.Length == 2 && parts[0] is "yarn" or "pnpm" or "bun") return parts[1];
        return null;
    }

    // ---- requests the view handles (dialogs need a XamlRoot)

    public static event Action<ProjectItemViewModel>? EditRequested;
    public static event Action<ProjectItemViewModel>? RemoveRequested;
    public static event Action<ProjectItemViewModel>? AddCommandRequested;

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void AddCustomCommand() => AddCommandRequested?.Invoke(this);

    // ---- commands (pure actions)

    [RelayCommand]
    private Task StartAsync() => _manager.StartAsync(Id);

    [RelayCommand]
    private Task StopAsync() => _manager.StopAsync(Id);

    /// <summary>Restarts whatever is running (the start command, or the other command that runs instead).</summary>
    [RelayCommand]
    private Task RestartAsync()
    {
        if (!Runtime.IsActive && Chips.FirstOrDefault(c => c.IsRunning && !c.IsStartScript) is { } chip) return RunChipAsync(chip);
        return _manager.RestartAsync(Id);
    }

    [RelayCommand]
    private Task ToggleAsync() =>
        Runtime.IsActive || Runtime.CommandRuns.Count > 0 || Runtime.NextRestartAt is not null ? _manager.StopAsync(Id) : _manager.StartAsync(Id);

    [RelayCommand]
    private async Task ReassignPortAndStartAsync()
    {
        if (_manager.ReassignPort(Id) is null) return;
        await _manager.StartAsync(Id);
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        if (Runtime.BrowserUrl is { } url) ShellLauncher.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenInCode() => ShellLauncher.OpenInVsCode(Runtime.Definition.Path);

    [RelayCommand]
    private void OpenFolder() => ShellLauncher.OpenFolder(Runtime.Definition.Path);

    [RelayCommand]
    private void OpenTerminal() => ShellLauncher.OpenTerminal(Runtime.Definition.Path, _manager.Settings.Terminal);

    /// <summary>
    /// Runs the chip's command. Whatever was running is stopped first, so pressing another chip switches
    /// to it and pressing the running chip restarts it.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RunChipAsync(CommandChipViewModel chip)
    {
        // The start command's chip is the project's own run: assigned port, state, auto-restart.
        if (chip.IsStartScript) return Runtime.IsActive ? _manager.RestartAsync(Id) : _manager.StartAsync(Id);
        if (chip.IsScript) return _manager.RunScriptAsync(Id, chip.Label);
        if (Runtime.Definition.Commands.FirstOrDefault(c => c.Name == chip.Label) is { } cmd) return _manager.RunCustomCommandAsync(Id, cmd);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void HideScript(CommandChipViewModel chip)
    {
        if (!chip.IsScript) return;
        var def = Runtime.Definition.Clone();
        if (!def.HiddenScripts.Contains(chip.Label)) def.HiddenScripts.Add(chip.Label);
        _manager.UpdateProject(def);
        RefreshScripts();
    }

    [RelayCommand]
    private void ShowScript(string name)
    {
        var def = Runtime.Definition.Clone();
        def.HiddenScripts.Remove(name);
        _manager.UpdateProject(def);
        RefreshScripts();
    }
}

public sealed partial class CommandChipViewModel : ObservableObject
{
    public CommandChipViewModel(ProjectItemViewModel owner, string key, string label, string command, bool isScript, bool isStartScript)
    {
        Owner = owner;
        Key = key;
        Label = label;
        CommandText = command;
        IsScript = isScript;
        IsStartScript = isStartScript;
    }

    public ProjectItemViewModel Owner { get; }
    public string Key { get; }
    public string Label { get; }
    public string CommandText { get; }
    public bool IsScript { get; }
    public bool IsStartScript { get; }
    public string Tooltip =>
        CommandText + (IsRunning ? "\n실행 중입니다. 다시 누르면 재시작합니다." : IsStartScript ? "\n기본 명령입니다. 목록의 ▶ 버튼과 트레이도 이 명령을 실행합니다." : "\n눌러서 실행");

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    /// <summary>null: never run, 0: succeeded, -1: stopped, other: failed.</summary>
    [ObservableProperty] public partial int? LastExit { get; private set; }

    public bool Succeeded => !IsRunning && LastExit == 0;
    public bool Failed => !IsRunning && LastExit is int c && c != 0 && c != -1;

    public void Update(bool running, int? lastExit)
    {
        IsRunning = running;
        LastExit = lastExit;
        OnPropertyChanged(nameof(Succeeded));
        OnPropertyChanged(nameof(Failed));
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Tooltip));

    [RelayCommand]
    private void Run() => Owner.RunChipCommand.Execute(this);

    [RelayCommand]
    private void Stop() => Owner.StopCommand.Execute(null);

    [RelayCommand]
    private void Hide() => Owner.HideScriptCommand.Execute(this);
}

internal static class ProjectRuntimeFormat
{
    public static string ExitCode(int? code) =>
        code is not int c ? "?" : c is < 0 or > 0xFFFF ? $"0x{unchecked((uint)c):X8}" : $"코드 {c}";
}
