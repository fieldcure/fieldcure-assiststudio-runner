# Release Notes

## v1.0.0

- **Auto-bootstrap stateless MCP servers** — exec mode auto-detects installed servers (Essentials, Outbox) when no MCP servers are configured, resolving to full paths for PATH-independent execution
- **AllowedTools null = all tools** — null means all discovered tools are permitted; explicit empty list means no tools (safe tools only). Breaking change from v0.x where null meant no tools
- **Round-by-round execution logging** — `ExecutionLog.Rounds` now populated from `AgentLoopResult.Messages` with full tool call arguments and results for audit trails
- **Scheduler-aware system prompt** — CONTEXT section tells LLM that scheduling is handled externally, preventing it from attempting to set up cron jobs or recurring automation
- **Full XML documentation** — `GenerateDocumentationFile` enabled, all public and private members documented
- **Requires FieldCure.Ai.Execution 0.2.0+** for `AgentLoopResult.Messages` support

## v0.5.0

- **AgentLoop extraction** — LLM execution loop replaced with shared `FieldCure.Ai.Execution.AgentLoop`, eliminating ~120 lines of inline loop code from TaskExecutor
- **MCP SDK 1.2.0** — upgraded ModelContextProtocol from 1.1.0 to 1.2.0
- **Removed retry logic** — `CompleteWithRetryAsync` removed; retry is now the caller's responsibility (task-level re-execution via schtasks serves as retry)
- **SafeTools moved** — safe tool allowlist (`get_environment`, `run_javascript`) moved from TaskExecutor to McpServerPool where filtering actually occurs

## v0.4.0

- **Fix: schtasks tool path resolution** — `ResolveToolPath()` now checks `%LOCALAPPDATA%\FieldCure\AssistStudio\tools\` first, fixing FILE_NOT_FOUND errors when schtasks triggers the runner executable
- **Fix: cron `*` normalization** — bare `*` is now normalized to `*/1` before schtasks mapping, so `0 * * * *` and `0 */1 * * *` are handled identically

## v0.3.0

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
