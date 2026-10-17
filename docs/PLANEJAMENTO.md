# NetMonitor — Planejamento do projeto

## 1. Objetivo

Ver rapidamente **quais programas estão usando a rede**, quanto, e com quem — e poder
**bloquear** um programa abusivo com um clique, sabendo que dá para **desfazer** com outro clique.
Interface mais simples que o GlassWire, sem limites de versão "Premium".

### Requisitos levantados com o usuário

| # | Requisito | Status v0.1 |
|---|-----------|-------------|
| R1 | Monitorar entradas e saídas de dados por aplicativo | ✅ ETW (TCP/UDP, IPv4/IPv6) |
| R2 | Lista dos apps recentes dentro de um intervalo (combo 5/10/15/30/60 min…) | ✅ 1 min a 24 h |
| R3 | Bloquear um app (quarentena ou firewall — decisão minha) | ✅ Firewall do Windows |
| R4 | Desbloquear facilmente se quebrar algo | ✅ aba Bloqueados + desbloquear na própria linha |
| R5 | Filtro para esconder apps do sistema operacional | ✅ "componentes do Windows" e "apps da Microsoft" |
| R6 | Esconder manualmente apps conhecidos, com aba "Ocultos" | ✅ persistido em settings.json |
| R7 | Interface simples | ✅ uma janela, 4 abas |
| R8 | Aba Configurações com "Iniciar com o Windows" Sim/Não | ✅ v0.2 (tarefa agendada) |
| R9 | Alerta quando um app passar de um certo consumo (sem alerta de "primeira conexão") | ✅ v0.2 |
| R10 | Filtros de Windows/Microsoft ligados por padrão | ✅ v0.2 |
| R11 | Build automático na `main` gerando .exe com .NET 8 embutido | ✅ v0.2 (GitHub Actions + Release) |

## 2. Decisões técnicas

### 2.1 Linguagem / UI: C# + WPF (.NET 8)
- Acesso nativo a ETW, COM do firewall e Win32 sem camadas extras.
- Um único `.exe`, sem servidor web/Electron. Leve.

### 2.2 Captura: ETW do kernel (e não pcap / WinDivert / polling de tabelas)
- `KernelTraceEventParser.Keywords.NetworkTCPIP` entrega **cada envio/recebimento com o PID** e
  o tamanho em bytes. Não precisa instalar driver (Npcap) nem inspecionar pacotes.
- Polling de `GetExtendedTcpTable` só mostra conexões, não bytes; `GetPerTcpConnectionEStats`
  exige habilitar estatísticas por conexão e não cobre UDP.
- Limitação: precisa de Administrador. Sessões nomeadas `NetMonitor-Kernel-Session`
  e `NetMonitor-Dns-Session` (Windows 8+ permite sessões de kernel com nome próprio,
  então não conflita com outras ferramentas que usam "NT Kernel Logger").

### 2.3 Bloqueio: regra de Firewall (e não quarentena)
- **Quarentena** (mover/renomear/negar execução do .exe) pode corromper instalações,
  quebrar atualizadores e é difícil de reverter para serviços do sistema.
- **Regra de firewall** só corta a rede, é instantaneamente reversível, sobrevive a reinícios
  e continua funcionando com o NetMonitor fechado. Regras de bloqueio têm precedência
  sobre regras de permissão no Windows Firewall.
- Criamos 2 regras (entrada + saída) por app; para `svchost.exe` 2 regras **por serviço**
  (`serviceName`), assim bloquear "Windows Update" não derruba o DNS.
- Todas no grupo **NetMonitor**; a descrição guarda metadados
  (`[NetMonitor]id=…|name=…|at=…`, URL-encoded) — a lista de bloqueados é reconstruída a partir das
  próprias regras, sem banco de dados paralelo que possa ficar dessincronizado.
- Regras só valem para conexões **novas**; por isso, ao bloquear, opcionalmente derrubamos as
  conexões TCP IPv4 já estabelecidas do processo (`SetTcpEntry` → `DELETE_TCB`).
- Aviso extra ao bloquear componentes do Windows. Aviso em faixa amarela se o Firewall
  estiver desligado no perfil atual (aí o bloqueio não tem efeito).

### 2.4 Iniciar com o Windows: Agendador de Tarefas
- A chave `Run` do registro não serve: o Windows não inicia silenciosamente programas que exigem
  Administrador. Uma tarefa agendada com *RunLevel=HighestAvailable* e gatilho de logon inicia
  elevada e sem UAC.
- Criada via `schtasks /Create /XML` para desligar os padrões ruins (parar após 3 dias, não
  rodar na bateria). Argumento `--minimized` abre direto na bandeja.
- A tarefa é a fonte da verdade (não fica em settings.json). A cada início, se a tarefa existir,
  é recriada apontando para o caminho atual do .exe.
- **Instância única** (mutex global): duas instâncias derrubariam a sessão ETW uma da outra.
  Abrir de novo só traz a janela existente para frente.

### 2.5 Alertas de consumo
- A cada 5 s verifica o total (↓+↑) de cada app na janela configurada (padrão 100 MB / 5 min).
- No máximo 1 alerta por app por janela; ignora bloqueados e (por padrão) ocultos.
- Exibido como notificação da bandeja; clicar abre o NetMonitor.
- Decisão do usuário: **não** alertar "primeira conexão" de apps novos (incômodo no GlassWire).

### 2.6 Distribuição
- GitHub Actions (`windows-latest`) publica `-r win-x64 --self-contained -p:PublishSingleFile=true
  -p:IncludeAllContentForSelfExtract=true` → um único `NetMonitor.exe` (~70 MB) com o .NET embutido.
