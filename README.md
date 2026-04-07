# dotnet-mcp-server

A self-hosted [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that gives Claude deep, structured, token-efficient access to your local C# codebase — without ever sending raw source files to the cloud.

Built on .NET 10, powered by Roslyn, backed by Redis.

---

## Why this exists

Most AI coding tools work by dumping entire files into an LLM's context window. This works for small files but breaks down across a real codebase:

- **Context fills up fast.** A 500-line service class costs ~2 000 tokens just to load. Ten files and you've burned your entire budget before writing a single line.
- **The AI hallucinates library APIs.** It was trained on an older version of the package than the one in your `.csproj`.
- **IDE extensions are locked to one project.** Managing a suite of microservices means constantly switching windows and losing context.
- **Your code leaves your machine.** Every paste into a chat box or IDE extension upload is a data transfer.

`dotnet-mcp-server` solves all four problems. It exposes your codebase through structured MCP tools that Claude can call selectively — fetching only what it needs, from the exact version of every dependency installed, across all your projects at once, with everything staying on your machine.

---

## How it works

```
Claude.ai  ──HTTPS──►  ngrok tunnel  ──SSE──►  dotnet-mcp-server  ──Roslyn──►  Your C# source
                                                       │
                                                       ├──Redis──►  Analysis cache + project index
                                                       │
                                                       └──NuGet──►  Installed package reflection
```

1. Claude calls an MCP tool (e.g. `analyze_c_sharp_file`).
2. The server checks Redis for a cached AST analysis of that file.
3. On a cache miss, Roslyn parses the file and extracts structured metadata (classes, methods, fields, attributes, DI dependencies, line ranges). The result is cached.
4. A background `FileSystemWatcher` invalidates cache entries whenever you save a file in your IDE.
5. A scheduled background indexer pre-warms the cache on startup so the first request is never cold.
6. Claude receives a compact, structured representation — not raw source — and uses it to reason about your code.

---

## Features

### Token-optimized code reading

A tiered reading strategy keeps context usage minimal:

| Step | Tool | When to use | Typical cost |
|------|------|-------------|--------------|
| 1 | `get_project_skeleton` | Understand project structure | ~200 tokens |
| 2 | `analyze_c_sharp_file` | Understand a class's API surface | ~300–500 tokens |
| 3 | `fetch_method_implementation` | Read a specific method body | ~80–150 tokens |
| 4 | `read_file_content` | Non-C# files or tiny scripts | full file |

Compared to dumping entire files, this approach saves **10–20× tokens** for a typical multi-service session.

### Roslyn-powered C# analysis

- Extracts namespace, using directives, all classes with modifiers, base types, and interfaces
- Constructor parameters mapped as dependency injection graph
- Every method: signature, return type, parameters, attributes, XML docs, start/end line numbers
- Properties, fields, and constants with types and modifiers
- Batch mode: analyze up to N files in a single tool call
- Private member inclusion toggle for deep-dive debugging sessions

### Precise method implementation fetching

- Returns complete method body with line numbers for every line
- Batch mode: fetch multiple methods in one call
- Optional class scoping to resolve overloaded names
- Line numbers are exact — Claude can suggest `edit_lines` patches against them directly

### Method call graph analysis

Before changing a method signature or deleting a method, ask Claude to run `analyze_method_call_graph`. It walks every `.cs` file in the project using Roslyn syntax walkers and returns:

- Every caller with exact file path, class, and line number
- Outgoing calls from the target method
- Class resolution hints for interface dispatch

This is impact analysis that prevents breaking changes.

### Live file read/write operations

Claude can create, edit, move, and delete files directly — with safety built in:

- **Per-file semaphore locking** — concurrent writes to the same file are serialized, not dropped
- **Atomic batch validation** — for multi-file moves, all destinations are validated before any file moves; one failure aborts the batch
- **Path guard** — all operations are sandboxed to registered project roots; no path traversal possible
- **Blocked patterns** — `bin/`, `obj/`, `.git/`, secrets files, password/token filename patterns are permanently blocked
- **Cache invalidation on write** — every successful file write evicts the corresponding Redis cache entry so the next analysis call sees fresh code
- **`edit_lines` patches** — targeted line-range replacement instead of full file rewrites; bottom-up application prevents line drift

### NuGet package exploration

Eliminate hallucinated library APIs. The four-step exploration pipeline reflects the actual DLL installed in your project:

```
search_nu_get_packages  →  get_nu_get_package_namespaces
    →  get_namespace_summary  →  get_method_overloads
```

- Returns production-ready, copy-paste C# signatures
- Targets your exact installed version and framework
- `get_namespace_summary` returns all types, methods, and properties in a namespace in a single call
- `get_method_overloads` expands collapsed overload groups on demand

### Global code search

`search_code_globally` finds classes, methods, properties, interfaces, and fields by name or keyword across:

- A single project
- All registered projects simultaneously (`projectName="*"`)

Useful for security audits (`Authorize`), dependency analysis (`IUserService`), and pre-refactor impact checks.

### Multi-project management

Register all your microservices once. Every tool accepts a `projectName` parameter. Claude can reason across your entire solution in a single chat — no context switching, no copy-pasting between windows.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Redis](https://redis.io/docs/getting-started/) (local or Docker)
- [ngrok](https://ngrok.com/download) (to expose the local server to Claude.ai)
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
# Docker (quickest)
docker compose up -d

# Or use an existing local Redis instance on the default port 6379
```

### 3. Configure

Edit `appsettings.json` (or `appsettings.Development.json` for local overrides):

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "AnalysisCacheConfig": {
    "CacheTtlHours": 24,
    "MaxConcurrentIndexing": 4
  }
}
```

### 4. Register your projects

Open the web UI at `http://localhost:5000/config.html` after starting the server and add your project paths. Alternatively, projects can be registered via the REST API:

```bash
POST http://localhost:5000/api/project-config
Content-Type: application/json

{
  "name": "MyApi",
  "path": "C:/source/MyApi",
  "description": "Main API service"
}
```

Project configuration is persisted in Redis — you only need to register once.

### 5. Run the server

```bash
cd LocalMcpServer
dotnet run
# Server starts on http://localhost:5000
```

### 6. Expose the server with ngrok

Claude.ai requires a publicly reachable HTTPS URL to connect to your MCP server. Since the server runs locally, you need to tunnel it using [ngrok](https://ngrok.com).

**Install ngrok:**

```bash
# macOS
brew install ngrok

# Windows
winget install ngrok

# Or download directly from https://ngrok.com/download
```

**Start the tunnel** (in a separate terminal while the server is running):

```bash
ngrok http 5000
```

ngrok will print something like:

```
Forwarding  https://a1b2-203-0-113-42.ngrok-free.app -> http://localhost:5000
```

Copy the `https://...ngrok-free.app` URL — you'll use it in the next step.

> **Note:** The ngrok URL changes every time you restart ngrok on the free plan. Update the connector URL in Claude.ai whenever this happens. A paid ngrok plan gives you a static domain.

### 7. Connect Claude.ai

In Claude.ai → **Settings** → **Connectors** → **Add connector**:

| Field | Value |
|-------|-------|
| Name | `dotnet-mcp-server` (or anything you like) |
| URL | `https://<your-ngrok-id>.ngrok-free.app/sse` |

Claude will discover all available tools automatically.

> **Tip:** Every time you restart ngrok, update this URL in Claude.ai connector settings.

### 7b. Connect via Claude Code Desktop (alternative)

If you are using **Claude Code Desktop** instead of Claude.ai in the browser, add the following to your Claude Code Desktop config file (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "dotnet-mcp-server": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote",
        "https://<your-ngrok-id>.ngrok-free.app/sse",
        "--transport",
        "sse-only"
      ]
    }
  },
  "preferences": {
    "coworkScheduledTasksEnabled": false,
    "ccdScheduledTasksEnabled": true,
    "sidebarMode": "chat",
    "coworkWebSearchEnabled": true
  }
}
```

Replace `https://<your-ngrok-id>.ngrok-free.app` with your actual ngrok URL.

