using System.Text.RegularExpressions;

namespace LocalAppManager.Core.Ports;

/// <summary>Hands out fixed ports from the app's range, skipping ports already assigned or in use.</summary>
public sealed class PortAllocator
{
    private readonly Func<int, bool> _isFree;

    public PortAllocator(Func<int, bool>? isFree = null) => _isFree = isFree ?? PortProbe.IsFree;

    /// <summary>
    /// The lowest port in [start, end] that isn't in <paramref name="assigned"/> and is free right now.
    /// Returns null if the range is exhausted.
    /// </summary>
    public int? Allocate(int start, int end, IEnumerable<int> assigned, int? preferAfter = null)
    {
        var taken = assigned.ToHashSet();
        int first = preferAfter is { } p && p >= start && p < end ? p + 1 : start;
        // Scan from `first` to the end, then wrap around.
        for (int i = 0; i <= end - start; i++)
        {
            int port = start + (first - start + i) % (end - start + 1);
            if (taken.Contains(port)) continue;
            if (_isFree(port)) return port;
        }
        return null;
    }
}

/// <summary>Finds "Local: http://localhost:5173/" style URLs in log lines.</summary>
public static partial class UrlDetector
{
    // Hosts that mean "this machine". 0.0.0.0 / [::] are bind-all addresses that we show as localhost.
    [GeneratedRegex(@"\bhttps?://(?<host>localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]|\[::\]|[a-z0-9][a-z0-9.-]*\.localhost|(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{2,5})(?<path>/[^\s'""<>)\]]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public readonly record struct DetectedUrl(string Url, string Host, int Port, bool IsLocal);

    /// <summary>The first local URL in the line, normalized to something a browser can open.</summary>
    public static DetectedUrl? Find(string plainText)
    {
        if (plainText.IndexOf("://", StringComparison.Ordinal) < 0) return null;
        DetectedUrl? fallback = null;
        foreach (Match m in UrlRegex().Matches(plainText))
        {
            if (!int.TryParse(m.Groups["port"].Value, out int port) || port is <= 0 or > 65535) continue;
            string host = m.Groups["host"].Value.ToLowerInvariant();
            bool isLocal = host is "localhost" or "127.0.0.1" or "0.0.0.0" or "[::1]" or "[::]" or "[:]" || host.EndsWith(".localhost", StringComparison.Ordinal);
            string shownHost = host is "0.0.0.0" or "[::]" or "[:]" ? "localhost" : host;
            string path = m.Groups["path"].Value.TrimEnd('.', ',', ';');
            var url = new DetectedUrl($"{(m.Value.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http")}://{shownHost}:{port}{path}", host, port, isLocal);
            if (isLocal) return url;
            fallback ??= url; // a LAN address ("Network: http://192.168.0.2:5173") only if nothing local
        }
        return fallback;
    }
}
