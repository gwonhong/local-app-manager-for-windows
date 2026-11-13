using System.Text;
using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;
using LocalAppManager.Core.Processes;

namespace LocalAppManager.Core.Runtime;

/// <summary>
/// One launched command: a <see cref="ChildProcess"/> plus the thread that pumps its output
/// into a <see cref="LogBuffer"/>. Used for both the start command and extra commands.
/// </summary>
internal sealed class ProcessSession : IDisposable
{
    private readonly ChildProcess _process;
    private readonly LogBuffer _log;
    private readonly string? _source;
    private readonly LineDecoder _decoder = new();
    private readonly AnsiParser _ansi = new();
    private readonly object _decoderGate = new();
    private readonly Timer _partialFlush;
    private readonly Thread _reader;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProcessId => _process.ProcessId;
    public DateTime StartedAt { get; } = DateTime.Now;
    public string ShellCommand { get; }
    public bool StopRequested { get; private set; }

    /// <summary>Raised on the reader thread for each URL found in the output.</summary>
    public event Action<UrlDetector.DetectedUrl>? UrlDetected;

    /// <summary>Completes once the tree has exited and every byte of output was logged. Result is the root's exit code.</summary>
    public Task<int> Completion { get; }

    private ProcessSession(ChildProcess process, LogBuffer log, string? source, string shellCommand)
    {
        _process = process;
        _log = log;
        _source = source;
        ShellCommand = shellCommand;
        _partialFlush = new Timer(_ => FlushPartial(), null, Timeout.Infinite, Timeout.Infinite);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"output:{source ?? "start"}:{process.ProcessId}" };
        Completion = CompleteAsync();
        _reader.Start();
    }

    public static ProcessSession Start(string commandLine, string shellCommand, string workingDirectory, IReadOnlyDictionary<string, string> environment, LogBuffer log, string? source)
    {
        var child = ChildProcess.Start(new ChildProcessOptions
        {
            CommandLine = commandLine,
            WorkingDirectory = workingDirectory,
            Environment = environment,
        });
        return new ProcessSession(child, log, source, shellCommand);
    }

    public IReadOnlyList<int> ProcessIds => _process.Job.GetProcessIds();

    public void Kill()
    {
        StopRequested = true;
        _process.Kill();
    }

    /// <summary>
    /// Ctrl+C first, then a hard kill if the tree hasn't exited within <paramref name="timeout"/>.
    /// Returns true if it exited on its own.
    /// </summary>
    public async Task<bool> StopGracefullyAsync(TimeSpan timeout)
    {
        StopRequested = true;
        if (timeout <= TimeSpan.Zero || !_process.TrySendCtrlC())
        {
            _process.Kill();
            return false;
        }
        // Interrupted .cmd shims (npm.cmd, node_modules\.bin\vite.cmd…) make cmd ask "Terminate batch job (Y/N)?",
        // once per shim; ReadLoop answers each prompt while a stop is in progress.
        _answerBatchPrompts = true;
        lock (_decoderGate) AnswerPromptIfAny(_decoder.PeekPartial());
        if (await Task.WhenAny(Completion, Task.Delay(timeout)).ConfigureAwait(false) == Completion) return true;
        _process.Kill();
        return false;
    }

    private volatile bool _answerBatchPrompts;

    // English and Korean wording of cmd's prompt; the "(Y/N)?" part is the same everywhere.
    private static readonly System.Text.RegularExpressions.Regex BatchPrompt = new(@"\((Y/N|y/n)\)\?\s*$");

    private void AnswerPromptIfAny(string text)
    {
        if (_answerBatchPrompts && BatchPrompt.IsMatch(text)) SendInput("Y\r\n");
    }

    public void SendInput(string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _process.Input.Write(bytes);
            _process.Input.Flush();
        }
        catch (IOException)
        {
            // process already gone
        }
    }

    private async Task<int> CompleteAsync()
    {
        int code = await _process.Exited.ConfigureAwait(false);
        // The job is terminated by now, so the pipe reaches EOF promptly. Guard against a
        // handle leaked to some process outside the job keeping it open forever.
        await Task.WhenAny(_drained.Task, Task.Delay(3000)).ConfigureAwait(false);
        return code;
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        var lines = new List<string>(64);
        try
        {
            while (true)
            {
                int n;
                try { n = _process.Output.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (n <= 0) break;

                lock (_decoderGate)
                {
                    _decoder.Feed(buffer.AsSpan(0, n), lines);
                    // The prompt has no trailing newline, so it sits in the partial line.
                    if (_answerBatchPrompts) AnswerPromptIfAny(_decoder.PeekPartial());
                    Publish(lines);
                    // A prompt without a trailing newline ("Ok to proceed? (y)") would stay invisible; flush it if nothing follows soon.
                    _partialFlush.Change(_decoder.HasPartial ? 250 : Timeout.Infinite, Timeout.Infinite);
                }
            }
            lock (_decoderGate)
            {
                _decoder.Flush(lines);
                Publish(lines);
            }
        }
        catch (Exception e)
        {
            _log.Error($"출력 읽기 오류: {e.Message}", _source);
        }
        finally
        {
            _partialFlush.Change(Timeout.Infinite, Timeout.Infinite);
            _drained.TrySetResult();
        }
    }

    private void FlushPartial()
    {
        lock (_decoderGate)
        {
            if (!_decoder.HasPartial) return;
            var lines = new List<string>(1);
            _decoder.Flush(lines);
            Publish(lines);
        }
    }

    private void Publish(List<string> raw)
    {
        if (raw.Count == 0) return;
        var pending = new List<PendingLine>(raw.Count);
        foreach (var r in raw)
        {
            var (text, spans) = _ansi.Parse(r);
            pending.Add(new PendingLine(LogLineKind.Output, text, spans, _source));
            if (UrlDetected is { } handler && UrlDetector.Find(text) is { } url) handler(url);
        }
        raw.Clear();
        _log.AddRange(pending);
    }

    public void Dispose()
    {
        _partialFlush.Dispose();
        _process.Dispose();
    }
}
