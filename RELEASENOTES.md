# Release Notes

## v2.0.3 (2026-05-04)

### Fix — Stop duplicate output-channel deliveries

End-to-end testing of v2.0.2 surfaced a redundancy: when `output_channel`
is configured, the framework's `TryNotifyAsync` auto-sends the final
summary at the end of the loop AND the LLM also calls `send_message`
mid-loop with the same channel. Users on KakaoTalk/Slack/Email saw two
messages — the structured payload from the model, then a redundant
"task completed" summary from the framework.

The fix is two layers — prompt guidance for normal behaviour and
detection-based suppression for defence in depth:

- **System prompt declares the configured channel by name.**
  `BuildSystemPrompt` now branches on `task.OutputChannel`. When set,
  the prompt names it explicitly: *"Output channel 'kakaotalk_1' is
  configured. The framework will automatically send your final response
  to that channel after this loop ends. Do NOT call send_message (or
  any equivalent output tool) for that channel — your final summary
  will be delivered automatically. Just produce the summary and end the
  loop."* When no channel is configured the prompt instead instructs
  the model to call the appropriate output tool explicitly.

- **Failure handling is now explicit.** Models occasionally chewed
  through the round budget retrying a transient tool failure. New rules:
  *"If a tool call fails, do NOT retry more than once. On unrecoverable
  failure, finalize immediately with the error reason in your final
  response. Do not exhaust your round budget retrying."*

- **`TryNotifyAsync` inspects the loop's tool-call history.** A new
  helper `LlmAlreadySentToChannel` scans `loopResult.Messages` for any
  `send_message` invocation whose arguments JSON contains the configured
  channel name; when found, the framework auto-send is skipped and
  `NotificationStatus` records `"skipped (llm sent)"`. Catches legacy
  tasks and stubborn models that ignore the prompt guidance, so the
  duplicate-message failure mode cannot survive a single side.

### Notes

- The `EXIT CONDITIONS` block from v2.0.2 is preserved; this release
  reframes "call the output tool" to a generic "finalize your response"
  so it composes cleanly with the new `DELIVERY` section.
- Substring match is sufficient for channel detection — false positives
  only fire when the channel name happens to appear in the message body,
  which is rare and only causes a *missed* notification (the model
  already sent it). Skipping a duplicate is the safer failure mode.

## v2.0.2 (2026-05-04)

### Fix — Stop scheduled tasks from over-iterating

End-to-end testing of v2.0.1 surfaced a behaviour bug independent of the
dnx migration: a "search LG stock and send to KakaoTalk in 2 minutes"
task burned all 10 rounds re-verifying data the LLM already had and
never reached the `send_message` call. The Outbox fallback notification
fired ("Task failed: Maximum rounds (10) reached"), so users saw a
delivered message that announced the task's failure to deliver — the
worst possible UX.

The system prompt and round budget are tuned for the
search → summarize → send shape:

- **`TaskExecutor.BuildSystemPrompt` adds explicit exit conditions:**
  - Cap information gathering at 2–3 searches; do NOT re-verify data
    already collected.
  - Once data is sufficient, call the output tool (e.g. `send_message`)
    immediately without further searches.
  - When two data points conflict, use the first reliable one and note
    the uncertainty — do not loop trying to resolve it.
  - "Prefer action over perfection. A good answer sent is better than a
    perfect answer never sent."

- **Default `MaxRounds` raised from 10 to 20.** A realistic
  search + summarize + send workflow is 7–8 rounds (search × 2-3 +
  fetch × 2-3 + JS calc × 1 + send × 1); 10 left no headroom for
  retries or error recovery. Updates both
  `RunnerTask.Guardrails.MaxRounds` (in-memory default) and
  `CreateTaskTool` (`max_rounds ?? 20`). Existing rows in `runner.db`
  keep their stored value — the change applies to new tasks only.

- **`CreateTaskTool.max_rounds` description now guides callers**:
  *"Maximum agent loop iterations (default: 20). Use 10 for simple
  single-tool tasks, 20 for search+summarize+send workflows, 30+ for
  complex multi-step research."* Tools-aware models pick a sensible
  budget without being asked.

- **`prompt` description rules out scheduling-time pre-fetch.** Models
  occasionally embed data they gathered while *creating* the task into
  the prompt, then have the worker "send" stale data at trigger time.
  The new wording for both `create_task` and `update_task` is explicit:
  *"Describe what the worker should do AT execution time. All data
  gathering (search, fetch, calculate) must happen at execution time —
  never pre-fetch at scheduling time."*

### Fix — Sync `.mcp/server.json` version with csproj `<Version>`

v2.0.1 shipped with `.mcp/server.json` still pinned at `2.0.0`. v2.0.2
brings both the top-level and `packages[0].version` fields back in
lockstep with the package version, and CLAUDE.md already calls this out
as a release-time checklist item.

