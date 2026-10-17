using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NetMonitor.Localization;
using NetMonitor.Models;

namespace NetMonitor.Services
{
    public sealed record BlockedApp(string AppId, string DisplayName, string ExePath, string? Service, DateTime? BlockedAt);

    /// <summary>
    /// Bloqueia/desbloqueia aplicativos criando regras no Firewall do Windows (Windows Defender Firewall).
    ///
    /// Por que firewall e não "quarentena"? Mover/renomear executáveis pode corromper instalações e
    /// atualizações; a regra de firewall só corta a rede do programa, é totalmente reversível e
    /// continua valendo mesmo com o NetMonitor fechado. Todas as regras ficam no grupo "NetMonitor",
    /// visíveis também em wf.msc, para que possam ser removidas manualmente se necessário.
    /// </summary>
    public sealed class FirewallManager
    {
        public const string RuleGroup = "NetMonitor";
        private static string RulePrefix => Loc.T("Rule_Prefix");
        private const string MetaPrefix = "[NetMonitor]";

        private const int NET_FW_RULE_DIR_IN = 1;
        private const int NET_FW_RULE_DIR_OUT = 2;
        private const int NET_FW_ACTION_BLOCK = 0;
        private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;
        private const int NET_FW_IP_PROTOCOL_ANY = 256;

        private static dynamic CreatePolicy() =>
            Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

        /// <summary>Verdadeiro se o firewall estiver ativo em todos os perfis de rede em uso agora.</summary>
        public bool IsFirewallEnabledForCurrentProfiles()
        {
            try
            {
                object policy = CreatePolicy();
                var type = policy.GetType();
                var current = (int)type.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null)!;
                foreach (var profile in new[] { 1, 2, 4 })
                {
                    if ((current & profile) == 0)
                        continue;
                    var enabled = (bool)type.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy,
                        new object[] { profile })!;
                    if (!enabled)
                        return false;
                }
                return true;
            }
            catch
            {
                return true; // não sabemos; não alarmar à toa
            }
        }

        public void Block(AppIdentity app)
        {
            if (app.ExePath == null)
                throw new InvalidOperationException(Loc.T("Error_NoExecutable"));

            Unblock(app.Id); // evita regras duplicadas

            var policy = CreatePolicy();
            var services = app.Services.Count > 0 ? app.Services.Cast<string?>() : new string?[] { null };
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            foreach (var service in services)
            {
                foreach (var direction in new[] { NET_FW_RULE_DIR_OUT, NET_FW_RULE_DIR_IN })
                {
                    dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
                    var label = service == null ? app.DisplayName : $"{app.DisplayName} ({service})";
                    rule.Name = $"{RulePrefix}{label} ({Loc.T(direction == NET_FW_RULE_DIR_OUT ? "Rule_Outbound" : "Rule_Inbound")}) [{ShortHash(app.Id)}]";
                    // Metadados para reconstruir a lista de bloqueados a partir das próprias regras.
                    rule.Description = $"{MetaPrefix}id={Esc(app.Id)}|name={Esc(app.DisplayName)}|at={Esc(stamp)}";
                    rule.ApplicationName = app.ExePath;
                    if (service != null)
                        rule.serviceName = service;
                    rule.Protocol = NET_FW_IP_PROTOCOL_ANY;
                    rule.Direction = direction;
                    rule.Action = NET_FW_ACTION_BLOCK;
                    rule.Profiles = NET_FW_PROFILE2_ALL;
                    rule.Grouping = RuleGroup;
                    rule.Enabled = true;
                    policy.Rules.Add(rule);
                }
            }
        }

        /// <summary>Remove todas as regras do NetMonitor para o app. Retorna quantas regras foram removidas.</summary>
        public int Unblock(string appId)
        {
            object policy = CreatePolicy();
            var names = EnumerateOurRules(policy)
                .Where(r => string.Equals(r.Meta.GetValueOrDefault("id"), appId, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .Distinct()
                .ToList();

            var removed = 0;
            foreach (var name in names)
            {
                // Remove(name) apaga uma regra por vez; repete enquanto existir alguma com esse nome.
                for (var guard = 0; guard < 16 && RuleExists(policy, name); guard++)
                {
                    ((dynamic)policy).Rules.Remove(name);
                    removed++;
                }
            }
            return removed;
        }

        public IReadOnlyList<BlockedApp> GetBlockedApps()
        {
            object policy = CreatePolicy();
            return EnumerateOurRules(policy)
                .Where(r => r.Meta.ContainsKey("id"))
                .GroupBy(r => r.Meta["id"], StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    DateTime? at = DateTime.TryParseExact(first.Meta.GetValueOrDefault("at"), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
                    var services = g.Select(r => r.Service).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                    return new BlockedApp(g.Key, first.Meta.GetValueOrDefault("name") ?? first.Name, first.ExePath,
                        services.Count > 0 ? string.Join(", ", services) : null, at);
                })
                .OrderBy(b => b.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public HashSet<string> GetBlockedIds() =>
            new(GetBlockedApps().Select(b => b.AppId), StringComparer.OrdinalIgnoreCase);

        private static string Esc(string value) => Uri.EscapeDataString(value);

        /// <summary>Sufixo curto e estável para que apps com o mesmo nome não compartilhem regras.</summary>
        private static string ShortHash(string id)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
            return Convert.ToHexString(bytes, 0, 4);
        }

        private static bool RuleExists(object policy, string name)
        {
            try
            {
                var rule = ((dynamic)policy).Rules.Item(name);
                return (object?)rule != null;
            }
            catch
            {
                return false;
            }
        }

        private sealed record RuleInfo(string Name, string ExePath, string? Service, Dictionary<string, string> Meta);

        private static List<RuleInfo> EnumerateOurRules(object fwPolicy)
        {
            dynamic policy = fwPolicy;
            var list = new List<RuleInfo>();
            foreach (dynamic rule in (IEnumerable)policy.Rules)
            {
                string? grouping = rule.Grouping;
                if (!string.Equals(grouping, RuleGroup, StringComparison.Ordinal))
                    continue;

                string? description = rule.Description;
                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (description != null && description.StartsWith(MetaPrefix, StringComparison.Ordinal))
                {
                    foreach (var part in description[MetaPrefix.Length..].Split('|'))
                    {
                        var eq = part.IndexOf('=');
                        if (eq > 0)
                            meta[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
                    }
                }

                list.Add(new RuleInfo((string)rule.Name, (string?)rule.ApplicationName ?? "", (string?)rule.serviceName, meta));
            }
            return list;
        }
    }
}