- `IncludeAllContentForSelfExtract` porque o TraceEvent carrega DLLs nativas relativas ao próprio
  assembly; extraindo tudo, os caminhos funcionam como numa instalação normal.
- Push na `main` → Release `v1.0.<número da execução>` com o .exe anexado.

### 2.7 Identidade de um "aplicativo"
- Chave = caminho do executável em minúsculas.
- `svchost.exe` = caminho + lista de serviços hospedados naquele PID (via WMI `Win32_Service`).
- PID 0/4 = "Sistema (Kernel do Windows)" — não bloqueável.
- Processo encerrado antes de ser identificado = "Processo encerrado / desconhecido".
- "Componente do Windows" = executável dentro de `%WINDIR%`. "App da Microsoft" = `CompanyName`
  do arquivo contém "Microsoft".

### 2.8 Armazenamento do histórico (em memória)
- Decisão do usuário: o histórico **não** precisa sobreviver a reinícios.
- Última 1 h em baldes de 1 s; de 1 h até 24 h compactado em baldes de 1 min.
- Velocidade "Agora" = média dos últimos 3 s completos.
- Hosts por app: totais acumulados desde que o NetMonitor iniciou, filtrados pela última atividade
  dentro do intervalo (por isso as colunas têm `*`).

## 3. Estrutura do código

```
NetMonitor.sln
src/NetMonitor/
├── App.xaml(.cs)              Composição: cria serviços, inicia ETW, abre a janela
├── MainWindow.xaml(.cs)       Interface (abas Atividade / Bloqueados / Ocultos) + bandeja
├── app.manifest               requireAdministrator, DPI
├── Models/AppIdentity.cs      Quem é o "app" (id, nome, caminho, serviços, empresa)
├── Services/
│   ├── EtwTrafficMonitor.cs   Sessões ETW (kernel TCP/UDP + cliente DNS)
│   ├── ProcessResolver.cs     PID → AppIdentity (cache, svchost→serviços)
│   ├── TrafficStore.cs        Histórico em baldes + consulta por intervalo
│   ├── DnsNameCache.cs        IP → nome (respostas DNS observadas + DNS reverso)
│   ├── FirewallManager.cs     Bloquear / desbloquear / listar via HNetCfg.FwPolicy2
│   ├── ConnectionKiller.cs    Derruba conexões TCP abertas de PIDs bloqueados
│   ├── SettingsStore.cs       %APPDATA%\NetMonitor\settings.json
│   ├── StartupManager.cs      Iniciar com o Windows (tarefa agendada)
│   ├── IconCache.cs           Ícone do .exe
│   └── Format.cs              Formatação de bytes/tempo (pt-BR)
├── ViewModels/                MVVM sem framework (ObservableObject, RelayCommand)
└── Interop/NativeMethods.cs   P/Invoke (kernel32, iphlpapi, user32)
```

Fluxo: `ETW thread → ProcessResolver → TrafficStore.Record()`; a UI, a cada 1 s,
chama `TrafficStore.Query(intervalo)` e sincroniza as linhas (sem recriar, para manter
seleção e ordenação ao vivo).

## 4. Estado atual (v0.2)

- Compila sem erros/avisos (`dotnet build -c Release`). Foi compilado fora do Windows
  (`EnableWindowsTargeting`), **ainda não foi executado em uma máquina Windows real** — o primeiro
  teste local é o próximo passo. Pontos que merecem atenção nesse teste:
  1. Sessão ETW de kernel com nome próprio iniciando sem erro.
  2. Atribuição de `daddr` em eventos *Recv* (esperado: endereço remoto).
  3. Enumeração das regras do firewall via `dynamic`/`IEnumerable` no COM.
  4. Evento DNS 3008 sendo recebido (senão, só DNS reverso).
  5. Desempenho com muito tráfego (downloads grandes geram muitos eventos).

## 5. Próximos passos (backlog)

Prioridade alta
- [ ] Testar em Windows real e corrigir o que aparecer (lista da seção 4).
- [ ] Ícone próprio do app (`.ico`) para janela/bandeja.
- [ ] Gráfico simples de tráfego total no tempo (como a faixa inferior do GlassWire).
- [x] Opção "Iniciar com o Windows" (v0.2).
- [x] Build automático com .exe self-contained (v0.2).

Prioridade média
- [x] Alerta quando um app passa de X MB no intervalo (v0.2).
- [ ] Ação "Bloquear" direto na notificação de alerta (exige toast nativo do Windows, não balão).
- [ ] Bloqueio temporário (ex.: 1 h) com desbloqueio automático.
- [ ] Separar tráfego WAN × LAN por app.
- [ ] País do IP remoto (base GeoIP offline).
- [ ] Exportar CSV.

Prioridade baixa
- [ ] Bloquear apenas um host/IP específico de um app.
- [ ] Tema escuro.
- [ ] Instalador (MSIX ou Inno Setup).
- [ ] Assinatura de código do .exe (evitar aviso do SmartScreen).

Descartado por decisão do usuário: persistir histórico em disco; alerta de "primeira conexão".

## 6. Respostas do usuário

1. Iniciar com o Windows → sim, como opção Sim/Não numa aba Configurações.
2. Histórico sobreviver a reinícios → não.
3. Alertas → só de consumo acima de um limite; nada de "primeira conexão".
4. Filtros Windows/Microsoft → ligados por padrão.

## 7. Perguntas em aberto

1. Prefere a interface em português apenas, ou português/inglês?
2. Valores padrão do alerta (100 MB em 5 min) estão bons?