### Notes

- v2.0.0 / v2.0.1 are still on NuGet — older `runner.json` and `runner.db`
  rows remain compatible. The behaviour fix is forward-looking only.
- Round budget awareness (dynamic `[Round 8/20, remaining: 12]` injection
  into the system prompt) is queued for v2.1+ — the static guidance and
  raised default in v2.0.2 are sufficient for current workloads.

## v2.0.1 (2026-05-04)

### Fix — Retire the `%LOCALAPPDATA%\FieldCure\AssistStudio\tools\` install scheme

Completes the dnx migration on the Runner side. AssistStudio's MCP
runtime moved to `dnx` in mid-April; Runner kept preferring tool-path
binaries under the legacy `tools/` folder, but nothing populated that
folder anymore. Stale binaries left there shadowed the dnx-cached
current versions and caused silent version-skew failures at trigger
time (e.g. a v1.x worker hitting the v2.x DB schema — exit 1, the cmd
window flashed and disappeared).

- **`WindowsTaskScheduler.BuildRunnerCommandLine`** drops the tool-path
  preference. Schtasks entries now spawn `dnx FieldCure.AssistStudio.Runner@<major>.* --yes exec <id>`
  by default; an explicit `RunnerConfig.ToolPath` override still wins
  when pinning a specific build is required.

- **`RunnerConfig.DetectInstalledServers`** replaces the dual global /
  local tool-path scan with dnx-based discovery. Each stateless server
  (essentials, outbox) is emitted as a dnx command pinned at its current
  major range (`@2.*`); bumping a major now requires an intentional
  Runner release so breaking changes never sneak in mid-cycle.

- **`RunnerConfig.Load`** auto-migrates pre-2.0.1 `runner.json` files —
  any `defaultMcpServers[].command` pointing into the retired `tools/`
  folder is rewritten to the equivalent dnx command and saved back
  durably. Unknown entries are left untouched.

- **New `DnxResolver` helper** centralizes PATH-based resolution of the
  `dnx` shim (Windows `.cmd` / `.exe` / `.bat` / `.ps1`, plain `dnx` on
  Linux). Used by both the schtasks command-line builder and stateless
  MCP server discovery.

`runner.json` migration is automatic on first start; no user action
required.

## v2.0.0 (2026-05-04)

### Breaking — Preset → Model rename across the public surface

Aligns Runner terminology with the upstream rename in
**FieldCure.Ai.Providers 0.7.0** (`ProviderPreset` → `ProviderModel`) and
**FieldCure.AssistStudio.Core 0.19.0** (`Profile.PreferredModelName`).

| Surface | Before | After |
|---|---|---|
| `runner.json` | `defaultPresetName` | `defaultModelName` |
| `runner.json` | `presets: { ... }` | `models: { ... }` |
| `runner.json` | `PresetConfig` | `ModelConfig` |
| `runner.db` Tasks column | `PresetName` | `ModelName` |
| MCP `create_task` / `update_task` | `preset_name` argument | `model_name` argument |
| CLI `config init` | `--preset <name>` | `--model-name <name>` |
| `RunnerConfig` API | `DefaultPresetName`, `Presets`, `ResolvePreset` | `DefaultModelName`, `Models`, `ResolveModel` |
| `RunnerTask` API | `PresetName` | `ModelName` |
| `ICredentialService` | parameter `presetName` | parameter `modelName` (signature unchanged behaviourally) |

### Migration

- **`runner.db` is auto-migrated on first start.** `TaskStore.Initialize`
  runs `ALTER TABLE Tasks RENAME COLUMN PresetName TO ModelName`
  inside the existing migration block (idempotent — fresh databases
  swallow the resulting "no such column" error). Existing task rows
  survive intact. Requires SQLite 3.25+, which is the floor for the
  bundled `Microsoft.Data.Sqlite` 8.x runtime.
- **`runner.json` is silently regenerated.** Pre-2.0 files load with
  empty `Models`, fall through to `BuildFromVault()`, and are
  overwritten in the new shape. Custom edits in the old file
  (`baseUrl`, `temperature`, `maxTokens` overrides, custom model ids)
  are lost — copy them out before upgrading if you depend on them.
- **MCP `preset_name` callers receive a clear error.** No alias is
  wired; the tool router rejects unknown arguments. Update LLM
  prompts and any wrapper scripts that hard-code `preset_name`.

### Rebuilt against

- `FieldCure.Ai.Providers` 0.7.1 (`ProviderModel`,
  `ChatMessage.IsHidden` / `IsContinuation` / `IsTruncated`,
  `ProviderModelBroadcast`, Gemini inline image output, audio
  attachment scaffold)
- `FieldCure.Ai.Execution` 0.4.1 (`SubAgentRequest.ModelName`,
  `SubAgentResult.UsedModel`)