> **Note:** `mcp-remote` is an npm bridge package that proxies SSE-based MCP servers into the stdio transport Claude Code Desktop expects. It is installed on demand via `npx -y` — no global install needed.

---

## Available MCP tools

### Code analysis

| Tool | Description |
|------|-------------|
| `analyze_c_sharp_file` | Structured metadata for one or more `.cs` files (batch mode supported) |
| `fetch_method_implementation` | Complete method body with exact line numbers (batch mode supported) |
| `read_file_content` | Raw file content; blocked for sensitive files |
| `analyze_method_call_graph` | All callers and outgoing calls for a given method |

### Project exploration

| Tool | Description |
|------|-------------|
| `get_project_skeleton` | ASCII folder tree with file sizes, NuGet packages, and solution structure |
| `search_folder_files` | Paginated file listing within a folder with optional name filter |
| `search_code_globally` | Keyword/name search across one or all registered projects |

### File operations

| Tool | Description |
|------|-------------|
| `write_file` | Create or overwrite files (`create` / `overwrite` / `upsert` modes) |
| `edit_lines` | Targeted line-range patches applied atomically |
| `move_file` | Move or rename files with atomic batch validation |
| `delete_file` | Delete files (blocked paths enforced) |
| `create_folder` | Create folders including nested paths |
| `move_folder` | Move or rename a folder with cache eviction |
| `delete_folder` | Recursively delete folders (blocked paths enforced) |
| `get_file_info` | File metadata (exists, size, line count, last modified) without reading content |

