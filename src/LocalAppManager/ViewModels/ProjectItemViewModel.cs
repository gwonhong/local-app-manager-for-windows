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
    [ObservableProperty] public partial bool AutoRestart { get; private set; }

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
            // A build or lint that ends is "done"/"failed"; only a server that dies is a crash.
            ProjectState.Exited => r.IsServerRun ? "종료됨" : "완료",
            ProjectState.Crashed => $"{(r.IsServerRun ? "크래시" : "실패")} ({ProjectRuntimeFormat.ExitCode(r.LastExitCode)})",
            _ => r.State.ToString(),
        };

        int? pinned = r.IsActive ? r.RunPort : def.Port;
        bool usesPort = def.Port is not null && (def.PortMode != PortMode.None || def.Commands.Any(c => c.Command.Contains("{port}")));
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
        AutoRestart = def.AutoRestart;
        UpdateUptime();
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

    /// <summary>Each chip mirrors the project's one run when it's the running one, else how it last ended.</summary>
    private void UpdateChips()
    {
        var r = Runtime;
        foreach (var chip in Chips)
            chip.Update(r.IsActive && r.RunKey == chip.Key, r.GetLastExit(chip.Key));

        // The run bar: what is running (one thing at a time), or else how the last run ended.
        IsRunningAny = r.IsActive;
        CanStop = r.IsActive || r.NextRestartAt is not null;
        DisplayState = r.State;
        RunLabel = r.RunName ?? "";
        HasRunLabel = RunLabel.Length > 0 && r.State != ProjectState.Stopped;
        RunStateText = StateText;
        RunCommandLine = r.RunShellCommand ?? "";
        _runStartedAt = r.IsActive ? r.StartedAt : null;
        RunElapsed = _runStartedAt is DateTime t ? FormatElapsed(DateTime.Now - t) : "";
    }

    private DateTime? _runStartedAt;

    /// <summary>A command is running.</summary>
    [ObservableProperty] public partial bool IsRunningAny { get; private set; }

    /// <summary>Something is running, or an automatic restart is pending (Stop cancels it).</summary>
    [ObservableProperty] public partial bool CanStop { get; private set; }

    /// <summary>The project state for display.</summary>
    [ObservableProperty] public partial ProjectState DisplayState { get; private set; }

    /// <summary>Name of what is running ("dev", "build"); empty when nothing is.</summary>
    [ObservableProperty] public partial string RunLabel { get; private set; } = "";
    [ObservableProperty] public partial bool HasRunLabel { get; private set; }
    [ObservableProperty] public partial string RunStateText { get; private set; } = "";
    [ObservableProperty] public partial string RunCommandLine { get; private set; } = "";
    [ObservableProperty] public partial string RunElapsed { get; private set; } = "";

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

    /// <summary>Re-reads the chip list (custom order + package.json scripts) into <see cref="Chips"/>.</summary>
    public void RefreshScripts()
    {
        var commands = _manager.GetCommands(Id);
        string? defaultKey = CommandList.DefaultOf(Runtime.Definition, commands)?.Key;
        Chips.Clear();
        foreach (var c in commands)
            Chips.Add(new CommandChipViewModel(this, c, isDefault: c.Key == defaultKey));
        Refresh();
    }

    // ---- requests the view handles (dialogs need a XamlRoot)

    public static event Action<ProjectItemViewModel>? EditRequested;
    public static event Action<ProjectItemViewModel>? RemoveRequested;
    public static event Action<ProjectItemViewModel>? ManageCommandsRequested;

    /// <summary>
    /// Asks the user before interrupting a running command (title, message); true to go ahead.
    /// Set by the view, which owns the XamlRoot for the dialog.
    /// </summary>
    public static Func<string, string, Task<bool>>? ConfirmInterrupt { get; set; }

    /// <summary>True when nothing runs, or the user agreed to stop what does.</summary>
    private async Task<bool> ConfirmInterruptAsync(string title, string message) =>
        !Runtime.IsActive || ConfirmInterrupt is null || await ConfirmInterrupt(title, message);

    private string RunningName => Runtime.RunName is { Length: > 0 } n ? n : "명령";

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void ManageCommands() => ManageCommandsRequested?.Invoke(this);

    // ---- commands (pure actions)

    [RelayCommand]
    private Task StartAsync() => _manager.StartAsync(Id);

    [RelayCommand]
    private Task StopAsync() => _manager.StopAsync(Id);

    /// <summary>Runs the current (or last) command again.</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!await ConfirmInterruptAsync($"{RunningName}을(를) 재시작할까요?",
                $"{Name}에서 {RunningName}이(가) 실행 중입니다. 정지하고 다시 실행합니다.")) return;
        await _manager.RestartAsync(Id);
    }

    /// <summary>The list's ▶/■: stops what runs, or runs the first chip.</summary>
    [RelayCommand]
    private Task ToggleAsync() =>
        Runtime.IsActive || Runtime.NextRestartAt is not null ? _manager.StopAsync(Id) : _manager.StartAsync(Id);

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
    private async Task RunChipAsync(CommandChipViewModel chip)
    {
        bool same = Runtime.RunKey == chip.Key;
        string title = same ? $"{chip.Label}을(를) 재시작할까요?" : $"{RunningName}을(를) 정지하고 {chip.Label}을(를) 실행할까요?";
        string message = same
            ? $"{Name}에서 {chip.Label}이(가) 실행 중입니다. 정지하고 다시 실행합니다."
            : $"{Name}에서 {RunningName}이(가) 실행 중입니다. 한 번에 하나만 실행되므로 {RunningName}은(는) 정지됩니다.";
        if (!await ConfirmInterruptAsync(title, message)) return;
        await _manager.RunAsync(Id, chip.Key);
    }

    /// <summary>Takes a chip off the row (a script can be put back from the command panel).</summary>
    [RelayCommand]
    private void RemoveChip(CommandChipViewModel chip)
    {
        var rest = _manager.GetCommands(Id).Where(c => c.Key != chip.Key).Select(ToDefinition).ToList();
        // Removing the default chip leaves no default: the first chip takes over.
        string? defaultKey = Runtime.Definition.DefaultCommand == chip.Key ? null : Runtime.Definition.DefaultCommand;
        _manager.SetCommands(Id, rest, defaultKey);
        RefreshScripts();
    }

    /// <summary>Makes this chip the default (what ▶, the tray and groups run).</summary>
    [RelayCommand]
    private void SetDefaultChip(CommandChipViewModel chip)
    {
        _manager.SetDefaultCommand(Id, chip.Key);
        RefreshScripts();
    }

    /// <summary>A resolved chip back into what <see cref="ProjectDefinition.Commands"/> stores.</summary>
    internal static ProjectCommand ToDefinition(ResolvedCommand c) =>
        c.Script is { } s ? new ProjectCommand { Name = s, Script = s } : new ProjectCommand { Name = c.Name, Command = c.ShellCommand };
}