### Internal

- `docs/AssistStudio_Runner_v2.0_Spec.md` — the gitignored 2026-03-30
  design draft was deleted from disk; the rename rationale lives in
  this RELEASENOTES entry and the rename commit message.

---

## v1.4.0 (2026-04-22)

### Changed

- **Auto-detected built-in server ids use `builtin_` prefix** — `DetectInstalledServers` now emits entries whose `McpServerConfig.Id` is `builtin_{Name}` instead of `default_{Name}`, matching the prefix the AssistStudio host uses for the same servers. Both sides share the `McpEnv_{serverId}_{key}` credential slot, so keys entered in the host are available to Runner-spawned servers without any additional mirror step.
- **`McpServerEntry` carries environment variable keys** — a new `EnvironmentVariableKeys` list on `McpServerEntry` is forwarded through `ToMcpServerConfig`, so `McpServerPool` resolves each key through `CredentialService.GetMcpEnvVar(serverId, key)` at bootstrap. The auto-detected Essentials entry declares `SERPER_API_KEY`, `TAVILY_API_KEY`, `SERPAPI_API_KEY`, and `WOLFRAM_APPID`.
- **`McpServerEntry.IsBuiltIn`** — a `[JsonIgnore]` flag set by `DetectInstalledServers` to distinguish auto-detected entries from user-configured ones. User entries keep the bare `Name` as their id.

### Breaking

- The id for auto-detected Essentials and Outbox changed from `default_essentials` / `default_outbox` to `builtin_essentials` / `builtin_outbox`. Any persisted `runner.json` or saved task row that references the old ids will no longer match the auto-detected entry. No external consumers are expected to be affected at this release stage.

---

## v1.3.0 (2026-04-21)

### Changed

- **Windows-only declaration** — assembly-level `[SupportedOSPlatform("windows")]` plus "Windows-only" wording in the package description and READMEs signal the platform requirement (Task Scheduler + Credential Manager). TargetFramework stays `net8.0` because `PackAsTool` does not support Windows-specific TFMs — the attribute triggers CA1416 analyzer warnings for non-Windows consumers instead.
- **Modern MCP package metadata** — `.mcp/server.json` now uses the latest identifier-based NuGet schema with `runtimeHint: "dnx"` for current MCP client and VS Code integration.
- **Scheduler abstraction** — add `IJobScheduler` with a Windows Task Scheduler implementation to isolate platform scheduling behavior and prepare for future non-Windows backends.
- **dnx fallback for scheduled runs** — when no runner executable is on disk, schtasks entries fall back to `dnx FieldCure.AssistStudio.Runner@<Major>.* --yes exec <task-id>`, letting AssistStudio 0.17+ hosts schedule tasks without installing the Runner global tool first.
- **Interactive scheduler behavior documented** — scheduled runs continue to use Windows Task Scheduler interactive mode, so users must be logged in when triggers fire.

---

## v1.2.0 (2026-04-14)

### Changed

- **Ai.Providers 0.4.0** — update dependency to pick up export, token compression, and attachment deduplication
- **English-only schedule_once examples** — replace Korean examples with English in tool descriptions for broader compatibility

---

## v1.1.4 (2026-04-08)

- **Fix build break** — adapt `McpServerPool.ExtractTextResult` to return `ToolExecutionResult` record type (Ai.Providers API change)

## v1.1.3 (2026-04-07)

- **Fix: Guide LLM to omit command in mcp_servers** — tool descriptions now instruct LLM to provide only `id` and `name`, preventing hallucinated command paths at the source

## v1.1.2 (2026-04-07)

- **Fix: Always override known server commands with auto-detected paths** — known servers (essentials, outbox) always get the system-resolved path regardless of what the LLM provided. Replaces file-existence validation which could not keep up with varied hallucinated paths

## v1.1.1 (2026-04-07)

- **Fix: Validate command path before skipping auto-resolve** — `ResolveCommands` now checks that absolute command paths exist on disk. LLM-hallucinated paths are replaced with auto-detected installed tool paths instead of failing at exec time

## v1.1.0 (2026-04-07)

- **New: One-time schedule (`schedule_once`)** — `create_task` and `update_task` accept ISO 8601 datetime for one-time execution via schtasks `/SC ONCE`. Use for relative-time requests like "in 5 minutes", "today at 6pm", "tomorrow at 9am". Mutually exclusive with cron `schedule`
- **Fix: Auto-resolve missing MCP server commands** — `McpServerConfig.ResolveCommands()` fills in missing command paths from auto-detected installed dotnet tools. Applied in CreateTaskTool, UpdateTaskTool (data quality), and TaskExecutor (defensive fallback). Fixes "MCP server is stdio but has no command" when LLM creates tasks with server name only

