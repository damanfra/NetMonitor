using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetMonitor.Localization;

namespace NetMonitor.Services
{
    public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string DownloadUrl, long Size, string? Sha256);

    /// <summary>
    /// Verifica e aplica atualizações a partir dos Releases públicos do GitHub
    /// (gerados pelo workflow .github/workflows/build.yml a cada push na main).
    ///
    /// Aplicação: baixa o novo NetMonitor.exe ao lado do atual, confere tamanho/SHA-256,
    /// renomeia o exe em execução para .old (o Windows permite renomear um exe em uso),
    /// coloca o novo no lugar e reinicia. O .old é apagado na próxima inicialização.
    /// </summary>
    public static class UpdateService
    {
        public const string Repository = "damanfra/NetMonitor";
        public const string AssetName = "NetMonitor.exe";
        public const string UpdatedArgument = "--updated";

        private static readonly HttpClient Http = CreateClient();

        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Último release publicado, ou null se a versão atual já for a mais nova.</summary>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return null;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            if (version <= CurrentVersion)
                return null;

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object)
                return null;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..];

            return new UpdateInfo(version, tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha);
        }

        /// <summary>Baixa, valida e troca o executável; depois reinicia o app. Não retorna em caso de sucesso.</summary>
        public static async Task ApplyAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
        {
            var current = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("Error_UnknownExePath"));
            var dir = Path.GetDirectoryName(current)!;
            var newPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(current) + ".new.exe");
            var oldPath = current + ".old";

            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? update.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(newPath);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0)
                        progress?.Report((double)read / total);
                }
            }

            var info = new FileInfo(newPath);
            if (info.Length != update.Size)
            {
                File.Delete(newPath);
                throw new InvalidDataException(Loc.T("Update_ErrorSize", info.Length, update.Size));
            }
            if (update.Sha256 != null)
            {
                await using var fs = File.OpenRead(newPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fs.Close();
                    File.Delete(newPath);
                    throw new InvalidDataException(Loc.T("Update_ErrorHash"));
                }
            }

            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(current, oldPath);
            try
            {
                File.Move(newPath, current);
            }
            catch
            {
                File.Move(oldPath, current); // desfaz
                throw;
            }

            // Já estamos elevados: o processo filho herda a elevação sem novo prompt do UAC.
            Process.Start(new ProcessStartInfo(current, UpdatedArgument) { UseShellExecute = false });
        }

        /// <summary>Remove o executável antigo deixado por uma atualização anterior.</summary>
        public static void CleanupOldVersion()
        {
            var current = Environment.ProcessPath;
            if (current == null)
                return;
            var oldPath = current + ".old";
            for (var i = 0; i < 10 && File.Exists(oldPath); i++)
            {
                try { File.Delete(oldPath); }
                catch { Thread.Sleep(500); } // o processo antigo pode estar terminando
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            // A API do GitHub exige User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetMonitor", CurrentVersion.ToString()));
            return client;
        }
    }
}
