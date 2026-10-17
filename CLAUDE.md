# CLAUDE.md

Contexto para quem (pessoa ou agente) continuar este projeto.

- App WPF .NET 8 para Windows; idioma da interface e dos comentários: **português (pt-BR)**.
- Leia `docs/PLANEJAMENTO.md` antes de mudar a arquitetura: lá estão as decisões (ETW para captura,
  Firewall do Windows para bloqueio, identidade de app por caminho/serviço) e o backlog.
- Compilar: `dotnet build -c Release` na raiz. Fora do Windows funciona graças a
  `EnableWindowsTargeting`, mas só roda no Windows e **como Administrador**.
- MVVM sem framework: `ViewModels/ObservableObject.cs` tem `ObservableObject` e `RelayCommand`.
- Regras de firewall criadas pelo app ficam no grupo `NetMonitor`; nunca mexer em regras fora desse grupo.
- Ao concluir itens do backlog, marque-os em `docs/PLANEJAMENTO.md`.
