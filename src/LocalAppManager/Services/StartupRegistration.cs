using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Storage;
using Microsoft.Win32;

namespace LocalAppManager.Services;

/// <summary>
/// "Start at login" for an unpackaged app: a value under HKCU\…\Run (docs/DECISIONS.md D-001).
/// The registry is the source of truth, so turning it off in Task Manager's Startup tab is respected.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static string ValueName => AppPaths.AppName;

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string) return false;
                // Task Manager disables entries via StartupApproved (first byte 0x03 = disabled).
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True if the entry points at a different exe (the app was moved or rebuilt elsewhere).</summary>
    public static bool IsStale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && !string.Equals(s, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            AppLog.Info($"start at login: {enabled}");
        }
        catch (Exception e)
        {
            AppLog.Error("failed to update the Run key", e);
            throw;
        }
    }
}
