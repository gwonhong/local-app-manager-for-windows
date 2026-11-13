using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Services;
using Microsoft.UI.Dispatching;

namespace LocalAppManager.ViewModels;

public sealed partial class GroupViewModel : ObservableObject
{
    private readonly ProjectManager _manager;

    public GroupViewModel(ProjectManager manager, ProjectGroup group)
    {
        _manager = manager;
        Id = group.Id;
        Update(group);
    }

    public string Id { get; }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial bool AnyActive { get; private set; }
    [ObservableProperty] public partial bool AllActive { get; private set; }

    public IReadOnlyList<string> ProjectIds { get; private set; } = [];

    public void Update(ProjectGroup group)
    {
        Name = group.Name;
        ProjectIds = [.. group.ProjectIds];
        RefreshState();
    }

    public void RefreshState()
    {
        var members = _manager.GroupMembers(Id);
        int active = members.Count(m => m.IsActive);
        AnyActive = active > 0;
        AllActive = members.Count > 0 && active == members.Count;
        Summary = members.Count == 0 ? "프로젝트 없음" : $"{active}/{members.Count} 실행 중";
    }

    public static event Action<GroupViewModel>? EditRequested;
    public static event Action<GroupViewModel>? RemoveRequested;

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private Task ToggleAsync() => AnyActive ? _manager.StopGroupAsync(Id) : _manager.StartGroupAsync(Id);

    [RelayCommand]
    private Task StartAllAsync() => _manager.StartGroupAsync(Id);

    [RelayCommand]
    private Task StopAllAsync() => _manager.StopGroupAsync(Id);
}

/// <summary>Root view model. Mirrors <see cref="ProjectManager"/> into observable collections.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _clock;
    private int _syncQueued;

    public MainViewModel(ProjectManager manager)
    {
        Manager = manager;
        Log = new LogViewModel { ShowTimestamps = manager.Settings.ShowTimestamps };
        manager.ProjectsChanged += () => Ui.Run(SyncProjects);
        manager.ProjectChanged += OnProjectChanged;

        _clock = Ui.Queue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) =>
        {
            foreach (var p in Projects)
                if (p.IsActive || p.RestartNote is not null) p.UpdateUptime();
        };
        _clock.Start();

        SyncProjects();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == manager.Settings.SelectedProjectId) ?? Projects.FirstOrDefault();
    }

    public ProjectManager Manager { get; }
    public LogViewModel Log { get; }
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<GroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial ProjectItemViewModel? SelectedProject { get; set; }

    [ObservableProperty] public partial bool HasProjects { get; private set; }
    [ObservableProperty] public partial bool HasGroups { get; private set; }
    [ObservableProperty] public partial int ActiveCount { get; private set; }

    /// <summary>Raised when the number of running projects (or the project list) changes, for the tray.</summary>
    public event Action? TrayStateChanged;

    [ObservableProperty] public partial bool HasSelection { get; private set; }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        HasSelection = value is not null;
        Log.Attach(value?.Runtime.Log);
        value?.RefreshScripts();
        if (Manager.Settings.SelectedProjectId != value?.Id)
        {
            Manager.Settings.SelectedProjectId = value?.Id;
            Manager.SaveSettings();
        }
    }

    private void OnProjectChanged(ProjectRuntime runtime)
    {
        var vm = Find(runtime.Id);
        vm?.QueueRefresh();
        if (Interlocked.Exchange(ref _syncQueued, 1) == 1) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _syncQueued, 0);
            foreach (var g in Groups) g.RefreshState();
            int active = Projects.Count(p => p.Runtime.IsActive);
            if (active != ActiveCount) ActiveCount = active;
            TrayStateChanged?.Invoke();
        });
    }

    public ProjectItemViewModel? Find(string id)
    {
        foreach (var p in Projects)
            if (p.Id == id) return p;
        return null;
    }

    /// <summary>Brings the collections in line with the manager, keeping existing view models (and selection).</summary>
    private void SyncProjects()
    {
        var runtimes = Manager.Projects;
        var wanted = runtimes.Select(r => r.Id).ToList();
        for (int i = Projects.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Projects[i].Id)) Projects.RemoveAt(i);
        for (int i = 0; i < runtimes.Count; i++)
        {
            var existing = Find(runtimes[i].Id);
            if (existing is null)
            {
                Projects.Insert(i, new ProjectItemViewModel(Manager, runtimes[i]));
            }
            else
            {
                int at = Projects.IndexOf(existing);
                if (at != i) Projects.Move(at, i);
                existing.Refresh();
            }
        }

        var groups = Manager.Groups;
        for (int i = Groups.Count - 1; i >= 0; i--)
            if (groups.All(g => g.Id != Groups[i].Id)) Groups.RemoveAt(i);
        for (int i = 0; i < groups.Count; i++)
        {
            var existing = Groups.FirstOrDefault(g => g.Id == groups[i].Id);
            if (existing is null) Groups.Insert(i, new GroupViewModel(Manager, groups[i]));
            else
            {
                existing.Update(groups[i]);
                int at = Groups.IndexOf(existing);
                if (at != i) Groups.Move(at, i);
            }
        }

        HasProjects = Projects.Count > 0;
        HasGroups = Groups.Count > 0;
        ActiveCount = Projects.Count(p => p.Runtime.IsActive);
        if (SelectedProject is not null && !Projects.Contains(SelectedProject)) SelectedProject = Projects.FirstOrDefault();
        TrayStateChanged?.Invoke();
    }

    public ProjectItemViewModel? AddProject(string folder)
    {
        var runtime = Manager.AddProject(folder);
        SyncProjects();
        var vm = Find(runtime.Id);
        SelectedProject = vm;
        return vm;
    }

    public async Task RemoveProjectAsync(ProjectItemViewModel project)
    {
        int index = Projects.IndexOf(project);
        await Manager.RemoveProjectAsync(project.Id);
        Ui.Run(() =>
        {
            SyncProjects();
            if (Projects.Count > 0) SelectedProject = Projects[Math.Clamp(index, 0, Projects.Count - 1)];
        });
    }

    [RelayCommand]
    private Task StopAllAsync() => Manager.StopAllAsync();

    public void SetLogTimestamps(bool on)
    {
        Log.ShowTimestamps = on;
        Manager.UpdateSettings(s => s.ShowTimestamps = on);
    }
}
