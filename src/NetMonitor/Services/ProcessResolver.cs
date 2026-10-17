using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using NetMonitor.Interop;
using NetMonitor.Models;

namespace NetMonitor.Services
{
    /// <summary>
    /// Converte PIDs (vindos do ETW) em <see cref="AppIdentity"/>, com cache.
    /// O cache de um PID é invalidado quando o kernel avisa que o processo terminou/iniciou.
    /// </summary>
    public sealed class ProcessResolver
    {
        private static readonly string WindowsDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        private readonly ConcurrentDictionary<int, AppIdentity> _byPid = new();
        private readonly ConcurrentDictionary<string, (string? Description, string? Company)> _fileInfo =
            new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string>? _serviceDisplayNames;

        public AppIdentity Resolve(int pid) => _byPid.GetOrAdd(pid, Build);

        public void Invalidate(int pid) => _byPid.TryRemove(pid, out _);

        /// <summary>PIDs atualmente associados a um app (usado para derrubar conexões ao bloquear).</summary>
        public IReadOnlyList<int> PidsFor(string appId)
        {
            var result = new List<int>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (Resolve(p.Id).Id == appId)
                        result.Add(p.Id);
                }
                catch
                {
                    // processo terminou no meio do caminho
                }
                finally
                {
                    p.Dispose();
                }
            }
            return result;
        }

        public string ServiceDisplayName(string service)
        {
            var map = _serviceDisplayNames ??= LoadServiceDisplayNames();
            return map.TryGetValue(service, out var name) ? name : service;
        }

        private AppIdentity Build(int pid)
        {
            if (pid == 0 || pid == 4)
                return new AppIdentity("system", "Sistema (Kernel do Windows)", null, Array.Empty<string>(),
                    "NT Kernel & System", "Microsoft Corporation", true);

            var path = GetImagePath(pid);
            if (path == null)
            {
                string? name = null;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    name = p.ProcessName;
                }
                catch
                {
                    // processo já encerrou
                }

                return name == null
                    ? new AppIdentity("pid:unknown", "Processo encerrado / desconhecido", null, Array.Empty<string>(),
                        null, null, false)
                    : new AppIdentity("name:" + name.ToLowerInvariant(), name, null, Array.Empty<string>(),
                        null, null, false);
            }

            var (description, company) = _fileInfo.GetOrAdd(path, ReadFileInfo);
            var isWindows = path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(path);

            if (fileName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var services = GetServicesForPid(pid);
                if (services.Count > 0)
                {
                    var id = path.ToLowerInvariant() + "|" + string.Join(",", services).ToLowerInvariant();
                    var display = "Serviço: " + string.Join(", ", services.Select(ServiceDisplayName));
                    return new AppIdentity(id, display, path, services, string.Join(", ", services), company, isWindows);
                }
            }

            var displayName = !string.IsNullOrWhiteSpace(description) ? description! : Path.GetFileNameWithoutExtension(path);
            return new AppIdentity(path.ToLowerInvariant(), displayName, path, Array.Empty<string>(), fileName,
                company, isWindows);
        }

        private static string? GetImagePath(int pid)
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        private static (string?, string?) ReadFileInfo(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return (Clean(info.FileDescription), Clean(info.CompanyName));
            }
            catch
            {
                return (null, null);
            }

            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static List<string> GetServicesForPid(int pid)
        {
            var list = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_Service WHERE ProcessId = {pid}");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["Name"] is string name)
                            list.Add(name);
                    }
                }
            }
            catch
            {
                // WMI indisponível: o svchost aparece agrupado
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static Dictionary<string, string> LoadServiceDisplayNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var sc in System.ServiceProcess.ServiceController.GetServices())
                {
                    using (sc)
                        map[sc.ServiceName] = sc.DisplayName;
                }
            }
            catch
            {
                // sem nomes amigáveis
            }
            return map;
        }
    }
}
