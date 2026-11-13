using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAppManager.Core.Logs;
using LocalAppManager.Services;
using Microsoft.UI.Dispatching;

namespace LocalAppManager.ViewModels;

/// <summary>One row in the log view. Immutable; options are baked in and the row is rebuilt when they change.</summary>
public sealed class LogLineViewModel(LogLine line, string? highlight, bool showTimestamp)
{
    public LogLine Line { get; } = line;
    public string? Highlight { get; } = highlight;
    public bool ShowTimestamp { get; } = showTimestamp;
    public long Seq => Line.Seq;
}

/// <summary>
/// Mirrors a project's <see cref="LogBuffer"/> into an observable list for the view.
/// It polls the buffer every 50 ms on the UI thread instead of reacting to each line,
/// so a chatty dev server costs one UI update per tick, not one per line.
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private const int RebuildThreshold = 800;
    private readonly DispatcherQueueTimer _timer;
    private LogBuffer? _buffer;
    private long _nextSeq;
    private int _generation;

    public LogViewModel()
    {
        _timer = Ui.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<LogLineViewModel> Lines { get; private set; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowTimestamps { get; set; }

    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    [ObservableProperty]
    public partial bool WrapLines { get; set; } = true;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Raised after new rows were appended (the view scrolls to the end if following).</summary>
    public event Action? Appended;

    /// <summary>Raised after the whole list was replaced.</summary>
    public event Action? Rebuilt;

    public void Attach(LogBuffer? buffer)
    {
        _buffer = buffer;
        Rebuild();
        if (buffer is null) _timer.Stop();
        else _timer.Start();
    }

    /// <summary>Pause polling while the window is hidden; catch up on resume.</summary>
    public void SetActive(bool active)
    {
        if (active && _buffer is not null)
        {
            Tick();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnShowTimestampsChanged(bool value) => Rebuild();

    private bool Matches(LogLine line) =>
        string.IsNullOrEmpty(Filter)
        || line.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || (line.Source?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private LogLineViewModel Wrap(LogLine line) => new(line, string.IsNullOrEmpty(Filter) ? null : Filter, ShowTimestamps);

    public void Rebuild()
    {
        if (_buffer is null)
        {
            Lines = [];
            _nextSeq = 0;
        }
        else
        {
            _generation = _buffer.Generation;
            var snapshot = _buffer.Snapshot();
            Lines = new ObservableCollection<LogLineViewModel>(snapshot.Where(Matches).Select(Wrap));
            _nextSeq = snapshot.Count > 0 ? snapshot[^1].Seq + 1 : _buffer.NextSeq;
        }
        OnPropertyChanged(nameof(Lines));
        UpdateSummary();
        Rebuilt?.Invoke();
    }

    private void Tick()
    {
        var buffer = _buffer;
        if (buffer is null) return;
        if (buffer.Generation != _generation)
        {
            Rebuild();
            return;
        }

        var fresh = buffer.GetSince(_nextSeq);
        long firstRetained = buffer.FirstSeq;
        int stale = 0;
        while (stale < Lines.Count && Lines[stale].Seq < firstRetained) stale++;
        if (fresh.Count == 0 && stale == 0) return;

        if (fresh.Count > RebuildThreshold || stale > RebuildThreshold)
        {
            Rebuild();
            return;
        }

        for (int i = 0; i < stale; i++) Lines.RemoveAt(0);
        foreach (var line in fresh)
            if (Matches(line)) Lines.Add(Wrap(line));
        if (fresh.Count > 0) _nextSeq = fresh[^1].Seq + 1;
        UpdateSummary();
        Appended?.Invoke();
    }

    private void UpdateSummary()
    {
        int total = _buffer?.Count ?? 0;
        Summary = string.IsNullOrEmpty(Filter)
            ? $"{total:N0}줄"
            : $"{Lines.Count:N0} / {total:N0}줄 일치";
    }

    /// <summary>Plain text of the rows currently shown (respects the filter).</summary>
    public string VisibleText()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines) AppendLine(sb, l.Line);
        return sb.ToString();
    }

    public static string LineText(LogLine line)
    {
        var sb = new StringBuilder();
        AppendLine(sb, line);
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendLine(StringBuilder sb, LogLine line)
    {
        if (line.Source is not null) sb.Append('[').Append(line.Source).Append("] ");
        sb.AppendLine(line.Text);
    }

    public void Clear() => _buffer?.Clear();

    public void Dispose() => _timer.Stop();
}
