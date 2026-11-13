using System.Text;

namespace LocalAppManager.Core.Diagnostics;

/// <summary>
/// The app's own diagnostic log (not project output): logs\app.log, rotated at 1 MB, 3 files kept.
/// WinUI tends to surface XAML errors as bare COM exceptions, so everything unexpected lands here.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private const long MaxBytes = 1024 * 1024;

    public static string? FilePath => _path;

    public static void Initialize(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        _path = Path.Combine(logsDir, "app.log");
        Info($"---- start pid={Environment.ProcessId} v{typeof(AppLog).Assembly.GetName().Version} os={Environment.OSVersion.Version}");
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? e = null) => Write("WARN ", message, e);

    public static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

    private static void Write(string level, string message, Exception? e)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (e is not null)
        {
            sb.AppendLine();
            sb.Append(e);
            // COM exceptions from XAML carry the useful part in HResult/Message only.
            sb.AppendLine().Append("  HResult=0x").Append(e.HResult.ToString("X8"));
        }
        sb.AppendLine();
        System.Diagnostics.Debug.Write(sb.ToString());
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_path, sb.ToString());
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var info = new FileInfo(_path!);
        if (!info.Exists || info.Length < MaxBytes) return;
        for (int i = 2; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            if (File.Exists(from)) File.Move(from, $"{_path}.{i + 1}", overwrite: true);
        }
        File.Move(_path!, $"{_path}.1", overwrite: true);
    }
}
