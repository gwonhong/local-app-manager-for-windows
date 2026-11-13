using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LocalAppManager.Core.Ports;

public sealed record PortOwner(int ProcessId, string? ProcessName, IPEndPoint Endpoint);

/// <summary>Checks whether a TCP port can be used and who is holding it.</summary>
public static partial class PortProbe
{
    /// <summary>
    /// True if nothing listens on the port (any IPv4/IPv6 address) and we can bind it ourselves.
    /// Binding catches what the listener table doesn't: Hyper-V/WSL excluded port ranges
    /// and ports reserved by other means.
    /// </summary>
    public static bool IsFree(int port)
    {
        if (port is <= 0 or > 65535) return false;
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(l => l.Port == port)) return false;
        }
        catch (NetworkInformationException)
        {
            // fall through to the bind test
        }
        return CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.Any, port) && (!Socket.OSSupportsIPv6 || CanBind(IPAddress.IPv6Loopback, port));
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>True if something accepts TCP connections on localhost:port (IPv4 or IPv6).</summary>
    public static async Task<bool> IsListeningAsync(int port, CancellationToken ct = default)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(400);
                await client.ConnectAsync(address, port, cts.Token);
                return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }
        return false;
    }

    /// <summary>Processes listening on the port, from the TCP tables (IPv4 and IPv6).</summary>
    public static IReadOnlyList<PortOwner> FindOwners(int port)
    {
        var result = new List<PortOwner>();
        try
        {
            foreach (var (pid, ep) in ReadListenTable(AF_INET).Concat(ReadListenTable(AF_INET6)))
            {
                if (ep.Port != port) continue;
                string? name = null;
                try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
                result.Add(new PortOwner(pid, name, ep));
            }
        }
        catch
        {
            // best effort
        }
        return result;
    }

    /// <summary>All TCP ports listened on by any of the given processes.</summary>
    public static IReadOnlyList<int> ListeningPortsOf(IReadOnlyCollection<int> processIds)
    {
        if (processIds.Count == 0) return [];
        var set = processIds as ISet<int> ?? processIds.ToHashSet();
        try
        {
            return ReadListenTable(AF_INET).Concat(ReadListenTable(AF_INET6))
                .Where(x => set.Contains(x.Pid))
                .Select(x => x.Endpoint.Port)
                .Distinct()
                .Order()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    private static IEnumerable<(int Pid, IPEndPoint Endpoint)> ReadListenTable(int family)
    {
        int size = 0;
        GetExtendedTcpTable(0, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size == 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return [];
            int count = Marshal.ReadInt32(buffer);
            var rows = new List<(int, IPEndPoint)>(count);
            if (family == AF_INET)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 DWORDs)
                const int rowSize = 24;
                for (int i = 0; i < count; i++)
                {
                    nint row = buffer + 4 + i * rowSize;
                    uint addr = (uint)Marshal.ReadInt32(row, 4);
                    int port = PortFromNetwork(Marshal.ReadInt32(row, 8));
                    int pid = Marshal.ReadInt32(row, 20);
                    rows.Add((pid, new IPEndPoint(new IPAddress(addr), port)));
                }
            }
            else
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort, remoteAddr[16], remoteScopeId, remotePort, state, pid
                const int rowSize = 56;
                for (int i = 0; i < count; i++)
                {
                    nint row = buffer + 4 + i * rowSize;
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    int port = PortFromNetwork(Marshal.ReadInt32(row, 20));
                    int pid = Marshal.ReadInt32(row, 52);
                    rows.Add((pid, new IPEndPoint(new IPAddress(bytes), port)));
                }
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int PortFromNetwork(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
