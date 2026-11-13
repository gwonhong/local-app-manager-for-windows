using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;
using LocalAppManager.Core.Processes;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Storage;

namespace LocalAppManager.Core.Runtime;

/// <summary>
/// The single owner of all project state: definitions, groups, settings and runtimes.
/// The UI only reads from it and calls its methods; it never keeps its own copy of the truth.
/// Events can fire on any thread.
/// </summary>
public sealed class ProjectManager
{
    private readonly object _gate = new();
    private readonly List<ProjectRuntime> _projects = [];
    private readonly List<ProjectGroup> _groups = [];
    private readonly PortAllocator _allocator;

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; } = new();

    public ProjectManager(AppPaths paths, PortAllocator? allocator = null)
    {
        Paths = paths;
        _allocator = allocator ?? new PortAllocator();
    }

    /// <summary>Projects or groups were added, removed, renamed or reordered.</summary>
    public event Action? ProjectsChanged;

    /// <summary>A project's runtime state changed.</summary>
    public event Action<ProjectRuntime>? ProjectChanged;

    /// <summary>A project's start command crashed (exit code passed along).</summary>
    public event Action<ProjectRuntime, int>? ProjectCrashed;

    public IReadOnlyList<ProjectRuntime> Projects
    {
        get { lock (_gate) return _projects.ToList(); }
    }

    public IReadOnlyList<ProjectGroup> Groups
    {
        get { lock (_gate) return _groups.ToList(); }
    }

    public ProjectRuntime? Get(string id)
    {
        lock (_gate) return _projects.FirstOrDefault(p => p.Id == id);
    }

    // ------------------------------------------------------------------ persistence

    public void Load()
    {
        Settings = JsonStore.Load<AppSettings>(Paths.SettingsFile);
        var doc = JsonStore.Load<ProjectsDocument>(Paths.ProjectsFile);
        lock (_gate)
        {
            _projects.Clear();
            foreach (var def in doc.Projects)
            {
                Normalize(def);
                _projects.Add(CreateRuntime(def));
            }
            _groups.Clear();
            _groups.AddRange(doc.Groups);
        }
        AppLog.Info($"loaded {doc.Projects.Count} projects, {doc.Groups.Count} groups from {Paths.ProjectsFile}");
        ProjectsChanged?.Invoke();
    }

    public void SaveProjects()
    {
        ProjectsDocument doc;
        lock (_gate)
            doc = new ProjectsDocument
            {
                Projects = _projects.Select(p => p.Definition).ToList(),
                Groups = _groups.Select(g => new ProjectGroup { Id = g.Id, Name = g.Name, ProjectIds = [.. g.ProjectIds] }).ToList(),
            };
        try
        {
            JsonStore.Save(Paths.ProjectsFile, doc);
        }
        catch (Exception e)
        {
            AppLog.Error("projects.json 저장 실패", e);
        }
    }

    public void SaveSettings()
    {
        try
        {
            JsonStore.Save(Paths.SettingsFile, Settings);
        }
        catch (Exception e)
        {
            AppLog.Error("settings.json 저장 실패", e);
        }
    }

    public void UpdateSettings(Action<AppSettings> change)
    {
        change(Settings);
        if (Settings.PortRangeEnd < Settings.PortRangeStart)
            (Settings.PortRangeStart, Settings.PortRangeEnd) = (Settings.PortRangeEnd, Settings.PortRangeStart);
        Settings.LogCapacity = Math.Clamp(Settings.LogCapacity, 500, 100_000);
        foreach (var p in Projects) p.Log.Resize(Settings.LogCapacity);
        SaveSettings();
    }

    private static void Normalize(ProjectDefinition def)
    {
        def.Commands ??= [];
        def.HiddenScripts ??= [];
        def.Environment = new Dictionary<string, string>(def.Environment ?? [], StringComparer.OrdinalIgnoreCase);
        def.PortArgs ??= "--port {port}";
        def.PortEnvVar ??= "PORT";
        def.BrowserPath ??= "/";
        def.StartCommand ??= "";
        def.Name = string.IsNullOrWhiteSpace(def.Name) ? Path.GetFileName(def.Path.TrimEnd('\\')) : def.Name;
    }

    private ProjectRuntime CreateRuntime(ProjectDefinition def)
    {
        var runtime = new ProjectRuntime(def, new LogBuffer(Settings.LogCapacity), () => EnvironmentBuilder.CreateBase());
        runtime.Changed += r => ProjectChanged?.Invoke(r);
        runtime.Crashed += (r, code) =>
        {
            AppLog.Warn($"project '{r.Definition.Name}' crashed with code {code}");
            ProjectCrashed?.Invoke(r, code);
        };
        return runtime;
    }

    // ------------------------------------------------------------------ projects

    /// <summary>Detects the framework in <paramref name="path"/>, assigns a port and registers it.</summary>
    public ProjectRuntime AddProject(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        lock (_gate)
        {
            var existing = _projects.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Definition.Path).TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }

        var detection = FrameworkDetector.Detect(path);
        var def = new ProjectDefinition
        {
            Name = UniqueName(detection.Name),
            Path = path,
            Framework = detection.Framework.Id,
            PackageManager = detection.PackageManager,
            StartCommand = detection.StartCommand,
            PortMode = detection.Framework.PortMode,
            PortArgs = detection.Framework.PortArgs,
        };
        if (detection.UsesPort) def.Port = AllocatePort(null);

        var runtime = CreateRuntime(def);
        lock (_gate) _projects.Add(runtime);
        AppLog.Info($"added project '{def.Name}' ({def.Framework}, {def.PackageManager ?? "-"}) port={def.Port} cmd='{def.StartCommand}'");
        runtime.Log.System($"등록했습니다: {detection.Framework.DisplayName}" + (def.PackageManager is { } pm ? $" · {pm}" : "") + (def.Port is int port ? $" · 포트 {port}" : ""));
        SaveProjects();
        ProjectsChanged?.Invoke();
        return runtime;
    }

    private string UniqueName(string name)
    {
        lock (_gate)
        {
            string candidate = name;
            for (int i = 2; _projects.Any(p => p.Definition.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
                candidate = $"{name} ({i})";
            return candidate;
        }
    }

    /// <summary>Replaces a project's definition (from the settings dialog).</summary>
    public void UpdateProject(ProjectDefinition updated)
    {
        var runtime = Get(updated.Id) ?? throw new InvalidOperationException("unknown project");
        Normalize(updated);
        var old = runtime.Definition;
        runtime.OnDefinitionChanged(old, updated.Clone());
        SaveProjects();
        if (old.Name != updated.Name) ProjectsChanged?.Invoke();
    }

    public async Task RemoveProjectAsync(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return;
        await runtime.StopAsync().ConfigureAwait(false);
        runtime.KillAll();
        lock (_gate)
        {
            _projects.Remove(runtime);
            foreach (var g in _groups) g.ProjectIds.Remove(id);
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public void MoveProject(string id, int newIndex)
    {
        lock (_gate)
        {
            int index = _projects.FindIndex(p => p.Id == id);
            if (index < 0) return;
            var item = _projects[index];
            _projects.RemoveAt(index);
            _projects.Insert(Math.Clamp(newIndex, 0, _projects.Count), item);
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    /// <summary>Lowest free port in the app's range not assigned to another project.</summary>
    public int? AllocatePort(string? forProjectId, int? preferAfter = null)
    {
        List<int> assigned;
        lock (_gate)
            assigned = _projects.Where(p => p.Id != forProjectId && p.Definition.Port is not null).Select(p => p.Definition.Port!.Value).ToList();
        return _allocator.Allocate(Settings.PortRangeStart, Settings.PortRangeEnd, assigned, preferAfter);
    }

    /// <summary>Gives the project a new port (after a conflict) and saves it.</summary>
    public int? ReassignPort(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return null;
        int? port = AllocatePort(id, runtime.Definition.Port);
        if (port is null) return null;
        var def = runtime.Definition.Clone();
        int? old = def.Port;
        def.Port = port;
        runtime.OnDefinitionChanged(runtime.Definition, def);
        runtime.ClearError();
        runtime.Log.System($"포트를 {old?.ToString() ?? "-"} → {port}(으)로 바꿨습니다.");
        SaveProjects();
        return port;
    }

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync(string id) => Get(id)?.StartAsync() ?? Task.CompletedTask;

    public Task StopAsync(string id) => Get(id)?.StopAsync() ?? Task.CompletedTask;

    public Task RestartAsync(string id) => Get(id)?.RestartAsync() ?? Task.CompletedTask;

    public Task StopAllAsync() => Task.WhenAll(Projects.Select(p => p.StopAsync()));

    /// <summary>Kills every process tree right away (app exit). Job objects would do it anyway when the process dies.</summary>
    public void KillAll()
    {
        foreach (var p in Projects) p.KillAll();
    }

    /// <summary>Starts the projects marked "start with app".</summary>
    public Task StartAutoStartProjectsAsync() =>
        Task.WhenAll(Projects.Where(p => p.Definition.StartWithApp).Select(p => p.StartAsync()));

    // ------------------------------------------------------------------ commands

    public static string ScriptKey(string script) => "script:" + script;

    public static string CommandKey(string name) => "cmd:" + name;

    public void RunScript(string id, string script)
    {
        var runtime = Get(id);
        if (runtime is null) return;
        runtime.RunCommand(ScriptKey(script), script, CommandBuilder.ScriptCommand(runtime.Definition, script));
    }

    public void RunCustomCommand(string id, ProjectCommand command)
    {
        var runtime = Get(id);
        if (runtime is null || string.IsNullOrWhiteSpace(command.Command)) return;
        runtime.RunCommand(CommandKey(command.Name), command.Name, command.Command);
    }

    public void StopCommand(string id, string key) => Get(id)?.StopCommand(key);

    // ------------------------------------------------------------------ groups

    public ProjectGroup AddGroup(string name, IEnumerable<string> projectIds)
    {
        var group = new ProjectGroup { Name = name.Trim(), ProjectIds = projectIds.Distinct().ToList() };
        lock (_gate) _groups.Add(group);
        SaveProjects();
        ProjectsChanged?.Invoke();
        return group;
    }

    public void UpdateGroup(string groupId, string name, IEnumerable<string> projectIds)
    {
        lock (_gate)
        {
            var g = _groups.FirstOrDefault(x => x.Id == groupId);
            if (g is null) return;
            g.Name = name.Trim();
            g.ProjectIds = projectIds.Distinct().ToList();
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public void RemoveGroup(string groupId)
    {
        lock (_gate) _groups.RemoveAll(g => g.Id == groupId);
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public IReadOnlyList<ProjectRuntime> GroupMembers(string groupId)
    {
        lock (_gate)
        {
            var g = _groups.FirstOrDefault(x => x.Id == groupId);
            if (g is null) return [];
            return g.ProjectIds.Select(id => _projects.FirstOrDefault(p => p.Id == id)).OfType<ProjectRuntime>().ToList();
        }
    }

    public Task StartGroupAsync(string groupId) => Task.WhenAll(GroupMembers(groupId).Select(p => p.StartAsync()));

    public Task StopGroupAsync(string groupId) => Task.WhenAll(GroupMembers(groupId).Select(p => p.StopAsync()));
}
