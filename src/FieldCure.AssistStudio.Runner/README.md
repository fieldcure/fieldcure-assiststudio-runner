# FieldCure.AssistStudio.Runner

**Headless LLM task automation engine** — define natural language tasks, schedule them via Windows Task Scheduler (recurring or one-time), and get results delivered through Slack, Telegram, Email, KakaoTalk, or Discord. Runs as an MCP server for task management or standalone for headless execution.

## Install

```bash
dotnet tool install -g FieldCure.AssistStudio.Runner
```

## Quick Start

```bash
# Create runner.json config template
assiststudio-runner config init

# Set API key
assiststudio-runner config set-credential "Claude Sonnet" sk-ant-api03-...

# Start MCP server
assiststudio-runner serve
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

## Tools (7)

| Tool | Description | Confirmation |
|------|-------------|:------------:|
| `create_task` | Create a task with prompt, schedule, and MCP servers | Required |
| `update_task` | Modify task fields (partial update) | Required |
| `delete_task` | Delete a task and its history | Required |
| `list_tasks` | List all tasks with last execution status | — |
| `run_task` | Start task execution (async or wait up to 60s) | Required |
| `get_task_history` | Query execution history for a task | — |
| `get_execution_status` | Check status of a running execution | — |

## Execution Modes

| Mode | Command | Purpose |
|------|---------|---------|
| **Serve** | `assiststudio-runner serve` | MCP server for task CRUD + execution |
| **Exec** | `assiststudio-runner exec <task-id>` | Headless single-task execution |
| **Config** | `assiststudio-runner config <cmd>` | Credential and configuration management |

## Requirements

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Windows (required for Task Scheduler and Credential Manager)
- [FieldCure.Ai.Providers](https://www.nuget.org/packages/FieldCure.Ai.Providers) (bundled)
- [FieldCure.Ai.Execution](https://www.nuget.org/packages/FieldCure.Ai.Execution) (bundled)

## See Also

Part of the [AssistStudio ecosystem](https://github.com/fieldcure/fieldcure-assiststudio#ecosystem).

- [GitHub](https://github.com/fieldcure/fieldcure-assiststudio-runner)
- [MCP Specification](https://modelcontextprotocol.io)