public sealed partial class CommandChipViewModel : ObservableObject
{
    public CommandChipViewModel(ProjectItemViewModel owner, ResolvedCommand command, bool isDefault)
    {
        Owner = owner;
        Key = command.Key;
        Label = command.Name;
        // For a script, show what it really runs (its package.json body), not "pnpm run dev".
        CommandText = command.Script is not null ? $"{command.ShellCommand}  →  {command.Body}" : command.ShellCommand;
        IsScript = command.Script is not null;
        IsDefault = isDefault;
    }

    public ProjectItemViewModel Owner { get; }
    public string Key { get; }
    public string Label { get; }
    public string CommandText { get; }
    public bool IsScript { get; }

    /// <summary>The default chip (★): what the list's ▶, the tray and groups run.</summary>
    public bool IsDefault { get; }
    public bool IsNotDefault => !IsDefault;

    public string Tooltip =>
        CommandText + (IsRunning ? "\n실행 중입니다. 다시 누르면 재시작합니다." : "\n눌러서 실행")
        + (IsDefault ? "\n★ 기본 명령: 목록의 ▶, 트레이, 그룹이 이 명령을 실행합니다." : "");

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
    private void SetDefault() => Owner.SetDefaultChipCommand.Execute(this);

    [RelayCommand]
    private void Stop() => Owner.StopCommand.Execute(null);

    [RelayCommand]
    private void Remove() => Owner.RemoveChipCommand.Execute(this);
}

internal static class ProjectRuntimeFormat
{
    public static string ExitCode(int? code) =>
        code is not int c ? "?" : c is < 0 or > 0xFFFF ? $"0x{unchecked((uint)c):X8}" : $"코드 {c}";
}
