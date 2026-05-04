# FieldCure AssistStudio Runner

Windows-only headless LLM task automation engine — define natural language
tasks, schedule them via Windows Task Scheduler, and dispatch results
through the AssistStudio Outbox channels (Slack, Telegram, Email,
KakaoTalk, Discord). Runs as an MCP server in `serve` mode for task CRUD
and as a one-shot CLI in `exec` mode for headless execution.

Distributed as a single dotnet tool NuGet package
(`FieldCure.AssistStudio.Runner`).

## Solution structure

```
FieldCure.AssistStudio.Runner.slnx       ← .slnx format (requires VS 17.12+)
src/
└── FieldCure.AssistStudio.Runner/       → NuGet: FieldCure.AssistStudio.Runner
    │                                      (net8.0, [SupportedOSPlatform("windows")])
    │                                      PackAsTool=true, ToolCommandName=assiststudio-runner
    │                                      PackageType=McpServer (.mcp/server.json)
    │
    ├── Program.cs                       Entry point — dispatches `serve` / `exec` /
    │                                    `config` subcommands.
    ├── Configuration/ConfigRunner.cs    `config init / set-credential / get-credential` CLI.
    ├── Credentials/                     PasswordVault adapter (Windows Credential Manager).
    │   ├── ICredentialService           GetApiKey / SetApiKey + GetMcpEnvVar / SetMcpEnvVar.
    │   └── CredentialService            Single-user CredWrite/CredRead implementation.
    ├── Execution/
    │   ├── TaskExecutor                 Task → AgentLoop run, streams ExecutionLog rounds.
    │   └── McpServerPool                Bootstraps task-scoped MCP servers + tool allowlist.
    ├── Models/
    │   ├── RunnerConfig                 Global runner.json shape: DefaultModelName, Models
    │   │                                (Dictionary<string, ModelConfig>), DefaultMcpServers,
    │   │                                Retry, FallbackChannel, …
    │   ├── RunnerTask                   Task DB row: prompt, schedule, ModelName, Guardrails,
    │   │                                McpServers, OutputChannel.
    │   ├── TaskExecution                Per-run execution record with status + summary.
    │   ├── ExecutionLog                 Round-by-round trace for audit (Messages array).
    │   ├── ExecutionStatus              Pending / Running / Succeeded / Failed / Cancelled / TimedOut.
    │   └── McpServerConfig              Task-level MCP server entry (id/name/command/args/env-keys).
    ├── Scheduling/
    │   ├── IJobScheduler                Abstraction so future non-Windows backends can plug in.
    │   ├── WindowsTaskScheduler         Default implementation — schtasks shell-out.
    │   ├── SchtasksTrigger              Encodes a schedule as schtasks command-line arguments.
    │   └── CronToSchtasks               Cron expression → schtasks subset (5-field standard cron).
    ├── Storage/TaskStore.cs             SQLite (`runner.db`) — Tasks + TaskExecutions tables,
    │                                    inline ALTER TABLE migrations on Initialize().
    ├── Tools/                           MCP tool implementations (one per file).
    │   ├── CreateTaskTool / UpdateTaskTool / DeleteTaskTool
    │   ├── ListTasksTool / GetTaskHistoryTool / GetExecutionStatusTool
    │   └── RunTaskTool                  Triggers a task and optionally waits up to 60s.
    ├── .mcp/server.json                 MCP registry metadata (mirrors csproj <Version>).
    ├── README.md                        NuGet-shipped package README.
    └── Logo.png                         Package icon.

tests/
└── FieldCure.AssistStudio.Runner.Tests/  RunnerConfigTests + others (MSTest).

scripts/publish-nuget.ps1                Pack → sign (GlobalSign EV USB dongle) → push.
```

## Architecture

- **dotnet tool packaging** — `PackAsTool=true` produces a single
  cross-distribution executable. The package is declared Windows-only via
  assembly-level `[SupportedOSPlatform("windows")]`; non-Windows
  consumers see CA1416 analyzer warnings rather than runtime surprises.
  PackAsTool does not support Windows-specific TFMs, so the project stays
  on `net8.0` and the OS gate happens through the attribute.
- **MCP server packaging** — `PackageType=McpServer` plus
  `.mcp/server.json` (latest 2025-12 schema) make the package discoverable
  through MCP-aware NuGet clients and VS Code's MCP integration. `dnx`
  fallback in saved schtasks entries lets AssistStudio hosts schedule
  tasks before any global tool install.
- **Two execution modes**:
  - **`serve`** — spins up the MCP transport (stdio) and exposes the
    seven tools in `Tools/`. Used by AssistStudio and Claude Desktop /
    VS Code as a long-running process.
  - **`exec <task-id>`** — one-shot headless execution invoked by
    Windows Task Scheduler at trigger time.
