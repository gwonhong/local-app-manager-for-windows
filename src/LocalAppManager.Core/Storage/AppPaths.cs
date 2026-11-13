namespace LocalAppManager.Core.Storage;

/// <summary>Where the app keeps its files. Everything lives under one folder in %LOCALAPPDATA%.</summary>
public sealed class AppPaths
{
    /// <summary>Temporary app name; change it here (and in the csproj) when the real name is decided.</summary>
    public const string AppName = "LocalAppManager";

    /// <summary>Overrides the data folder (tests, or running a second copy side by side).</summary>
    public const string DataDirOverrideVariable = "LOCALAPPMANAGER_DATA";

    public string DataDir { get; }
    public string ProjectsFile => Path.Combine(DataDir, "projects.json");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string LogsDir => Path.Combine(DataDir, "logs");

    public AppPaths(string? dataDir = null)
    {
        DataDir = dataDir
            ?? (Environment.GetEnvironmentVariable(DataDirOverrideVariable) is { Length: > 0 } o ? o : null)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
