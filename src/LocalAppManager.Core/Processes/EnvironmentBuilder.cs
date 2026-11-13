using System.Collections;
using Microsoft.Win32;

namespace LocalAppManager.Core.Processes;

/// <summary>
/// Builds the environment for child processes.
/// It starts from this process's environment and then re-reads the user and machine
/// variables from the registry (as Explorer does). If the app stays running in the tray,
/// it still picks up a PATH change, e.g. after installing a new Node or Python.
/// </summary>
public static class EnvironmentBuilder
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = "Environment";

    public static Dictionary<string, string> CreateBase(bool refreshFromRegistry = true)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";

        if (!refreshFromRegistry) return env;
        try
        {
            var machine = ReadKey(Registry.LocalMachine, MachineKey);
            var user = ReadKey(Registry.CurrentUser, UserKey);
            foreach (var (k, v) in machine) env[k] = v;
            foreach (var (k, v) in user)
            {
                if (k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) continue;
                env[k] = v;
            }
            machine.TryGetValue("PATH", out var mp);
            user.TryGetValue("PATH", out var up);
            if (mp is not null || up is not null)
                env["PATH"] = string.Join(';', new[] { mp, up }.Where(s => !string.IsNullOrEmpty(s)));
        }
        catch
        {
            // Registry access is best-effort; the inherited environment is a fine fallback.
        }
        return env;
    }

    private static Dictionary<string, string> ReadKey(RegistryKey root, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = root.OpenSubKey(path);
        if (key is null) return result;
        foreach (var name in key.GetValueNames())
        {
            // Values are read unexpanded and expanded with the current environment, so
            // REG_EXPAND_SZ entries like %USERPROFILE%\bin resolve correctly.
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is string s) result[name] = Environment.ExpandEnvironmentVariables(s);
        }
        return result;
    }
}
