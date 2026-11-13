using LocalAppManager.Core.Projects;

namespace LocalAppManager.Core.Storage;

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    /// <summary>Ports handed out to projects come from [PortRangeStart, PortRangeEnd].</summary>
    public int PortRangeStart { get; set; } = 4100;
    public int PortRangeEnd { get; set; } = 4999;

    /// <summary>Lines kept per project.</summary>
    public int LogCapacity { get; set; } = 5000;

    /// <summary>The close button hides the window to the tray instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Show a toast when a project crashes.</summary>
    public bool NotifyOnCrash { get; set; } = true;

    /// <summary>Show a one-time hint the first time the window is hidden to the tray.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>"wt" (Windows Terminal), "pwsh", "powershell" or "cmd".</summary>
    public string Terminal { get; set; } = "wt";

    /// <summary>Show timestamps in the log view.</summary>
    public bool ShowTimestamps { get; set; }

    /// <summary>Log font size in DIPs.</summary>
    public double LogFontSize { get; set; } = 12.5;

    public WindowPlacement? Window { get; set; }

    public string? SelectedProjectId { get; set; }

    public double ProjectListWidth { get; set; } = 280;
}

/// <summary>Contents of projects.json.</summary>
public sealed class ProjectsDocument
{
    public int Version { get; set; } = 1;
    public List<ProjectDefinition> Projects { get; set; } = [];
    public List<ProjectGroup> Groups { get; set; } = [];
}
