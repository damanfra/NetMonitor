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

## Requisitos

- Windows 10 ou 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) para rodar,
  ou o .NET 8 SDK para compilar
- Executar **como Administrador** (o executável já pede elevação via UAC) — necessário para
  ler os eventos de rede do kernel (ETW) e criar regras de firewall.

## Compilar e executar

```powershell
git clone https://github.com/damanfra/NetMonitor.git
cd NetMonitor
dotnet build -c Release
# executável em: src\NetMonitor\bin\Release\net8.0-windows\win-x64\NetMonitor.exe

# ou gerar uma pasta pronta para copiar:
dotnet publish src/NetMonitor/NetMonitor.csproj -c Release -o publish
```

Também é possível abrir `NetMonitor.sln` no Visual Studio 2022 (rode o VS como Administrador
para depurar).

Cada push no GitHub dispara o workflow `build`, que publica o executável como *artifact* da execução.

## Como funciona (resumo)

| Parte | Tecnologia |
|---|---|
| Captura de tráfego | ETW do kernel (`NetworkTCPIP`) via `Microsoft.Diagnostics.Tracing.TraceEvent` — o mesmo mecanismo do Monitor de Recursos |
| Nome dos hosts | ETW do cliente DNS do Windows (`Microsoft-Windows-DNS-Client`, evento 3008) + DNS reverso |
| Bloqueio | Regras do Windows Defender Firewall (COM `HNetCfg.FwPolicy2`), grupo **NetMonitor** |
| Interface | WPF (.NET 8) |
| Configurações | `%APPDATA%\NetMonitor\settings.json` |

Detalhes, decisões e próximos passos em [docs/PLANEJAMENTO.md](docs/PLANEJAMENTO.md).

## Se um bloqueio quebrar alguma coisa

1. Abra o NetMonitor → aba **Bloqueados** → **Desbloquear** (ou **Desbloquear todos**).
2. Sem o NetMonitor: abra `wf.msc` → Regras de Saída/Entrada → filtre pelo grupo **NetMonitor** e exclua.
3. Pela linha de comando (PowerShell como Admin):
   ```powershell
   Remove-NetFirewallRule -Group "NetMonitor"
   ```
