using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NetMonitor.Interop;

namespace NetMonitor.Services
{
    /// <summary>
    /// Regras de firewall valem para conexões novas; conexões TCP já abertas podem continuar vivas.
    /// Esta classe encerra as conexões TCP IPv4 estabelecidas de um conjunto de PIDs, para que o
    /// bloqueio tenha efeito imediato. (O Windows não oferece API equivalente para IPv6.)
    /// </summary>
    public static class ConnectionKiller
    {
        public static int CloseTcpConnections(IReadOnlyCollection<int> pids)
        {
            if (pids.Count == 0)
                return 0;

            var set = new HashSet<int>(pids);
            int size = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeMethods.AF_INET,
                NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AF_INET,
                        NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    return 0;

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                var closed = 0;
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(buffer + 4 + i * rowSize);
                    if (row.state != NativeMethods.MIB_TCP_STATE_ESTAB || !set.Contains((int)row.owningPid))
                        continue;

                    var entry = new NativeMethods.MIB_TCPROW
                    {
                        state = NativeMethods.MIB_TCP_STATE_DELETE_TCB,
                        localAddr = row.localAddr,
                        localPort = row.localPort,
                        remoteAddr = row.remoteAddr,
                        remotePort = row.remotePort,
                    };
                    if (NativeMethods.SetTcpEntry(ref entry) == 0)
                        closed++;
                }
                return closed;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