- **Provider resolution** — `RunnerConfig.ResolveModel(name)` looks up an
  entry in `Models`, falling back to provider-type match. Returns a
  `ProviderModel` consumed by `ProviderFactory.Create` from
  `FieldCure.Ai.Providers`. API keys come from PasswordVault keyed by the
  same name (host and Runner share the credential slot).
- **Tool allowlist** — `RunnerTask.Guardrails.AllowedTools` is the
  pre-approval surface in headless mode. `null` means all tools
  discovered through MCP bootstrap; an empty list means none. There is
  no interactive approval — the schedule trigger has no human in the
  loop.
- **Scheduler abstraction** — `IJobScheduler` isolates Windows Task
  Scheduler so the schedule code path is testable and swappable. The
  Windows implementation shells out to `schtasks.exe`; cron expressions
  are translated to its argument grammar by `CronToSchtasks`.

## Storage layout

- **`%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/runner.json`** — global
  config. Hand-edited or generated by `config init` /
  `BuildFromVault()`. Pre-2.0 `defaultPresetName` / `presets` keys are
  silently ignored (not migrated); set the new `defaultModelName` /
  `models` keys instead.
- **`%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/runner.db`** — SQLite
  Tasks + TaskExecutions store. `Initialize()` runs idempotent
  ALTER TABLE migrations for each schema version, so v1.x databases
  auto-upgrade on first v2.0 start (PresetName → ModelName, requires
  SQLite 3.25+ / Microsoft.Data.Sqlite 8.x).
- **`%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/logs/`** — per-execution
  log files retained per `RunnerConfig.LogRetentionDays` (default 30).

## Build & test

```bash
dotnet build                    # Build src + tests
dotnet test                     # Run RunnerConfigTests + ScheduleTests + …
```

## NuGet publishing

```powershell
# pack → sign (GlobalSign EV USB dongle required) → push
.\scripts\publish-nuget.ps1                   # full publish
.\scripts\publish-nuget.ps1 -SkipPush         # pack + sign only
.\scripts\publish-nuget.ps1 -SkipSign -SkipPush  # local pack-only test
```

When bumping `<Version>` in the csproj, also update `version` in
`.mcp/server.json` (both top-level and `packages[0].version`) so the MCP
registry metadata stays in sync.

## Coding conventions

- C# 12, nullable enable, implicit usings.
- `GenerateDocumentationFile` enabled — every public surface and
  internal helper method carries a `/// <summary>` comment, including
  event handlers and private constructors.
- MCP tool implementations live one-per-file in `Tools/`; each is a
  discoverable type with `[McpServerTool]` attributes from the
  `ModelContextProtocol` SDK.
- Migrations in `TaskStore.Initialize` are wrapped in
  `try { ... ALTER TABLE ... } catch (SqliteException) { /* idempotent */ }`
  so reruns and fresh databases share the same code path.
- Error messages from MCP tools serialize a JSON envelope
  `{ success: false, error: "..." }` so the LLM-side caller can route on
  the `success` flag without parsing prose.

## Cross-repo links

- **Upstream NuGet deps**: `FieldCure.Ai.Providers` (LLM clients) and
  `FieldCure.Ai.Execution` (`AgentLoop`). Floating refs (`0.*`) so the
  package picks up patches without a Runner publish — but a major bump
  on either upstream package needs a Runner build verification, since
  the renames there cascade into Runner code (e.g., the v0.7.0
  `ProviderPreset → ProviderModel` rename triggered Runner v2.0).
- **Host integration**: AssistStudio's `BuiltInServerHelper` registers
  the Runner as a built-in MCP server, sharing the
  `McpEnv_builtin_{Name}_{key}` credential slot with auto-detected
  Essentials / Outbox stateless servers.
- **Sibling MCP servers**: Runner does not depend on any of the
  `fieldcure-mcp-*` repos directly. Tasks discover them at runtime
  through `RunnerConfig.DefaultMcpServers` plus task-specific
  `McpServers` entries.

## Notes

- The package is intentionally large (~110 MB nupkg) because it ships
  the full `dnx` runtime plus transitive native dependencies (SkiaSharp,
  DocumentFormat.OpenXml, …). This is the cost of the dotnet-tool
  shipping model with a self-contained Windows runtime — do not try to
  trim it without a deliberate redesign.
- `runner.db` lives outside the package install dir so a `dotnet tool
  uninstall` does not destroy task history. If a clean reset is needed,
  delete the data directory manually.
- API keys are stored in PasswordVault keyed by **model name**
  (e.g., `Claude`, `OpenAI`); the same key is shared with the
  AssistStudio host. There is no per-Runner credential silo.
