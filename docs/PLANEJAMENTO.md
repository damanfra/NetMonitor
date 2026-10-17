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
| R12 | Interface só em pt-BR por enquanto, pronta para inglês no futuro | ✅ v0.3 (textos em `.resx`) |
| R13 | Ícone próprio que represente o objetivo do software | ✅ v0.4 (escudo + setas ↓↑) |
| R14 | Versão na barra de título: "NetMonitor - vX.Y.Z" | ✅ v0.4 |
| R15 | Verificar/instalar atualizações (aba Configurações + checagem ao iniciar, aviso discreto) | ✅ v0.4 (GitHub Releases) |
| R16 | Visual moderno (sem "cara de Windows 95") mantendo a organização das telas | ✅ v0.4 (tema Fluent claro) |
| R17 | Rodapé com crédito discreto à direita: "by Daniel Manfrini - Powered by Claude Code" | ✅ v0.5 |
| R18 | Tamanho padrão de abertura para telas 1366×768 | ✅ v0.5 (1280×720, limitado à área de trabalho) |

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

### 2.6.1 Auto-atualização
- Fonte: `GET https://api.github.com/repos/damanfra/NetMonitor/releases/latest` (repositório público,
  sem token). Compara `tag_name` (`v1.0.N`) com a versão do assembly (definida pela Action).
- Checagem automática 5 s após abrir (opção "Procurar atualizações ao iniciar", ligada por padrão);
  falhas são silenciosas (só aparecem na aba Configurações).
- Aviso **discreto**: pílula "Nova versão vX" no canto do cabeçalho + pontinho azul na aba
  Configurações. Nada de pop-up.
- Instalação: baixa `NetMonitor.exe` para `NetMonitor.new.exe` na mesma pasta, confere tamanho e
  SHA-256 (campo `digest` do asset no GitHub), renomeia o exe em uso para `.old` (Windows permite),
  move o novo para o lugar e o inicia com `--updated`. O novo processo espera o mutex de instância
  única (até 30 s) antes de iniciar o ETW, e apaga o `.old`.
- Bloqueios (regras de firewall) e configurações sobrevivem à atualização; o histórico em memória não.

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

### 2.9 Visual
- Tema "Fluent" claro em `Themes/Theme.xaml` (cores, botões, abas com sublinhado, tabela sem grade,
  ComboBox, interruptores no lugar de caixas de seleção, segmentado Sim/Não, barras de rolagem finas,
  dicas e menus arredondados). A janela só usa estilos/chaves desse dicionário, então um tema escuro
  futuro é outro dicionário com as mesmas chaves.
- Ícones de interface: fonte Segoe Fluent Icons (Win11) com fallback para Segoe MDL2 Assets (Win10).
- Ícone do app: `src/NetMonitor/Assets/NetMonitor.ico`, gerado por `tools/make_icon.py` (Pillow).
- Configurações em cartões com linhas "título + descrição | controle" (como nas Configurações do Windows 11).

### 2.10 Idiomas (i18n)
- **Nenhum texto de interface fica no código.** Todos estão em `Localization/Strings.resx`
  (pt-BR, idioma neutro do assembly — `NeutralLanguage=pt-BR`).
- XAML: `Text="{l:T Chave}"` (markup extension `TExtension`, que gera uma binding para
  `Loc.Instance[Chave]`). C#: `Loc.T("Chave")` ou `Loc.T("Chave", arg0, arg1…)` (usa `string.Format`
  com a cultura atual, então `{0:HH:mm:ss}` etc. funcionam).
- Troca de idioma em tempo de execução: `Loc.Instance.SetLanguage(código)` dispara `Item[]` e
  todas as bindings do XAML se atualizam; textos montados no ViewModel se atualizam no próximo tick (1 s).
- Idioma salvo em `settings.json` (`Language`), combo "Idioma" na aba Configurações
  (hoje só com Português).
- Formatação de números/datas segue `Loc.Instance.Culture`.
- **Como adicionar inglês:**
  1. Copiar `Strings.resx` → `Strings.en.resx` e traduzir os `<value>` (manter `{0}`, `{1}`…).
  2. Descomentar `new LanguageOption("en", "English")` em `Localization/Loc.cs`.
  3. No `.csproj`, `SatelliteResourceLanguages` → `pt-BR;en`.
- Exceção consciente: nomes das regras de firewall usam o idioma ativo no momento do bloqueio;
  o desbloqueio usa os metadados da descrição, então trocar o idioma não quebra nada.

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
│   ├── UpdateService.cs       Verificar/baixar/aplicar atualizações (GitHub Releases)
│   ├── IconCache.cs           Ícone do .exe
│   └── Format.cs              Formatação de bytes/tempo (pt-BR)
├── Assets/NetMonitor.ico      Ícone (gerado por tools/make_icon.py)
├── Themes/Theme.xaml          Cores e estilos de todos os controles
├── Localization/
│   ├── Strings.resx           TODOS os textos da interface (pt-BR)
│   ├── Loc.cs                 Acesso aos textos, idioma atual, lista de idiomas
│   └── TExtension.cs          {l:T Chave} para XAML
├── ViewModels/                MVVM sem framework (ObservableObject, RelayCommand)
└── Interop/NativeMethods.cs   P/Invoke (kernel32, iphlpapi, user32)
```

Fluxo: `ETW thread → ProcessResolver → TrafficStore.Record()`; a UI, a cada 1 s,
chama `TrafficStore.Query(intervalo)` e sincroniza as linhas (sem recriar, para manter
seleção e ordenação ao vivo).

## 4. Estado atual (v0.5)

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
- [x] Ícone próprio do app (v0.4).
- [x] Verificar e instalar atualizações pelo próprio app (v0.4).
- [ ] Testar o fluxo de bloqueio/desbloqueio em uso real (ainda não testado pelo usuário).
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
- [ ] Tradução para inglês (`Strings.en.resx`) — infraestrutura pronta desde a v0.3.
- [ ] Tema escuro (estrutura pronta em `Themes/`).
- [ ] Instalador (MSIX ou Inno Setup).
- [ ] Assinatura de código do .exe (evitar aviso do SmartScreen).

Descartado por decisão do usuário: persistir histórico em disco; alerta de "primeira conexão".

## 6. Respostas do usuário

1. Iniciar com o Windows → sim, como opção Sim/Não numa aba Configurações.
2. Histórico sobreviver a reinícios → não.
3. Alertas → só de consumo acima de um limite; nada de "primeira conexão".
4. Filtros Windows/Microsoft → ligados por padrão.
5. Alerta padrão de 100 MB em 5 min → aprovado para começar.
6. Idioma → só pt-BR por enquanto, mas preparado para inglês no futuro.

## 7. Perguntas em aberto

Nenhuma no momento.
