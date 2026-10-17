using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using NetMonitor.Localization;

namespace NetMonitor.Services
{
    /// <summary>
    /// "Iniciar com o Windows" via Agendador de Tarefas.
    ///
    /// Não usamos a chave Run do registro porque o NetMonitor exige Administrador: o Windows
    /// ignora (ou pede UAC a cada logon) programas elevados na chave Run. Uma tarefa agendada com
    /// "Executar com privilégios mais altos" inicia elevada, sem prompt.
    /// A própria tarefa é a fonte da verdade (não guardamos isso em settings.json).
    /// </summary>
    public static class StartupManager
    {
        public const string TaskName = "NetMonitor";
        public const string MinimizedArgument = "--minimized";

        public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

        public static void Enable()
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("Error_UnknownExePath"));
            var user = WindowsIdentity.GetCurrent().Name;
            var xmlPath = Path.Combine(Path.GetTempPath(), "NetMonitor-task.xml");

            // XML em vez de parâmetros do schtasks para desligar os padrões ruins
            // (parar após 3 dias, não iniciar na bateria).
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>{SecurityElement.Escape(Loc.T("Startup_TaskDescription"))}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
      <Arguments>{MinimizedArgument}</Arguments>
    </Exec>
  </Actions>
</Task>";
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            try
            {
                if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out var output) != 0)
                    throw new InvalidOperationException(output.Trim());
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { /* ignorado */ }
            }
        }

        public static void Disable()
        {
            if (IsEnabled() && RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output) != 0)
                throw new InvalidOperationException(output.Trim());
        }

        /// <summary>Se o .exe mudou de lugar, atualiza a tarefa para apontar para o caminho atual.</summary>
        public static void RefreshIfEnabled()
        {
            try
            {
                if (IsEnabled())
                    Enable();
            }
            catch
            {
                // não é crítico
            }
        }

        private static int RunSchtasks(string arguments, out string output)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
    }
}