### NuGet exploration

| Tool | Description |
|------|-------------|
| `search_nu_get_packages` | Search NuGet.org by exact package ID |
| `get_nu_get_package_namespaces` | List all namespaces in a package version |
| `get_namespace_summary` | All types and signatures in a namespace |
| `get_method_overloads` | Expand collapsed overload groups |

### Utility

| Tool | Description |
|------|-------------|
| `get_date_time` | Current date/time in UTC, local, or a specified timezone |

---

## Recommended Claude workflow

The tool descriptions embedded in each MCP tool guide Claude automatically, but understanding the intended workflow helps you write better prompts.

**For code changes:**

```
1. get_project_skeleton("*")          — orient across all projects
2. get_project_skeleton("MyApi")      — understand structure of the target project
3. analyze_c_sharp_file(...)          — inspect relevant classes (batch where possible)
4. fetch_method_implementation(...)   — drill into specific methods
5. analyze_method_call_graph(...)     — check impact before any signature change
6. edit_lines(...) / write_file(...)  — make the change
7. analyze_c_sharp_file(...)          — verify the result
```

**For NuGet usage:**

```
1. search_nu_get_packages("Microsoft.EntityFrameworkCore")
2. get_nu_get_package_namespaces(packageId, version)
3. get_namespace_summary(namespace, packageId, version)
4. get_method_overloads(...)          — only if overloads need expanding
```

---

## Architecture

```
LocalMcpServer/
├── MCPServers/                  # MCP tool definitions (thin wrappers over services)
│   ├── CodeAnalysisTools.cs
│   ├── CodeSearchTools.cs
│   ├── DateTimeTool.cs
│   ├── FileWriteTools.cs
│   ├── MethodCallGraphTools.cs
│   ├── NuGetExplorationTools.cs
│   └── ProjectSkeletonTools.cs
├── ProjectExplorationServices/  # Roslyn analysis, search, skeleton generation
├── FileModificationService/     # Atomic file read/write with per-file locking
├── FileUpdateService/           # FileSystemWatcher + debounced re-analysis
├── NugetServices/               # NuGet protocol + MetadataLoadContext reflection
├── Services/                    # Redis cache, project config, TOML serialization
├── BackgroundServices/          # Startup indexer + scheduled refresh
├── Controllers/                 # REST API for project registration
├── Middlewares/                 # Global exception handling
├── Models/                      # Shared data models
├── Configuration/               # Strongly-typed config classes
├── wwwroot/                     # Project registration web UI
├── Program.cs                   # DI wiring and MCP server registration
└── docker-compose.yml           # Redis for local development
```

**Key dependencies:**

| Package | Purpose |
|---------|----------|
| `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` | MCP server host and SSE transport |
| `Microsoft.CodeAnalysis.CSharp` (Roslyn) | AST parsing and syntax walking |
| `StackExchange.Redis` + `NRedisStack` | Analysis cache and project index |
| `NuGet.Protocol` + `NuGet.Packaging` | Package search and metadata |
| `System.Reflection.MetadataLoadContext` | DLL reflection for NuGet exploration |
| `Tomlyn` | TOML-based project configuration |

---

## Known limitations

- **No namespace update on file move.** `move_file` and `move_folder` move files and evict cache but do not rewrite `namespace` declarations or `using` directives. Update namespaces manually after a move.
- **No cache purge tool.** There is no MCP tool to force a full re-index. If the cache gets stale after a large refactor, restart the server to trigger the startup indexer, or wait for the scheduled refresh.
- **Call graph is syntactic only.** `analyze_method_call_graph` uses Roslyn syntax walkers, not a full semantic model. It finds method invocations by name but cannot resolve interface dispatch targets or dynamic calls.
- **Search result cap.** `search_code_globally` returns at most `topK` results (default 20) with no pagination cursor.

---

## Contributing

Pull requests welcome. Before submitting:

- Keep files under 500 lines; split by responsibility
- All new tools must go in `MCPServers/` as thin wrappers over a service interface
- New services must be registered via constructor injection
- Redis cache keys must follow the `project:relativepath` convention used by `RedisAnalysisCacheService`
- Do not read from `appsettings.json`, secrets files, or `.env` in tool handlers; use `IOptions<T>` instead

---

## License

MIT
