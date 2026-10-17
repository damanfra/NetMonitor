# NetMonitor

Monitor de tráfego de rede **por aplicativo** para Windows 10/11, com bloqueio de um clique
via Firewall do Windows — uma alternativa simples e gratuita ao GlassWire.

![status](https://img.shields.io/badge/status-v0.1%20(primeira%20versão)-blue)

## O que faz

- Lista os aplicativos que usaram a rede no intervalo escolhido
  (1, 5, 10, 15, 30 min, 1 h, 3 h ou 24 h), com download, upload, total, velocidade atual,
  última atividade e quantidade de hosts remotos.
- Ao selecionar um app, mostra os hosts/IPs com quem ele conversou (com nome DNS quando possível).
- **Bloquear / Desbloquear**: cria (ou remove) regras de entrada e saída no Firewall do Windows
  para o executável. Para serviços dentro do `svchost.exe`, a regra é por serviço.
- **Ocultar**: esconde apps que você já sabe que são seguros; ficam na aba **Ocultos**,
  de onde podem voltar com "Mostrar novamente".
- Filtros: *Ocultar componentes do Windows* e *Ocultar apps da Microsoft*, além de busca por nome/caminho.
- Aba **Bloqueados** lista tudo que foi bloqueado (lido das próprias regras do firewall),
  com "Desbloquear" e "Desbloquear todos".
- Minimiza para a bandeja do sistema sem parar de monitorar.
- **Alertas de consumo**: notificação do Windows quando um app passa de X MB em um período
  (padrão: 100 MB em 5 minutos), no máximo um aviso por app por período.
- Aba **Configurações**: iniciar com o Windows (Sim/Não), alertas, bandeja, filtros.

## Download (sem compilar)

Na página **Releases** do repositório, baixe o `NetMonitor.exe` mais recente. Ele já traz o
.NET 8 embutido — não precisa instalar nada. Cada push na branch `main` gera um novo Release
automaticamente (workflow `.github/workflows/build.yml`).

## Requisitos

- Windows 10 ou 11 (x64)
- Para compilar: .NET 8 SDK
- Executar **como Administrador** (o executável já pede elevação via UAC) — necessário para
  ler os eventos de rede do kernel (ETW) e criar regras de firewall.

## Compilar e executar

```powershell
git clone https://github.com/damanfra/NetMonitor.git
cd NetMonitor
dotnet build -c Release
# executável em: src\NetMonitor\bin\Release\net8.0-windows\win-x64\NetMonitor.exe

# ou gerar o mesmo .exe único (com .NET embutido) que a Action gera:
dotnet publish src/NetMonitor/NetMonitor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Também é possível abrir `NetMonitor.sln` no Visual Studio 2022 (rode o VS como Administrador
para depurar).

O workflow `build` roda em push na `main` (gera Release), em pull requests para a `main`
(gera o .exe como *artifact* para teste) e manualmente pela aba Actions.

## Como funciona (resumo)

| Parte | Tecnologia |
|---|---|
| Captura de tráfego | ETW do kernel (`NetworkTCPIP`) via `Microsoft.Diagnostics.Tracing.TraceEvent` — o mesmo mecanismo do Monitor de Recursos |
| Nome dos hosts | ETW do cliente DNS do Windows (`Microsoft-Windows-DNS-Client`, evento 3008) + DNS reverso |
| Bloqueio | Regras do Windows Defender Firewall (COM `HNetCfg.FwPolicy2`), grupo **NetMonitor** |
| Interface | WPF (.NET 8) |
| Configurações | `%APPDATA%\NetMonitor\settings.json` |
| Iniciar com o Windows | Tarefa agendada "NetMonitor" (ao fazer logon, privilégios mais altos, sem UAC) |
| Alertas | Notificação na bandeja (`NotifyIcon.ShowBalloonTip`) |

Detalhes, decisões e próximos passos em [docs/PLANEJAMENTO.md](docs/PLANEJAMENTO.md).

## Se um bloqueio quebrar alguma coisa

1. Abra o NetMonitor → aba **Bloqueados** → **Desbloquear** (ou **Desbloquear todos**).
2. Sem o NetMonitor: abra `wf.msc` → Regras de Saída/Entrada → filtre pelo grupo **NetMonitor** e exclua.
3. Pela linha de comando (PowerShell como Admin):
   ```powershell
   Remove-NetFirewallRule -Group "NetMonitor"
   ```
