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
| R7 | Interface simples | ✅ uma janela, 3 abas |

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

### 2.4 Identidade de um "aplicativo"
- Chave = caminho do executável em minúsculas.
- `svchost.exe` = caminho + lista de serviços hospedados naquele PID (via WMI `Win32_Service`).
- PID 0/4 = "Sistema (Kernel do Windows)" — não bloqueável.
- Processo encerrado antes de ser identificado = "Processo encerrado / desconhecido".
- "Componente do Windows" = executável dentro de `%WINDIR%`. "App da Microsoft" = `CompanyName`
  do arquivo contém "Microsoft".

### 2.5 Armazenamento do histórico (em memória)
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
│   ├── IconCache.cs           Ícone do .exe
│   └── Format.cs              Formatação de bytes/tempo (pt-BR)
├── ViewModels/                MVVM sem framework (ObservableObject, RelayCommand)
└── Interop/NativeMethods.cs   P/Invoke (kernel32, iphlpapi, user32)
```

Fluxo: `ETW thread → ProcessResolver → TrafficStore.Record()`; a UI, a cada 1 s,
chama `TrafficStore.Query(intervalo)` e sincroniza as linhas (sem recriar, para manter
seleção e ordenação ao vivo).

## 4. Estado atual (v0.1)

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
- [ ] Persistir o histórico em disco (SQLite) para sobreviver a reinícios.
- [ ] Opção "Iniciar com o Windows" (Agendador de Tarefas com privilégio elevado, sem prompt UAC).

Prioridade média
- [ ] Alerta de "primeira conexão" de um app novo (como o Log Analysis do GlassWire).
- [ ] Alerta quando um app passa de X MB no intervalo.
- [ ] Bloqueio temporário (ex.: 1 h) com desbloqueio automático.
- [ ] Separar tráfego WAN × LAN por app.
- [ ] País do IP remoto (base GeoIP offline).
- [ ] Exportar CSV.

Prioridade baixa
- [ ] Bloquear apenas um host/IP específico de um app.
- [ ] Tema escuro.
- [ ] Instalador (MSIX ou Inno Setup).

## 6. Perguntas em aberto para o usuário

1. Quer que o NetMonitor **inicie com o Windows** e fique na bandeja por padrão?
2. O histórico precisa **sobreviver a reinícios** do PC/app, ou só a sessão atual basta?
3. Quer **alertas** (notificação do Windows) para "app novo usando a rede" ou "consumo acima de X"?
4. Os filtros "Ocultar componentes do Windows / Microsoft" devem vir **ligados por padrão**?
5. Prefere a interface em português apenas, ou português/inglês?
