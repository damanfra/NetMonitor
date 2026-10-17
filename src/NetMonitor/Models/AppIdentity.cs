using System;
using System.Collections.Generic;

namespace NetMonitor.Models
{
    /// <summary>
    /// Identifica um "aplicativo" para fins de contabilização e bloqueio.
    /// Normalmente é o caminho do executável; para o svchost.exe inclui também
    /// os serviços hospedados, para que cada serviço apareça separado.
    /// </summary>
    public sealed class AppIdentity
    {
        public AppIdentity(string id, string displayName, string? exePath, IReadOnlyList<string> services,
            string? description, string? company, bool isWindowsComponent)
        {
            Id = id;
            DisplayName = displayName;
            ExePath = exePath;
            Services = services;
            Description = description;
            Company = company;
            IsWindowsComponent = isWindowsComponent;
        }

        /// <summary>Chave estável (minúscula) usada no armazenamento e nas listas de ocultos.</summary>
        public string Id { get; }

        public string DisplayName { get; }

        /// <summary>Caminho completo do executável, ou null para pseudo-processos (System, PID desconhecido).</summary>
        public string? ExePath { get; }

        /// <summary>Nomes curtos dos serviços Windows hospedados (somente para svchost.exe).</summary>
        public IReadOnlyList<string> Services { get; }

        public string? Description { get; }

        public string? Company { get; }

        /// <summary>Executável dentro da pasta do Windows ou o próprio kernel.</summary>
        public bool IsWindowsComponent { get; }

        public bool IsMicrosoft => Company != null && Company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Só é possível criar regra de firewall para algo que tenha um executável.</summary>
        public bool CanBlock => !string.IsNullOrEmpty(ExePath);
    }
}