## v1.0.0 (2026-04-07)

- **Auto-bootstrap stateless MCP servers** — exec mode auto-detects installed servers (Essentials, Outbox) when no MCP servers are configured, resolving to full paths for PATH-independent execution
- **AllowedTools null = all tools** — null means all discovered tools are permitted; explicit empty list means no tools (safe tools only). Breaking change from v0.x where null meant no tools
- **Round-by-round execution logging** — `ExecutionLog.Rounds` now populated from `AgentLoopResult.Messages` with full tool call arguments and results for audit trails
- **Scheduler-aware system prompt** — CONTEXT section tells LLM that scheduling is handled externally, preventing it from attempting to set up cron jobs or recurring automation
- **Full XML documentation** — `GenerateDocumentationFile` enabled, all public and private members documented
- **Requires FieldCure.Ai.Execution 0.2.0+** for `AgentLoopResult.Messages` support

## v0.5.0 (2026-04-03)

- **AgentLoop extraction** — LLM execution loop replaced with shared `FieldCure.Ai.Execution.AgentLoop`, eliminating ~120 lines of inline loop code from TaskExecutor
- **MCP SDK 1.2.0** — upgraded ModelContextProtocol from 1.1.0 to 1.2.0
- **Removed retry logic** — `CompleteWithRetryAsync` removed; retry is now the caller's responsibility (task-level re-execution via schtasks serves as retry)
- **SafeTools moved** — safe tool allowlist (`get_environment`, `run_javascript`) moved from TaskExecutor to McpServerPool where filtering actually occurs

## v0.4.0 (2026-04-02)

- **Fix: schtasks tool path resolution** — `ResolveToolPath()` now checks `%LOCALAPPDATA%\FieldCure\AssistStudio\tools\` first, fixing FILE_NOT_FOUND errors when schtasks triggers the runner executable
- **Fix: cron `*` normalization** — bare `*` is now normalized to `*/1` before schtasks mapping, so `0 * * * *` and `0 */1 * * *` are handled identically

## v0.3.0 (2026-03-31)

- **Default MCP servers** — `defaultMcpServers` in runner.json, auto-bootstrapped for every task execution
- **Essentials auto-detection** — `BuildFromVault` includes FieldCure.Mcp.Essentials if installed
- **Safe tools bypass** — `get_environment` and `run_javascript` always allowed regardless of AllowedTools
- **`exclude_default_servers`** — per-task flag to opt out of default servers
- **Core dependency removed** — replaced `FieldCure.AssistStudio.Core` with `FieldCure.Ai.Providers` for independent releases
- **Test isolation** — `RUNNER_DATA_DIR` env var support; tests use temp directory

## v0.2.0 (2026-03-30)

- **Auto-config from Credential Manager** — `serve` mode auto-generates `runner.json` by scanning Windows Credential Manager for known provider API keys when no presets are configured
- **API key lookup fix** — resolve API keys by provider type (matching AssistStudio's storage format) instead of preset name
- **Preset resolution fallback** — `ResolvePreset` now falls back to matching by provider type when exact preset name match fails
- **CredentialService rewrite** — switch from direct PasswordVault API to `CredEnumerateW` P/Invoke for `PackAsTool` compatibility (net8.0 TFM)
- **Enhanced `config init`** — supports `--preset`, `--provider`, `--model`, `--if-missing` flags

## v0.1.0 (2026-03-30)

Initial release.

- **Dual-mode operation** — MCP server (`serve`) for task CRUD + execution, headless CLI (`exec`) for scheduled runs
- **7 MCP tools** — `create_task`, `update_task`, `delete_task`, `list_tasks`, `run_task`, `get_task_history`, `get_execution_status`
- **6-phase execution pipeline** — initialize, MCP bootstrap, LLM loop, summarize, notify, cleanup
- **Windows Task Scheduler integration** — cron-to-schtasks mapping (minute, hourly, daily, weekly, monthly)
- **Multi-provider LLM support** — Claude, OpenAI, Gemini, Ollama, Groq via AssistStudio.Core
- **MCP server orchestration** — tasks bootstrap configured MCP servers (Outbox, RAG, Filesystem, custom)
- **Safety-first tool control** — AllowedTools null = no tools; explicit allowlist required for headless execution
- **Secure credential storage** — Windows Credential Manager (DPAPI), shared with AssistStudio
- **SQLite storage** — WAL mode for concurrent serve + exec access; Tasks + TaskExecutions tables
- **Execution logging** — DB summary + detailed JSON logs with full conversation history per round
- **Result delivery** — optional Outbox channel notification with fallback channel for failures
- **CLI credential management** — `config init`, `set-credential`, `get-credential` subcommands
- **LLM retry policy** — configurable exponential backoff (3 attempts default)
- **Log retention** — automatic cleanup of old execution logs (30 days default)
