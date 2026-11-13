using System.ComponentModel;
using System.Runtime.InteropServices;
using LocalAppManager.Core.Native;
using static LocalAppManager.Core.Native.NativeMethods;

namespace LocalAppManager.Core.Processes;

/// <summary>
/// A Win32 job object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Every process launched for a project is created directly inside its own job, so
/// terminating the job kills the whole tree (cmd → npm → node → …), and if this app
/// dies for any reason the kernel closes the handle and kills the tree as well.
/// </summary>
public sealed unsafe class JobObject : IDisposable
{
    internal SafeJobHandle Handle { get; }

    public JobObject()
    {
        Handle = CreateJobObject(0, null);
        if (Handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        if (!SetInformationJobObject(Handle, JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            var err = Marshal.GetLastPInvokeError();
            Handle.Dispose();
            throw new Win32Exception(err, "SetInformationJobObject failed");
        }
    }

    /// <summary>Kills every process in the job. Safe to call more than once.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (Handle.IsClosed) return;
        TerminateJobObject(Handle, exitCode);
    }

    /// <summary>Number of processes still alive in the job.</summary>
    public int ActiveProcessCount
    {
        get
        {
            if (Handle.IsClosed) return 0;
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
            return QueryInformationJobObject(Handle, JobObjectBasicAccountingInformation, &info, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), out _)
                ? (int)info.ActiveProcesses
                : 0;
        }
    }

    /// <summary>Ids of the processes currently in the job.</summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        if (Handle.IsClosed) return [];
        // Header is two DWORDs, followed by ULONG_PTR ids. Grow until it fits.
        for (int capacity = 64; capacity <= 65536; capacity *= 4)
        {
            int size = 8 + capacity * IntPtr.Size;
            var mem = NativeMemory.Alloc((nuint)size);
            try
            {
                if (QueryInformationJobObject(Handle, JobObjectBasicProcessIdList, mem, (uint)size, out _))
                {
                    uint inList = ((uint*)mem)[1];
                    var ids = new int[inList];
                    nuint* list = (nuint*)((byte*)mem + 8);
                    for (int i = 0; i < inList; i++) ids[i] = (int)list[i];
                    return ids;
                }
                const int ERROR_MORE_DATA = 234;
                if (Marshal.GetLastPInvokeError() != ERROR_MORE_DATA) return [];
            }
            finally
            {
                NativeMemory.Free(mem);
            }
        }
        return [];
    }

    public void Dispose() => Handle.Dispose();
}
