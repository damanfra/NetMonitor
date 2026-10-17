using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NetMonitor.Models;

namespace NetMonitor.Services
{
    public sealed record HostSnapshot(IPAddress Address, int LastPort, long Sent, long Received, DateTime LastSeen);

    public sealed record AppSnapshot(
        AppIdentity App,
        long Sent,
        long Received,
        long SendRate,
        long ReceiveRate,
        DateTime LastSeen,
        IReadOnlyList<int> Pids,
        IReadOnlyList<HostSnapshot> Hosts);

    /// <summary>
    /// Guarda o histórico de bytes enviados/recebidos por aplicativo.
    /// A última hora fica em "baldes" de 1 segundo; o restante (até 24h) em baldes de 1 minuto.
    /// Todos os métodos são thread-safe (o ETW grava de uma thread, a UI lê de outra).
    /// </summary>
    public sealed class TrafficStore
    {
        public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
        private static readonly TimeSpan FineRetention = TimeSpan.FromHours(1);

        private readonly object _lock = new();
        private readonly Dictionary<string, AppTraffic> _apps = new();

        public void Record(AppIdentity app, int pid, IPAddress? remote, int remotePort, long sent, long received, DateTime timestampUtc)
        {
            var second = ToUnixSeconds(timestampUtc);
            lock (_lock)
            {
                if (!_apps.TryGetValue(app.Id, out var t))
                    _apps[app.Id] = t = new AppTraffic(app);

                t.App = app;
                t.Add(second, sent, received);
                t.Pids[pid] = timestampUtc;
                if (timestampUtc > t.LastSeen)
                    t.LastSeen = timestampUtc;

                if (remote != null)
                {
                    var key = remote.ToString();
                    if (!t.Hosts.TryGetValue(key, out var h))
                        t.Hosts[key] = h = new HostStat(remote);
                    h.Sent += sent;
                    h.Received += received;
                    h.LastPort = remotePort;
                    if (timestampUtc > h.LastSeen)
                        h.LastSeen = timestampUtc;
                }
            }
        }

        /// <summary>Totais por app dentro da janela [agora - window, agora].</summary>
        public List<AppSnapshot> Query(TimeSpan window, DateTime nowUtc)
        {
            var now = ToUnixSeconds(nowUtc);
            var from = now - (long)window.TotalSeconds;
            var fromUtc = nowUtc - window;
            // "Velocidade atual": média dos últimos 3 segundos completos.
            const int rateSeconds = 3;
            var rateFrom = now - rateSeconds;

            var result = new List<AppSnapshot>();
            lock (_lock)
            {
                foreach (var t in _apps.Values)
                {
                    if (t.LastSeen < fromUtc)
                        continue;

                    long sent = 0, recv = 0, rateSent = 0, rateRecv = 0;
                    foreach (var b in t.Coarse)
                    {
                        if (b.Time >= from) { sent += b.Sent; recv += b.Received; }
                    }
                    foreach (var b in t.Fine)
                    {
                        if (b.Time >= from) { sent += b.Sent; recv += b.Received; }
                        if (b.Time >= rateFrom && b.Time < now) { rateSent += b.Sent; rateRecv += b.Received; }
                    }

                    if (sent == 0 && recv == 0)
                        continue;

                    var pids = t.Pids.Where(p => p.Value >= fromUtc).Select(p => p.Key).OrderBy(p => p).ToList();
                    var hosts = t.Hosts.Values
                        .Where(h => h.LastSeen >= fromUtc)
                        .OrderByDescending(h => h.Sent + h.Received)
                        .Select(h => new HostSnapshot(h.Address, h.LastPort, h.Sent, h.Received, h.LastSeen))
                        .ToList();

                    result.Add(new AppSnapshot(t.App, sent, recv, rateSent / rateSeconds, rateRecv / rateSeconds,
                        t.LastSeen, pids, hosts));
                }
            }
            return result;
        }

        /// <summary>Compacta baldes antigos e descarta o que passou da retenção. Chamar periodicamente.</summary>
        public void Prune(DateTime nowUtc)
        {
            var fineLimit = ToUnixSeconds(nowUtc - FineRetention);
            var coarseLimit = ToUnixSeconds(nowUtc - Retention);
            var retentionUtc = nowUtc - Retention;

            lock (_lock)
            {
                foreach (var key in _apps.Keys.ToList())
                {
                    var t = _apps[key];
                    t.Compact(fineLimit, coarseLimit);

                    foreach (var h in t.Hosts.Where(h => h.Value.LastSeen < retentionUtc).Select(h => h.Key).ToList())
                        t.Hosts.Remove(h);
                    foreach (var p in t.Pids.Where(p => p.Value < retentionUtc).Select(p => p.Key).ToList())
                        t.Pids.Remove(p);

                    if (t.LastSeen < retentionUtc)
                        _apps.Remove(key);
                }
            }
        }

        private static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        private struct Bucket
        {
            public long Time;
            public long Sent;
            public long Received;
        }

        private sealed class HostStat
        {
            public HostStat(IPAddress address) => Address = address;
            public IPAddress Address { get; }
            public int LastPort;
            public long Sent;
            public long Received;
            public DateTime LastSeen;
        }

        private sealed class AppTraffic
        {
            public AppTraffic(AppIdentity app) => App = app;

            public AppIdentity App;
            public DateTime LastSeen;
            public readonly List<Bucket> Fine = new();    // 1 s, ordem crescente de tempo
            public readonly List<Bucket> Coarse = new();  // 60 s, ordem crescente de tempo
            public readonly Dictionary<string, HostStat> Hosts = new();
            public readonly Dictionary<int, DateTime> Pids = new();

            public void Add(long second, long sent, long received)
            {
                // Eventos chegam praticamente em ordem; procura de trás para frente.
                for (var i = Fine.Count - 1; i >= 0 && i >= Fine.Count - 8; i--)
                {
                    if (Fine[i].Time == second)
                    {
                        var b = Fine[i];
                        b.Sent += sent;
                        b.Received += received;
                        Fine[i] = b;
                        return;
                    }
                    if (Fine[i].Time < second)
                    {
                        Fine.Insert(i + 1, new Bucket { Time = second, Sent = sent, Received = received });
                        return;
                    }
                }
                if (Fine.Count == 0 || Fine[^1].Time < second)
                    Fine.Add(new Bucket { Time = second, Sent = sent, Received = received });
                else
                    Fine.Insert(0, new Bucket { Time = second, Sent = sent, Received = received });
            }

            public void Compact(long fineLimit, long coarseLimit)
            {
                var move = 0;
                while (move < Fine.Count && Fine[move].Time < fineLimit)
                {
                    var b = Fine[move];
                    var minute = b.Time - b.Time % 60;
                    if (Coarse.Count > 0 && Coarse[^1].Time == minute)
                    {
                        var c = Coarse[^1];
                        c.Sent += b.Sent;
                        c.Received += b.Received;
                        Coarse[^1] = c;
                    }
                    else
                    {
                        Coarse.Add(new Bucket { Time = minute, Sent = b.Sent, Received = b.Received });
                    }
                    move++;
                }
                if (move > 0)
                    Fine.RemoveRange(0, move);

                var drop = 0;
                while (drop < Coarse.Count && Coarse[drop].Time < coarseLimit)
                    drop++;
                if (drop > 0)
                    Coarse.RemoveRange(0, drop);
            }
        }
    }
}
