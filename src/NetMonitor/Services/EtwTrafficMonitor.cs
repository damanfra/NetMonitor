using System;
using System.Net;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetMonitor.Services
{
    /// <summary>
    /// Captura, via ETW (Event Tracing for Windows), cada envio/recebimento TCP e UDP do kernel
    /// com o PID do processo responsável. É o mesmo mecanismo usado pelo Monitor de Recursos do Windows.
    /// Requer execução como administrador.
    /// </summary>
    public sealed class EtwTrafficMonitor : IDisposable
    {
        private const string KernelSessionName = "NetMonitor-Kernel-Session";
        private const string DnsSessionName = "NetMonitor-Dns-Session";
        private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

        private readonly TrafficStore _store;
        private readonly ProcessResolver _resolver;
        private readonly DnsNameCache _dns;
        private TraceEventSession? _kernelSession;
        private TraceEventSession? _dnsSession;
        private Thread? _kernelThread;
        private Thread? _dnsThread;

        public EtwTrafficMonitor(TrafficStore store, ProcessResolver resolver, DnsNameCache dns)
        {
            _store = store;
            _resolver = resolver;
            _dns = dns;
        }

        public event Action<Exception>? Failed;

        public static bool IsElevated => TraceEventSession.IsElevated() == true;

        public void Start()
        {
            _kernelSession = new TraceEventSession(KernelSessionName) { StopOnDispose = true };
            _kernelSession.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);

            var kernel = _kernelSession.Source.Kernel;
            kernel.TcpIpSend += d => OnTraffic(d.ProcessID, d.daddr, d.dport, d.size, 0, d);
            kernel.TcpIpRecv += d => OnTraffic(d.ProcessID, d.daddr, d.dport, 0, d.size, d);
            kernel.TcpIpSendIPV6 += d => OnTraffic(d.ProcessID, d.daddr, d.dport, d.size, 0, d);
            kernel.TcpIpRecvIPV6 += d => OnTraffic(d.ProcessID, d.daddr, d.dport, 0, d.size, d);
            kernel.UdpIpSend += d => OnTraffic(d.ProcessID, d.daddr, d.dport, d.size, 0, d);
            kernel.UdpIpRecv += d => OnTraffic(d.ProcessID, d.daddr, d.dport, 0, d.size, d);
            kernel.UdpIpSendIPV6 += d => OnTraffic(d.ProcessID, d.daddr, d.dport, d.size, 0, d);
            kernel.UdpIpRecvIPV6 += d => OnTraffic(d.ProcessID, d.daddr, d.dport, 0, d.size, d);
            // PIDs são reutilizados pelo Windows: esquece o mapeamento quando o processo muda.
            kernel.ProcessStart += d => _resolver.Invalidate(d.ProcessID);
            kernel.ProcessStop += d => _resolver.Invalidate(d.ProcessID);

            _kernelThread = StartPump(_kernelSession, "ETW Kernel");

            StartDnsSession();
        }

        /// <summary>
        /// Escuta as respostas do cliente DNS do Windows para saber qual nome de host
        /// corresponde a cada IP (melhor que DNS reverso, que quase nunca devolve o nome real).
        /// É opcional: se falhar, o app usa só DNS reverso.
        /// </summary>
        private void StartDnsSession()
        {
            try
            {
                _dnsSession = new TraceEventSession(DnsSessionName) { StopOnDispose = true };
                _dnsSession.EnableProvider(DnsClientProvider, TraceEventLevel.Informational);
                var parser = new RegisteredTraceEventParser(_dnsSession.Source);
                parser.All += OnDnsEvent;
                _dnsThread = StartPump(_dnsSession, "ETW DNS");
            }
            catch
            {
                _dnsSession?.Dispose();
                _dnsSession = null;
            }
        }

        private void OnDnsEvent(TraceEvent e)
        {
            // 3008 = consulta DNS concluída (QueryName / QueryResults)
            if ((int)e.ID != 3008)
                return;
            try
            {
                if (e.PayloadByName("QueryName") is string name && e.PayloadByName("QueryResults") is string results)
                    _dns.AddDnsAnswer(name, results);
            }
            catch
            {
                // evento com formato inesperado
            }
        }

        private Thread StartPump(TraceEventSession session, string name)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex);
                }
            })
            { IsBackground = true, Name = name };
            thread.Start();
            return thread;
        }

        private void OnTraffic(int pid, IPAddress remote, int remotePort, int sent, int received, TraceEvent e)
        {
            try
            {
                var app = _resolver.Resolve(pid);
                _store.Record(app, pid, remote, remotePort, sent, received, e.TimeStamp.ToUniversalTime());
            }
            catch
            {
                // nunca deixar uma exceção derrubar o loop do ETW
            }
        }

        public void Dispose()
        {
            _kernelSession?.Dispose();
            _dnsSession?.Dispose();
            _kernelThread?.Join(2000);
            _dnsThread?.Join(2000);
        }
    }
}
