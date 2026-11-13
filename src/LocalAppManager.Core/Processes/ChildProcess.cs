using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static LocalAppManager.Core.Native.NativeMethods;

namespace LocalAppManager.Core.Processes;

public sealed class ChildProcessOptions
{
    /// <summary>Full command line passed to CreateProcess, e.g. <c>cmd.exe /d /s /c "pnpm dev"</c>.</summary>
    public required string CommandLine { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Complete environment for the child. Null inherits this process's environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Attach the child to a pseudo console (ConPTY) instead of plain pipes.</summary>
    public bool UsePseudoConsole { get; init; }

    public short Columns { get; init; } = 160;
    public short Rows { get; init; } = 40;
}

/// <summary>
/// A process tree started inside its own <see cref="JobObject"/>.
/// The root process is created with <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, so it is in the
/// job from its very first instruction: there is no window in which a grandchild could be
/// spawned outside the job. stdout and stderr share one pipe so their ordering is kept.
/// </summary>
public sealed unsafe class ChildProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly RegisteredWaitHandle _waitRegistration;
    private readonly ProcessWaitHandle _waitHandle;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _pseudoConsole;
    private int _disposed;

    public int ProcessId { get; }
    public JobObject Job { get; }

    /// <summary>Merged stdout/stderr of the tree (or the ConPTY output stream).</summary>
    public Stream Output { get; }

    /// <summary>stdin of the root process.</summary>
    public Stream Input { get; }

    /// <summary>Completes with the root process's exit code once it has exited and the rest of the tree was killed.</summary>
    public Task<int> Exited => _exited.Task;

    public bool UsesPseudoConsole => _pseudoConsole != 0;

    private ChildProcess(int pid, SafeProcessHandle process, JobObject job, Stream output, Stream input, nint pseudoConsole)
    {
        ProcessId = pid;
        _process = process;
        Job = job;
        Output = output;
        Input = input;
        _pseudoConsole = pseudoConsole;
        _waitHandle = new ProcessWaitHandle(process);
        _waitRegistration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => OnRootExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public static ChildProcess Start(ChildProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommandLine);
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"작업 폴더가 없습니다: {options.WorkingDirectory}");

        var job = new JobObject();
        SafeFileHandle? ourOutRead = null, childOutWrite = null, childInRead = null, ourInWrite = null;
        nint pseudoConsole = 0;
        nint attrList = 0;
        nint* handleArrays = null;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
            if (!CreatePipe(out ourOutRead, out childOutWrite, ref sa, 1 << 16)) throw Last("CreatePipe(out)");
            if (!CreatePipe(out childInRead, out ourInWrite, ref sa, 1 << 16)) throw Last("CreatePipe(in)");
            // Our ends must not leak into the child.
            SetHandleInformation(ourOutRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(ourInWrite, HANDLE_FLAG_INHERIT, 0);

            if (options.UsePseudoConsole)
            {
                int hr = CreatePseudoConsole(new COORD { X = options.Columns, Y = options.Rows }, childInRead, childOutWrite, 0, out pseudoConsole);
                if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");
            }

            // Attribute list: job list (+ handle list for pipes, or the pseudo console).
            const int attributeCount = 2;
            nint size = 0;
            InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);
            attrList = (nint)NativeMemory.Alloc((nuint)size);
            if (!InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref size)) throw Last("InitializeProcThreadAttributeList");

            // [0] = job handle, [1..2] = inheritable std handles. Must outlive CreateProcess.
            handleArrays = (nint*)NativeMemory.Alloc((nuint)(3 * sizeof(nint)));
            handleArrays[0] = job.Handle.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &handleArrays[0], sizeof(nint), 0, 0))
                throw Last("UpdateProcThreadAttribute(JOB_LIST)");

            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
            si.lpAttributeList = attrList;
            bool inherit;
            uint flags = CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT;

            if (pseudoConsole != 0)
            {
                if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, (void*)pseudoConsole, sizeof(nint), 0, 0))
                    throw Last("UpdateProcThreadAttribute(PSEUDOCONSOLE)");
                inherit = false;
            }
            else
            {
                handleArrays[1] = childInRead.DangerousGetHandle();
                handleArrays[2] = childOutWrite.DangerousGetHandle();
                if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &handleArrays[1], 2 * sizeof(nint), 0, 0))
                    throw Last("UpdateProcThreadAttribute(HANDLE_LIST)");
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = handleArrays[1];
                si.StartupInfo.hStdOutput = handleArrays[2];
                si.StartupInfo.hStdError = handleArrays[2];
                inherit = true;
                flags |= CREATE_NO_WINDOW;
            }

            char[] cmd = (options.CommandLine + '\0').ToCharArray();
            string? env = options.Environment is null ? null : BuildEnvironmentBlock(options.Environment);
            PROCESS_INFORMATION pi;
            bool ok;
            fixed (char* pCmd = cmd)
            fixed (char* pEnv = env)
            fixed (char* pDir = options.WorkingDirectory)
            {
                ok = CreateProcess(null, pCmd, 0, 0, inherit, flags, pEnv, pDir, &si, &pi);
            }
            if (!ok) throw Last("CreateProcess");

            CloseHandle(pi.hThread);
            // The child has its copies now; ours must close so EOF is seen when the tree exits.
            childOutWrite.Dispose();
            childInRead.Dispose();

            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            var output = new FileStream(ourOutRead, FileAccess.Read, 1, isAsync: false);
            var input = new FileStream(ourInWrite, FileAccess.Write, 1, isAsync: false);
            return new ChildProcess(pi.dwProcessId, process, job, output, input, pseudoConsole);
        }
        catch
        {
            if (pseudoConsole != 0) ClosePseudoConsole(pseudoConsole);
            ourOutRead?.Dispose();
            childOutWrite?.Dispose();
            childInRead?.Dispose();
            ourInWrite?.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (attrList != 0)
            {
                DeleteProcThreadAttributeList(attrList);
                NativeMemory.Free((void*)attrList);
            }
            if (handleArrays != null) NativeMemory.Free(handleArrays);
        }
    }

    /// <summary>Kills the whole process tree. <see cref="Exited"/> completes shortly after.</summary>
    public void Kill() => Job.Terminate(1);

    public void Resize(short columns, short rows)
    {
        if (_pseudoConsole != 0) ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
    }

    private void OnRootExited()
    {
        uint code = GetExitCodeProcess(_process, out var c) ? c : uint.MaxValue;
        // The root (usually cmd.exe) is gone; anything it left behind goes with it.
        Job.Terminate(code == 0 ? 1u : code);
        ReleasePseudoConsole();
        _exited.TrySetResult(unchecked((int)code));
    }

    private void ReleasePseudoConsole()
    {
        var pc = Interlocked.Exchange(ref _pseudoConsole, 0);
        // ClosePseudoConsole can block until its output is drained, so never on the reader's thread.
        if (pc != 0) Task.Run(() => ClosePseudoConsole(pc));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _waitRegistration.Unregister(null);
        Job.Terminate(1);
        ReleasePseudoConsole();
        Job.Dispose();
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        _waitHandle.Dispose();
        _process.Dispose();
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(k) || k.Contains('=') && k.IndexOf('=') != 0) continue;
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }

    private static Win32Exception Last(string what) => new(Marshal.GetLastPInvokeError(), what + " failed");

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
