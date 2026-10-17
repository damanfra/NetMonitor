using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NetMonitor.Services
{
    /// <summary>
    /// Associa endereços IP a nomes de host. Primeiro usa as respostas DNS observadas via ETW;
    /// se não houver, tenta DNS reverso (em segundo plano, com cache).
    /// </summary>
    public sealed class DnsNameCache
    {
        private readonly ConcurrentDictionary<string, string> _names = new();
        private readonly ConcurrentDictionary<string, byte> _pendingReverse = new();

        public void AddDnsAnswer(string queryName, string results)
        {
            // Formato típico: "type:  5 cdn.exemplo.net;::ffff:1.2.3.4;2606:4700::1;"
            foreach (var raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = raw.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? raw[7..] : raw;
                if (IPAddress.TryParse(token, out var ip))
                    _names[ip.ToString()] = queryName;
            }
        }

        /// <summary>Nome conhecido para o IP, ou null (e dispara DNS reverso para uma próxima vez).</summary>
        public string? TryGetName(IPAddress address)
        {
            var key = address.ToString();
            if (_names.TryGetValue(key, out var name))
                return name.Length == 0 ? null : name;

            if (IsPrivate(address))
                return null;

            if (_pendingReverse.TryAdd(key, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var entry = await Dns.GetHostEntryAsync(address).ConfigureAwait(false);
                        _names.TryAdd(key, entry.HostName ?? "");
                    }
                    catch
                    {
                        _names.TryAdd(key, "");
                    }
                });
            }
            return null;
        }

        public static bool IsPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
                return true;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || b[0] >= 224; // multicast / broadcast
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 (ULA)
        }
    }
}
