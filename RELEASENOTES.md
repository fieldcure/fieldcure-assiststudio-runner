# Release Notes

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
- **6-phase execution pipeline** — initialize, MCP bootstrap, LLM loop (CompleteAsync), summarize, notify, cleanup
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
