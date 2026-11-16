namespace LocalAppManager.Core.Logs;

public enum LogLineKind : byte
{
    /// <summary>Output of the project's process.</summary>
    Output,
    /// <summary>Informational message from this app (started, stopped, port…).</summary>
    System,
    /// <summary>An error reported by this app.</summary>
    Error,
}

/// <summary>
/// One immutable log line. <see cref="Spans"/> is empty when the line has no styling.
/// <see cref="Source"/> names the command that printed it (null for the start command).
/// </summary>
public sealed record LogLine(long Seq, DateTime Time, LogLineKind Kind, string Text, AnsiSpan[] Spans, string? Source = null);

public readonly record struct PendingLine(LogLineKind Kind, string Text, AnsiSpan[] Spans, string? Source = null);

/// <summary>
/// Fixed-capacity, thread-safe ring buffer of log lines.
/// Lines get increasing sequence numbers, so readers can poll for "everything after N"
/// (the UI does this on a timer, which batches updates for free) and can tell which
/// lines fell off the front.
/// </summary>
public sealed class LogBuffer
{
    private readonly object _gate = new();
    private LogLine?[] _ring;
    private long _nextSeq;
    private int _count;
    private int _generation;

    public LogBuffer(int capacity = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new LogLine?[capacity];
    }

    public int Capacity
    {
        get { lock (_gate) return _ring.Length; }
    }

    /// <summary>Changes every time <see cref="Clear"/> is called.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Raised (on the writer's thread) after lines were added. Payload is the last sequence number.</summary>
    public event Action<long>? Appended;

    public event Action? Cleared;

    public long NextSeq
    {
        get { lock (_gate) return _nextSeq; }
    }

    /// <summary>Sequence number of the oldest retained line.</summary>
    public long FirstSeq
    {
        get { lock (_gate) return _nextSeq - _count; }
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public LogLine Add(LogLineKind kind, string text, AnsiSpan[] spans, string? source = null)
    {
        LogLine line;
        lock (_gate)
        {
            line = new LogLine(_nextSeq, DateTime.Now, kind, text, spans, source);
            Store(line);
        }
        Appended?.Invoke(line.Seq);
        return line;
    }

    public void AddRange(IReadOnlyList<PendingLine> lines)
    {
        if (lines.Count == 0) return;
        long last;
        lock (_gate)
        {
            var now = DateTime.Now;
            foreach (var p in lines) Store(new LogLine(_nextSeq, now, p.Kind, p.Text, p.Spans, p.Source));
            last = _nextSeq - 1;
        }
        Appended?.Invoke(last);
    }

    private void Store(LogLine line)
    {
        _ring[_nextSeq % _ring.Length] = line;
        _nextSeq++;
        if (_count < _ring.Length) _count++;
    }

    public LogLine System(string text, string? source = null) => Add(LogLineKind.System, text, [], source);

    public LogLine Error(string text, string? source = null) => Add(LogLineKind.Error, text, [], source);

    /// <summary>Lines with <c>Seq &gt;= fromSeq</c> that are still retained, oldest first.</summary>
    public List<LogLine> GetSince(long fromSeq, int max = int.MaxValue)
    {
        lock (_gate)
        {
            long first = _nextSeq - _count;
            long start = Math.Max(fromSeq, first);
            long n = Math.Max(0, Math.Min(_nextSeq - start, max));
            var result = new List<LogLine>((int)n);
            for (long s = start; s < start + n; s++) result.Add(_ring[s % _ring.Length]!);
            return result;
        }
    }

    public List<LogLine> Snapshot() => GetSince(0);

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _count = 0;
            _generation++;
        }
        Cleared?.Invoke();
    }

    /// <summary>Changes the capacity, keeping the newest lines.</summary>
    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_gate)
        {
            if (capacity == _ring.Length) return;
            int keep = Math.Min(_count, capacity);
            var next = new LogLine?[capacity];
            for (long s = _nextSeq - keep; s < _nextSeq; s++) next[s % capacity] = _ring[s % _ring.Length];
            _ring = next;
            _count = keep;
        }
    }
}
