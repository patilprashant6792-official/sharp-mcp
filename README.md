# sharp-mcp

> **The AI-native C# dev assistant** — real NuGet DLL reflection, Roslyn codebase analysis, surgical file editing, and live build diagnostics. One self-hosted MCP server. Zero code leaves your machine.

Built on **.NET 10** · Powered by **Roslyn** · Backed by **Redis** · Reflected via **MetadataLoadContext**

---

## Featured On

- [I Just Wanted Claude to Stop Hallucinating My NuGet APIs. Somehow I Ended Up Building a Full C# Dev Assistant.](https://dev.to/prashant_patil_9e62d3fa8a/i-just-wanted-claude-to-stop-hallucinating-my-nuget-apis-somehow-i-ended-up-building-a-full-c-dev-12om) — dev.to


## Why I built this — the reasoning behind every decision

Most MCP servers are wrappers. sharp-mcp started as a question: *what would a C# developer actually need if they built this themselves, for themselves?*

Here's the full reasoning — every problem, every tradeoff, every decision in sequence.

---

### Problem 1: Generic AI tooling is wasteful for .NET backend work

Claude Code, GitHub Copilot, Cursor — they're all built for every language, every ecosystem. That generality has a real cost. When you're doing C# backend work, you're paying token overhead for abstractions you don't need. I wanted something that only knows C#, only knows .NET, and uses that focus to do the job better.

---

### Problem 2: Direct file reading was the obvious first move — and it was too slow

I started where everyone starts: read the file, pass it to the model. It worked. It was also impractically slow for any real codebase. Search latency was high, large files burned context fast, and it touched the real filesystem on every request. Not viable — and that failure made the solution obvious: parse the structure once, cache it, and serve from memory.

---

### Problem 3: Roslyn was the right answer, but it needed a home

Roslyn gives you a full AST — classes, methods, line spans, dependencies, everything structured. But parsing on every request defeats the purpose. The data needed to live somewhere fast.

Redis was the obvious choice. If you're a .NET backend developer, you already have Redis. It's not an exotic dependency — it's infrastructure you trust. So: parse once with Roslyn, cache in Redis, serve in milliseconds.

---

### Problem 4: NuGet hallucinations — the one nobody talks about

This was the most interesting problem to solve. AI models generate correct-looking C# that doesn't compile because they don't know what's actually inside the NuGet packages you're using. Documentation is incomplete. Training data is stale. The model guesses.

The fix: stop guessing, decode the binary. `MetadataLoadContext` loads the real `.nupkg`, reads the actual IL, and returns real signatures — real method names, real parameter types, real generics. No docs, no guessing. The model gets the same ground truth the compiler uses.

Frontier LLMs can web search documentation for behavioral context. What they can't do is know the exact method signatures in your specific package version. That's what this solves.

---

### Problem 5: The cache needs to stay warm

A cached Roslyn analysis is only useful if it reflects the current state of the file. The solution: a file watcher architecture that listens for changes and invalidates or updates the relevant cache entries automatically. Every edit you make in your IDE propagates to the cache without manual intervention. Global code search runs against warm cache data — within seconds, without touching the real codebase.

---

### Problem 6: Claude.ai needs a bridge to localhost

The last piece was practical. An LLM running over the web can't reach your local machine directly. ngrok provides the SSE tunnel. It's a real dependency and I won't pretend otherwise — a VS Code extension is on the roadmap to eliminate it. But for now it's one command, and the rest of the setup is `dotnet run`.

---

### The UI that ties it together

One thing I didn't want was a tool that required editing JSON config files or restarting a server every time you added a project. So there's a built-in project management UI at `/config.html`.

You point it at a solution folder. That's it. sharp-mcp indexes everything — classes, methods, dependencies, file sizes — and the LLM immediately has full structural awareness of that codebase. Each project is independently enabled, re-indexable on demand, and deletable without touching any config file.

And yes — sharp-mcp manages its own source code through this same UI. The tool is its own first user.

One detail worth calling out explicitly: **sensitive files never reach the model.** `appsettings.json`, secrets, credentials, `.env` files — they're on a blocklist at the file read layer. The UI gives you full control over what's exposed; the server enforces what's protected.

---

### The three principles everything was built around

- **Speed** — solved by Redis cache + file watcher keeping it warm
- **Token efficiency** — every tool description tells the model when *not* to call it; batch modes, size hints, and "last resort" labels are all deliberate prompt engineering baked into the architecture
- **Correctness** — NuGet reflection via `MetadataLoadContext` means the model works with real signatures, not approximations

---

### What this does not replace

This is not an inline autocomplete tool. It does not suggest the next line as you type. It does not integrate into your IDE as an extension.

What it replaces is the *reasoning session* — when you open a chat, describe a problem, and ask the LLM to understand your codebase, plan a refactor, check what breaks if you change a signature, look up how a dependency actually works, or implement a feature that spans multiple files. Those sessions are where context limits, hallucinated APIs, and cloud data exposure all cause real damage. That is exactly the scope this is built for.

---

### Coding system prompt

A public system prompt is available in [`prompts/CODING_SYSTEM_PROMPT.md`](prompts/CODING_SYSTEM_PROMPT.md). It combines engineering principles with sharp-mcp tool usage characteristics. Paste it before starting a session — it works with any LLM that supports MCP, not just Claude.

---


## Tech stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 10, ASP.NET Core |
| C# analysis | Microsoft.CodeAnalysis.CSharp (Roslyn) 5.0 |
| MCP host | ModelContextProtocol + ModelContextProtocol.AspNetCore |
| Cache & index | Redis via StackExchange.Redis + NRedisStack |
| NuGet reflection | NuGet.Protocol, NuGet.Packaging, System.Reflection.MetadataLoadContext |
| Config serialization | Tomlyn (TOML) |
| Transport | Server-Sent Events (SSE) over HTTPS via ngrok |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Redis](https://redis.io/docs/getting-started/) (local or Docker)
- [ngrok](https://ngrok.com/download) (to expose the server to Claude.ai)
- A [Claude.ai](https://claude.ai) account with MCP connector support
- Windows, macOS, or Linux

---

## Setup

### 1. Clone and build

```bash
git clone https://github.com/patilprashant6792-official/dotnet-mcp-server
cd dotnet-mcp-server/LocalMcpServer
dotnet build
```

### 2. Start Redis

```bash
# Docker — quickest path
docker run -d -p 6379:6379 redis:latest

# Or use an existing local Redis on the default port 6379
```

### 3. Configure

Edit `appsettings.Development.json` for local overrides:

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379",
    "ConnectTimeout": 3000,
    "SyncTimeout": 3000,
    "ConnectRetry": 2
  },
  "AnalysisCache": {
    "TtlHours": 24,
    "RefreshIntervalMinutes": 60,
    "IndexingConcurrency": 4,
    "FileWatcherDebounceMs": 300
  }
}
```

| Setting | Default | What it controls |
|---------|---------|------------------|
| `TtlHours` | 24 | How long each file analysis lives in Redis |
| `RefreshIntervalMinutes` | 60 | How often the background indexer re-scans all projects |
| `IndexingConcurrency` | 4 | Max parallel Roslyn parses during bulk indexing |
| `FileWatcherDebounceMs` | 300 | Wait after a file-change event before re-analysing |

### 4. Run the server

```bash
cd LocalMcpServer
dotnet run
# Starts on http://localhost:5000
```

### 5. Register your projects

Open the web UI at `http://localhost:5000/config.html` and add your project paths. The UI is a plain HTML page served by the server itself — no separate frontend to run.

Alternatively via the REST API:

```bash
curl -X POST http://localhost:5000/api/project-config \
  -H "Content-Type: application/json" \
  -d '{"name": "MyApi", "path": "C:/source/MyApi", "description": "Main API service"}'
```

Project config is persisted in Redis — register once, it survives server restarts.

### 6. Expose with ngrok

Claude.ai requires a publicly reachable HTTPS URL. ngrok creates one in seconds:

```bash
# Install
brew install ngrok          # macOS
winget install ngrok        # Windows

# Tunnel
ngrok http 5000
```

ngrok prints:
```
Forwarding  https://a1b2-203-0-113-42.ngrok-free.app -> http://localhost:5000
```

Copy that `https://` URL.

> **Free plan note:** The ngrok URL changes on every restart. Update the Claude.ai connector URL when this happens, or use a paid ngrok plan for a stable domain.

### 7. Connect Claude.ai

In Claude.ai → **Settings** → **Connectors** → **Add connector**:

| Field | Value |
|-------|-------|
| Name | `sharp-mcp` |
| URL | `https://<your-ngrok-id>.ngrok-free.app/sse` |

Claude discovers all tools automatically via the MCP capability negotiation protocol.

### 7b. Connect Claude Code Desktop (alternative)

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "sharp-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://<your-ngrok-id>.ngrok-free.app/sse", "--transport", "sse-only"]
    }
  }
}
```

`mcp-remote` is an npm bridge that proxies SSE-based MCP servers into the stdio transport Claude Code Desktop expects. `npx -y` installs it on demand — no global install needed.

---

## All 22 MCP tools

### Code analysis

| Tool | Description |
|------|-------------|
| `analyze_c_sharp_file` | Roslyn-extracted metadata: namespaces, classes, methods, properties, DI graph, line ranges. Batch mode: comma-separated paths, no spaces. Toggle `includePrivateMembers=true` for internals. |
| `fetch_method_implementation` | Complete method body with per-line line numbers. Batch mode: comma-separated method names from the same file. |
| `read_file_content` | Raw file content with three modes: **full file** (default), **line range** (`startLine` + `endLine`, 1-based inclusive), **search/grep** (`query`, case-insensitive substring — returns only matching lines with line numbers). Modes are mutually exclusive. Blocked for `appsettings.json`, secrets, `.env`, and `bin/`/`obj/` paths. |
| `analyze_method_call_graph` | All callers (file, class, line) and outgoing calls for a method. Optionally includes test files (`includeTests`). Paginated — `page` / `pageSize` up to 200. |

### Project exploration

| Tool | Description |
|------|-------------|
| `get_project_skeleton` | ASCII folder tree with file sizes and NuGet package list. Pass `"*"` to list all registered projects. Supports `sinceTimestamp` for incremental diffs. |
| `search_folder_files` | Paginated file listing within a folder. Optional `searchPattern` filename filter (case-insensitive). Use when a folder has 50+ files. |
| `search_code_globally` | Name/keyword search across one project or all (`"*"`). Optional `memberType` filter (Class/Interface/Method/Property/Field/All). Optional `caseSensitive` flag. Paginated — `page` / `pageSize` up to 200. |

### File operations

| Tool | Description |
|------|-------------|
| `write_file` | Create or overwrite files. Modes: `create` (fails if exists) / `overwrite` (fails if missing) / `upsert` (always succeeds). Parent dirs created automatically. Batch supported. |
| `edit_lines` | Apply multiple `patch` / `insert` / `delete` / `append` operations to a file atomically. All patches validated then applied bottom-up — supply original line numbers from your last read. Overlap-validated. |
| `move_file` | Move or rename files. Same-folder destination = rename. Cross-folder = move. All-or-nothing batch: all destinations validated before any file moves. |
| `delete_file` | Delete files. Each file is independent — partial success is possible. Blocked paths enforced. |
| `create_folder` | Create one or more folders including nested paths. Idempotent — existing folders are not an error. |
| `move_folder` | Move or rename a folder. Single operation only (high-impact). Evicts all Redis keys under the old path before moving. Does not update C# namespaces. |
| `delete_folder` | Recursively delete folders. Deepest-first ordering. Non-existent folders silently skipped. Blocked: `bin/`, `obj/`, `.git/`, `.vs/`, `node_modules/`, `logs/`. |

### NuGet exploration

**IntelliSense workflow (default — use these):**

| Tool | Description |
|------|-------------|
| `search_nu_get_packages` | Queries NuGet.org by package ID or keywords. Returns version, download count, and tags. Use the full ID (`Microsoft.EntityFrameworkCore`, not `EFCore`). |
| `get_package_namespaces` | Lists all namespaces a package exposes. Always the first step on an unfamiliar package. Feeds into `get_namespace_types`. |
| `get_namespace_types` | Type index for one namespace — names, kind (Class/Interface/Enum/Struct), and member count hints (`[2 ctors, 14 methods]`). ~10 tokens per type. No member details yet. |
| `get_type_surface` | Constructors + methods of ONE type. For client/service types you instantiate and call. Methods collapsed; overloads shown as `+ N overloads`. |
| `get_type_shape` | Properties of ONE type — its readable/writable shape. For result types and options/config classes. Enums and structs show their valid values. |
| `get_method_overloads` | Expands overload groups collapsed in `get_type_surface` into individual signatures with full parameter lists. |

### .NET CLI

| Tool | Description |
|------|-------------|
| `execute_dotnet_command` | Runs `dotnet build` or `dotnet add package` against a configured project. Supported commands: `build` (clean + build by default; `--no-clean` for incremental, ~5× faster), `add-package` (installs a NuGet package by ID and optional version). Returns structured Roslyn diagnostics (severity, code, file, line, column, message) — not raw text. Flags: `--no-clean` \| `--warnings` \| `--page <n>` \| `--page-size <n>` \| `--target <path>`. Resolves build target automatically: solution file at root → single `.csproj` anywhere in tree → ambiguous (returns `availableTargets` list so Claude can re-call with `--target`). |

### Utility

| Tool | Description |
|------|-------------|
| `get_date_time` | Current date/time in UTC, server local time, or any IANA timezone (e.g. `Asia/Kolkata`). Returns `localDateTime`, `utcDateTime`, `timeZone`, and `unixTimestamp`. |
---

## Recommended workflows

### Exploring and editing code

```
1. get_project_skeleton("*")                     orient — see all registered projects
2. get_project_skeleton("MyApi")                 understand the target project structure
3. analyze_c_sharp_file("MyApi", "Svc/A.cs,Svc/B.cs")   batch — inspect two classes at once
4. fetch_method_implementation("MyApi", "Svc/A.cs", "ProcessOrder")   read the method
5. analyze_method_call_graph("MyApi", "Svc/A.cs", "ProcessOrder")      who calls it?
6. edit_lines(...)                               make the change
7. execute_dotnet_command("MyApi", "build")      verify compilation — catch errors before moving on
```

### Installing a NuGet package and using it

```
1. search_nu_get_packages("Serilog")                      find the exact package ID
2. execute_dotnet_command("MyApi", "add-package", ["Serilog", "4.2.0"])   install it
3. execute_dotnet_command("MyApi", "build")               confirm it resolves cleanly
4. get_package_namespaces("Serilog")                      explore what it exposes
5. get_namespace_types("Serilog", "Serilog")              find the types
6. get_type_surface("Serilog", "Serilog", "Log")          see what you can call
```

### Looking up a library API

```
1. get_package_namespaces(packageId)                  what namespaces does it expose?
2. get_namespace_types(packageId, namespace)           what types are in this namespace?
3. get_type_surface(packageId, namespace, typeName)    what can I call on this type?
4. get_type_shape(packageId, namespace, returnType)    what does the return type look like?
5. get_method_overloads(...)                           expand a specific overload group if needed
```

Each call fetches only what the next decision requires — the same order a developer follows in the IDE.

---

**Design rules enforced throughout:**
- MCP tool classes are thin wrappers — no business logic, only parameter parsing and formatting
- Every tool class depends only on interfaces — concrete implementations are injected
- All services are registered as singletons; thread safety is handled inside each service
- Redis keys follow `{type}:{project}:{normalizedPath}` — lowercase, forward-slash normalized
- Blocked path patterns are enforced at the `IFileModificationService` layer, not in individual tools

---

## Contributing

Pull requests are welcome.

- Keep files under 500 lines; split by responsibility
- New MCP tools go in `MCPServers/` as thin wrappers over a new service interface
- New services use constructor injection and must be registered in `Program.cs`
- Redis keys must follow the `type:project:path` convention in `RedisAnalysisCacheService`
- Never access `appsettings.json` or `.env` directly from a tool handler — use `IOptions<T>`

---

## License

MIT
