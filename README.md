# AssistStudio Runner

[![NuGet](https://img.shields.io/nuget/v/FieldCure.AssistStudio.Runner)](https://www.nuget.org/packages/FieldCure.AssistStudio.Runner)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/fieldcure/fieldcure-assiststudio-runner/blob/main/LICENSE)

A headless LLM task automation engine that executes natural language tasks on schedule and delivers results through configured channels. Built as a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server with the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Features

- **Dual-mode operation** — MCP server (`serve`) for task management, headless CLI (`exec`) for scheduled execution
- **7 MCP tools** — `create_task`, `update_task`, `delete_task`, `list_tasks`, `run_task`, `get_task_history`, `get_execution_status`
- **Windows Task Scheduler integration** — cron expressions automatically mapped to `schtasks` entries
- **Shared AgentLoop** — LLM execution powered by [Ai.Execution](https://www.nuget.org/packages/FieldCure.Ai.Execution) (same loop used by SubAgentExecutor)
- **Multi-provider LLM support** — Claude, OpenAI, Gemini, Ollama, Groq via [Ai.Providers](https://www.nuget.org/packages/FieldCure.Ai.Providers)
- **MCP server orchestration** — tasks can bootstrap any MCP servers (Outbox, RAG, Filesystem, custom)
- **Flexible tool control** — `AllowedTools` null = all tools permitted; explicit list for fine-grained control; empty list = safe tools only
- **Secure credentials** — API keys in Windows Credential Manager (DPAPI), shared with AssistStudio
- **Execution logging** — DB summary + detailed JSON logs with full conversation history
- **One-time scheduling** — `schedule_once` with ISO 8601 datetime for single-execution tasks ("5분 후에", "내일 9시에")
- **Result delivery** — send results via Outbox channels (Slack, Telegram, Email, KakaoTalk, Discord)

## Installation

### dotnet tool (recommended)

```bash
dotnet tool install -g FieldCure.AssistStudio.Runner
```

After installation, the `assiststudio-runner` command is available globally.

### From source

```bash
git clone https://github.com/fieldcure/fieldcure-assiststudio-runner.git
cd fieldcure-assiststudio-runner
dotnet build
```

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Windows (required for Task Scheduler and Credential Manager)

## Configuration

### Auto-configuration (recommended)

When launched in `serve` mode with no `runner.json`, Runner automatically scans Windows Credential Manager for known provider API keys and generates the config file. If you use AssistStudio, API keys are already stored — no manual setup needed.

### Manual Setup

```bash
# Create runner.json config template
assiststudio-runner config init

# Set API key for a provider preset
assiststudio-runner config set-credential "Claude" sk-ant-api03-...

# Verify (displays masked value)
assiststudio-runner config get-credential "Claude"
```

The config file is created at `%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/runner.json`:

```json
{
  "defaultPresetName": "Claude",
  "presets": {
    "Claude": {
      "providerType": "Claude",
      "modelId": "claude-sonnet-4-20250514"
    }
  },
  "fallbackChannel": "runner-alerts",
  "logRetentionDays": 30
}
```

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "runner": {
      "command": "assiststudio-runner",
      "args": ["serve"]
    }
  }
}
```

### VS Code (Copilot)

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "runner": {
      "command": "assiststudio-runner",
      "args": ["serve"]
    }
  }
}
```

### From source (without dotnet tool)

```json
{
  "mcpServers": {
    "runner": {
      "command": "dotnet",
      "args": [
        "run",
        "--project", "C:\\path\\to\\fieldcure-assiststudio-runner\\src\\FieldCure.AssistStudio.Runner",
        "--", "serve"
      ]
    }
  }
}
```

## Tools

| Tool | Description | Confirmation |
|------|-------------|:------------:|
| `create_task` | Create a task with prompt, schedule, and MCP server config | Required |
| `update_task` | Modify task fields — partial update, only changed fields | Required |
| `delete_task` | Delete a task, its executions, and log files | Required |
| `list_tasks` | List tasks with filtering and last execution status | — |
| `run_task` | Start execution (async default, optional 60s wait) | Required |
| `get_task_history` | Query execution history with status filtering | — |
| `get_execution_status` | Check real-time status of an execution | — |

## Usage

### Conversation Example

```
User: "Summarize competitor news every morning at 9 AM and send it to Slack"
  LLM → create_task (schedule: "0 9 * * 1-5", mcp_servers: [outbox, rag])

User: "Run a test"
  LLM → run_task (wait: true) → reports result

User: "Exclude weekends"
  LLM → update_task (schedule: "0 9 * * 1-5")

User: "What were yesterday's results?"
  LLM → get_task_history (limit: 1)
```

### Execution Modes

| Mode | Command | Purpose |
|------|---------|---------|
| **Serve** | `assiststudio-runner serve` | MCP server (stdio) for task management |
| **Exec** | `assiststudio-runner exec <task-id>` | Headless execution (called by schtasks) |
| **Config** | `assiststudio-runner config init` | Create config template |
| | `assiststudio-runner config set-credential <key> <value>` | Store API key or env var |
| | `assiststudio-runner config get-credential <key>` | Retrieve credential (masked) |

### Exit Codes (exec mode)

| Code | Meaning |
|------|---------|
| `0` | Succeeded |
| `1` | Failed |
| `2` | Timed out |
| `3` | Task not found |
| `4` | Already running |

## Scheduling

Cron expressions are automatically mapped to Windows Task Scheduler entries:

| Schedule | Parameter | schtasks |
|----------|-----------|----------|
| Once at specific time | `schedule_once: "2026-04-07T15:30:00+09:00"` | `/SC ONCE /SD 2026/04/07 /ST 15:30` |
| Every 30 minutes | `schedule: "*/30 * * * *"` | `/SC MINUTE /MO 30` |
| Every 2 hours | `schedule: "0 */2 * * *"` | `/SC HOURLY /MO 2` |
| Daily at 9:00 AM | `schedule: "0 9 * * *"` | `/SC DAILY /ST 09:00` |
| Weekdays at 9:00 AM | `schedule: "0 9 * * 1-5"` | `/SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 09:00` |
| Monthly on the 1st | `schedule: "0 9 1 * *"` | `/SC MONTHLY /D 1 /ST 09:00` |

## Data Storage

| Data | Location |
|------|----------|
| Configuration | `%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/runner.json` |
| Task database | `%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/runner.db` (SQLite, WAL) |
| Execution logs | `%LOCALAPPDATA%/FieldCure/AssistStudio/Runner/logs/{id}.json` |
| API keys | Windows Credential Manager (`FieldCure.AssistStudio`) |

## Project Structure

```
src/FieldCure.AssistStudio.Runner/
├── Program.cs                    # Dual-mode entry point (serve/exec/config)
├── Models/                       # RunnerTask, TaskExecution, RunnerConfig, ExecutionLog
├── Storage/TaskStore.cs          # SQLite storage with WAL mode
├── Credentials/                  # ICredentialService + Windows PasswordVault
├── Scheduling/                   # CronToSchtasks parser, SchedulerService (schtasks)
├── Execution/                    # TaskExecutor (AgentLoop-based), McpServerPool
├── Tools/                        # 7 MCP tools for serve mode
└── Configuration/ConfigRunner.cs # CLI config subcommands
```

## Development

```bash
# Build
dotnet build

# Test
dotnet test

# Pack
dotnet pack -c Release
```

## See Also

Part of the [AssistStudio ecosystem](https://github.com/fieldcure/fieldcure-assiststudio#ecosystem).

## License

[MIT](LICENSE)
