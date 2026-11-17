using LocalAppManager.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace LocalAppManager.Services;

/// <summary>
/// Updates through Velopack from this repository's GitHub Releases (docs/DECISIONS.md D-012).
/// Only an installed copy (Setup.exe or the portable zip) can update itself; a dev build reports
/// <see cref="IsSupported"/> = false and the UI hides the update controls.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/gwonhong/local-app-manager-for-windows";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    public UpdateService()
    {
        try
        {
            // A pre-release build follows pre-releases too; a stable build only sees stable releases.
            bool pre = InformationalVersion.Split('+')[0].Contains('-');
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: pre));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception e)
        {
            AppLog.Warn("update manager unavailable", e);
        }
    }

    public bool IsSupported => _manager is not null;

    private static string InformationalVersion =>
        typeof(UpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    /// <summary>The running version: the installed package's, else the build's (without the commit hash).</summary>
    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? InformationalVersion.Split('+')[0];

    /// <summary>A newer version that has been downloaded and is applied when the app quits.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Checks GitHub and downloads a newer version if there is one. Returns its version, or null.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager is null) return null;
        if (_ready is not null) return ReadyVersion;
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _ready = info;
        AppLog.Info($"update {ReadyVersion} downloaded");
        return ReadyVersion;
    }

    /// <summary>
    /// Called on the way out: hands the downloaded update to Velopack's updater, which waits for this
    /// process to exit, swaps the files, and (if <paramref name="restart"/>) starts the new version.
    /// </summary>
    public void ApplyOnExit(bool restart)
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: restart);
            AppLog.Info($"applying update {ReadyVersion} on exit (restart={restart})");
        }
        catch (Exception e)
        {
            AppLog.Error("failed to start the updater", e);
        }
    }
}
