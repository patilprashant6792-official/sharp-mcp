# dotnet-mcp-server

A self-hosted [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that gives Claude surgical, token-efficient access to your local C# codebase — without ever sending raw source to the cloud.

Built on **.NET 10**, powered by **Roslyn**, backed by **Redis**.

---

## The problem it solves

Every AI coding tool eventually hits the same wall: context windows fill up, library APIs get hallucinated, and your code leaves your machine with every paste.

`dotnet-mcp-server` is a different model. Instead of dumping files into Claude's context, it runs locally and exposes your codebase as a set of structured MCP tools. Claude calls only what it needs — one class at a time, one method at a time — from the exact version of every dependency you have installed, across all your projects at once, with zero data leaving your machine.

---

## How it works

```
Claude.ai ──HTTPS──► ngrok tunnel ──SSE──► dotnet-mcp-server ──Roslyn──► Your C# source
                                                   │
                                                   ├── Redis ──► AST cache + project index
                                                   │
                                                   └── NuGet ──► Installed package reflection
```

1. **Claude calls a tool** — e.g. `analyze_c_sharp_file`.
2. **Redis is checked first.** Cache hit → response in milliseconds, zero disk I/O.
3. **On a miss**, Roslyn parses the `.cs` file and extracts structured metadata: namespaces, classes, methods with line ranges, DI constructor graphs, attributes, XML docs. The result is serialized and stored in Redis with a configurable TTL.
4. **A `FileSystemWatcher`** monitors every registered project path. On any `Create`, `Change`, `Delete`, or `Rename` event, a 300 ms debounce fires and the affected file is re-analysed and written back to Redis — so Claude always sees the code as it exists on disk right now.
5. **A background indexer** runs on startup and on a configurable schedule (default: every 60 minutes), walking every `.cs` file across all registered projects and pre-warming the cache. The first tool call is never cold.
6. **Claude gets a compact representation** — never raw source — and uses it to reason, suggest edits, check impact, or look up exact library signatures.

---

## Token efficiency — why it matters

A 500-line service class costs roughly 2 000 tokens to load raw. Ten files and you've burned your entire context budget before writing a single line of code.

This server uses a tiered reading strategy instead:

| Step | Tool | What you get | Typical token cost |
|------|------|--------------|--------------------|
| 1 | `get_project_skeleton` | Full folder tree, file sizes, NuGet packages | ~200 |
| 2 | `analyze_c_sharp_file` | Class API surface: methods, properties, DI graph | ~300–500 |
| 3 | `fetch_method_implementation` | Exact method body with line numbers | ~80–150 |
| 4 | `read_file_content` | Raw content — non-C# files or tiny scripts only | full file |

For a typical multi-service session this approach saves **10–20× tokens** compared to loading files directly. All four steps can batch: analyze 7 files or fetch 3 methods in a single tool call.

---

## Features

### Roslyn-powered C# analysis

The core of the server is a Roslyn syntax tree walker that runs on every `.cs` file. It extracts:

- Namespace and all `using` directives
- Every class with its modifiers, base type, and implemented interfaces
- Constructor parameters mapped as a dependency injection graph (what the class depends on, typed)
- Every method: full signature, return type, parameters with types, attributes, XML doc comments, and exact start/end line numbers
- Properties, fields, and constants with modifiers and types
- A `private` vs `public` mode toggle — default is public API surface only; flip `includePrivateMembers=true` to see internals during debugging

Batch mode accepts comma-separated file paths with no spaces: `Services/UserService.cs,Controllers/OrderController.cs`.

### Precise method fetching with line numbers

`fetch_method_implementation` returns the complete body of a method with every line numbered. Line numbers are exact — Claude can reference them directly in `edit_lines` patch operations. Batch mode (`Method1,Method2`) fetches multiple methods from one file in a single round trip.

### Method call graph analysis

Before touching a method signature, run `analyze_method_call_graph`. It walks every `.cs` file in the project using a Roslyn syntax walker and returns:

- Every caller: exact file path, class name, and line number
- Every outgoing call from the target method
- Paginated results (`page` / `pageSize` up to 200) for high-traffic methods

This is the difference between a safe refactor and a breaking change that only surfaces in CI.

### Live file operations with safety guarantees

Claude can create, edit, move, rename, and delete files — all guarded by:

- **Per-file semaphore locking**: concurrent writes to the same file are serialized, never dropped or interleaved
- **Atomic batch validation for moves**: all destinations are validated before any file moves; one failure aborts the entire batch
- **Path sandboxing**: every operation is resolved against the registered project root — path traversal is structurally impossible
- **Blocked path patterns**: `bin/`, `obj/`, `.git/`, `.vs/`, `node_modules/`, and any file matching password/token/secret filename patterns are permanently blocked at the service layer, not as a config flag
- **Automatic cache eviction**: every successful write invalidates the corresponding Redis keys so the next analysis call sees the updated file, not a stale snapshot
- **`edit_lines` bottom-up application**: patches are validated for overlaps, then applied in descending line-number order — original line numbers stay correct for every patch in the batch

Supported write modes for `write_file`: `create` (fails if file exists), `overwrite` (fails if file is missing), `upsert` (always succeeds).

### NuGet package exploration — reflects the actual DLL, not training data

This is the most technically distinct feature of the server. When an AI is asked "how do I use method X from package Y", the standard answer comes from training data — which may be months or years behind the version you actually have installed. This server eliminates that entirely.

Here is what happens when you call `get_namespace_summary`:

1. **Version resolution** — `NuGet.Protocol` queries NuGet.org and resolves the exact version you specify (or latest stable). No guessing.
2. **Package download** — The `.nupkg` is downloaded and unpacked into a temp directory. The `lib/` folder is inspected and the right `net*/` target framework folder is selected, with an automatic fallback chain (`net10.0` → `net9.0` → `net8.0` → `netstandard2.1` → ...) so you always get the closest match.
3. **Dependency resolution** — The package's `.nuspec` dependency groups are parsed. Every declared dependency is downloaded and its assemblies are collected. This matters because `MetadataLoadContext` needs to resolve types that cross assembly boundaries — without dependencies, reflection on generics and inherited types fails.
4. **DLL reflection via `MetadataLoadContext`** — The assembly is loaded into an isolated `MetadataLoadContext` alongside the runtime's own DLLs and all dependency assemblies. This means the binary is inspected, not executed, and there is zero risk of running static constructors or polluting the running process. Every public type is walked: methods (`MethodInfo`), properties (`PropertyInfo`), fields (`FieldInfo`), events (`EventInfo`), and constructors — all with full parameter types, visibility, and modifiers.
5. **`MetadataLoadContext` is disposed immediately** after extraction — no assembly leaks, no AppDomain side effects.
6. **Result cached in Redis for 7 days** — keyed on `packageId:version:targetFramework`. The second call for the same package is a Redis read, not a download.
7. **Temp packages are deleted** after metadata is extracted — the server doesn't accumulate `.nupkg` files on disk.

The result Claude receives is a set of valid, copy-paste-ready C# signatures reflecting the exact binary you are shipping against — not what the model thinks the API looks like.

```
search_nu_get_packages          ← confirm the exact NuGet package ID
get_nu_get_package_namespaces   ← discover which namespaces the package exposes
get_namespace_summary           ← all public types, methods, properties — from the DLL
get_method_overloads            ← expand collapsed overload groups on demand
```

### Global code search with pagination

`search_code_globally` finds classes, interfaces, methods, properties, and fields by name or keyword. Scope it to one project or pass `projectName="*"` to search across every registered project simultaneously. Results are fully paginated (`page` / `pageSize` up to 200 per page).

Practical uses:
- Security audit: `search_code_globally("*", "Authorize")` — find every authorization point across all services
- Dependency check: `search_code_globally("*", "IOrderService")` — find every consumer before renaming an interface
- Pre-refactor impact: locate all references to a class before splitting it

### Multi-project support from day one

Register as many projects as you have. Every tool accepts `projectName`. Claude can reason across your entire microservices solution in a single conversation — skeleton one service, read a method from another, edit a third — with no context switching and no copy-pasting between windows.

### Background indexing and real-time cache coherence


---

## Why this matters for .NET developers specifically

Every major AI coding tool in 2025–2026 is a general-purpose assistant designed primarily around JavaScript, Python, and TypeScript. They work on .NET, but with measurable blind spots that cost real development time. This section breaks down where those gaps are and what this server does differently.

### The hallucination problem in .NET is worse than the averages suggest

A March 2025 developer analysis on Medium documented that GitHub Copilot "tends to hallucinate about C# methods and properties that do not exist" — generated code that doesn't compile, requiring manual correction every time. A 2025–2026 study measuring AI coding tool quality put Copilot's wrong-dependency rate at roughly 15%, and a GitHub community thread titled *"What happened to Copilot? Hallucinatory, complicating, wrong, sycophantic, forgetful"* accumulated hundreds of upvotes from developers describing exactly this experience for strongly-typed languages like C#.

The core reason is structural: every LLM is frozen at its training cutoff. NuGet packages release breaking changes constantly — `System.Text.Json` changed nullable handling between 6.0 and 8.0, EF Core changed `DbContext` configuration patterns between 7.0 and 8.0, `IgnoreNullValues` was deprecated mid-lifecycle. A model trained before those changes will confidently generate code that no longer compiles against the version in your `.csproj`.

`dotnet-mcp-server` doesn't use training data for library APIs. It downloads the `.nupkg`, reflects the actual DLL binary using `MetadataLoadContext`, and returns signatures from the version you have installed. There is no training cutoff. The answer is always current to your exact dependency lock.

### Context window limits hit .NET solutions harder than small projects

GitHub Copilot's effective context cap is 64K tokens on standard plans, expanding to 128K on VS Code Insiders (confirmed by GitHub's own API — `max_prompt_tokens: 128000`). At roughly 4 tokens per line of C#, that's approximately 32,000 lines before the context window is full — which sounds like a lot until you're working across a microservices solution with multiple projects, each with their own services, repositories, controllers, and models.

More critically, even within that limit, Copilot's `#codebase` search is widely documented as poor quality. A filed VS Code issue with 23 upvotes from April 2025 described it as "quite poor and doesn't seem to include all the necessary files as context" — leading developers to manually attach files and burn through their context budget before getting a useful answer.

This server inverts that model. Claude never receives a full file unless you explicitly ask for raw content. A complete class analysis costs ~300–500 tokens. A single method body costs ~80–150 tokens. You can work across an entire 5-project solution without approaching any context limit because the server delivers only what is needed, structured, on demand.

### Your code never leaves your machine

Every cloud-based AI coding tool — Copilot, Cursor, Windsurf, Tabnine cloud — sends your source code to an external server to generate completions. GitHub Copilot's privacy documentation confirms that code snippets are transmitted to GitHub's AI servers with each request. A 2026 analysis found that 79% of organizations using AI for automated workflows have no visibility into what data those systems actually touch or where they send it.

For .NET developers working in finance, healthcare, or any regulated industry, this is not a theoretical concern. The EU AI Act entered phased enforcement in August 2025. The EU-US Data Privacy Framework collapsed in late 2025, leaving organizations without a clear legal mechanism for cross-border data transfers. A Gartner projection put 75% of organizations demanding AI solutions with strong data residency guarantees by 2026.

This server runs entirely on your machine. Claude.ai receives structured metadata — class names, method signatures, line ranges — never raw source. Your business logic, your proprietary algorithms, and your customer data never travel anywhere.

### What the alternatives actually offer — and where they stop

| | GitHub Copilot Pro | Cursor Pro | Tabnine Enterprise | **dotnet-mcp-server** |
|---|---|---|---|---|
| **Cost** | $10/month | $20/month | $59/user/month | Free, self-hosted |
| **Code leaves machine** | Yes | Yes (AWS infra) | Optional (on-prem tier) | **Never** |
| **NuGet API accuracy** | Training data only | Training data only | Training data only | **Actual DLL reflection** |
| **Multi-project support** | Via `#codebase` (unreliable) | Workspace indexing | Workspace indexing | **Native, per-tool `projectName`** |
| **C# method line numbers** | No | No | No | **Yes — exact, for patch ops** |
| **Context strategy** | File dump | File dump + embeddings | File dump + embeddings | **Surgical: metadata only** |
| **IDE lock-in** | VS Code, VS, JetBrains | VS Code only (fork) | Multi-IDE | **IDE-agnostic (Claude.ai + Claude Code)** |
| **Scales to microservices** | Context fills up | Context fills up | Context fills up | **Unlimited registered projects** |

**Cost note:** Tabnine's self-hosted on-premises deployment — the only competing option that keeps code off external servers — starts at $59/user/month. For a 5-developer team that's $295/month, or $3,540/year. `dotnet-mcp-server` is open source and free to run on any machine that can run Redis and .NET 10.

### What this does not replace

This is not an inline autocomplete tool. It does not suggest the next line as you type. It does not integrate into your IDE as an extension.

What it replaces is the *reasoning session* — when you open a chat, describe a problem, and ask Claude to understand your codebase, plan a refactor, check what breaks if you change a signature, look up how a dependency actually works, or write a feature that spans multiple files. Those sessions are where context limits, hallucinated APIs, and cloud data exposure all cause real damage. That is exactly the scope this server is built for.

Two background services run independently:

- **`CSharpAnalysisBackgroundService`**: on startup, walks all registered projects and pre-warms Redis with full Roslyn analysis and method bodies. Re-runs on a configurable schedule (default: 60 minutes). Concurrency is bounded by `IndexingConcurrency` (default: 4 parallel Roslyn parses).
- **`CSharpFileWatcherService`**: registers a `FileSystemWatcher` per project. On any `.cs` file event, debounces 300 ms (to handle the burst of events Visual Studio fires on a single save), then re-analyses only the changed file and updates Redis. Delete events evict the key. Rename events evict the old key and index the new path.

Both services coexist without blocking each other. The watcher loop and the scheduled full-pass loop run on separate tasks.

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
| Name | `dotnet-mcp-server` |
| URL | `https://<your-ngrok-id>.ngrok-free.app/sse` |

Claude discovers all tools automatically via the MCP capability negotiation protocol.

### 7b. Connect Claude Code Desktop (alternative)

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "dotnet-mcp-server": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://<your-ngrok-id>.ngrok-free.app/sse", "--transport", "sse-only"]
    }
  }
}
```

`mcp-remote` is an npm bridge that proxies SSE-based MCP servers into the stdio transport Claude Code Desktop expects. `npx -y` installs it on demand — no global install needed.

---

## All 20 MCP tools

### Code analysis

| Tool | Description |
|------|-------------|
| `analyze_c_sharp_file` | Roslyn-extracted metadata: namespaces, classes, methods, properties, DI graph, line ranges. Batch mode: comma-separated paths, no spaces. |
| `fetch_method_implementation` | Complete method body with per-line line numbers. Batch mode: comma-separated method names. |
| `read_file_content` | Raw file content. Blocked for `appsettings.json`, secrets, `.env`, and `bin/`/`obj/` paths. |
| `analyze_method_call_graph` | All callers (file, class, line) and outgoing calls for a method. Paginated — `page` / `pageSize` up to 200. |

### Project exploration

| Tool | Description |
|------|-------------|
| `get_project_skeleton` | ASCII folder tree with file sizes and NuGet package list. Pass `"*"` to list all registered projects. Supports `sinceTimestamp` for incremental diffs. |
| `search_folder_files` | Paginated file listing within a folder. Optional filename filter. Use when a folder has 50+ files. |
| `search_code_globally` | Name/keyword search across one project or all (`"*"`). Filters by member type. Paginated — `page` / `pageSize` up to 200. |

### File operations

| Tool | Description |
|------|-------------|
| `write_file` | Create or overwrite files. Modes: `create` / `overwrite` / `upsert`. Parent dirs created automatically. Batch supported. |
| `edit_lines` | Apply multiple `patch` / `insert` / `delete` / `append` operations to a file atomically. Bottom-up application. Overlap-validated. |
| `move_file` | Move or rename files. All-or-nothing batch: validation runs on every destination before any file moves. |
| `delete_file` | Delete files. Each file is independent — partial success is possible. Blocked paths enforced. |
| `create_folder` | Create one or more folders including nested paths. Idempotent. |
| `move_folder` | Move or rename a folder. Single operation only (high-impact). Evicts all Redis keys under the old path before moving. |
| `delete_folder` | Recursively delete folders. Deepest-first ordering. Non-existent folders silently skipped. |
| `get_file_info` | Metadata only — existence, line count, byte size, last modified (UTC ISO 8601). Never reads content. |

### NuGet exploration

| Tool | Description |
| `search_nu_get_packages` | Queries NuGet.org by package ID. Returns version, download count, and tags. Use the full ID (`Microsoft.EntityFrameworkCore`, not `EFCore`). |
| `get_nu_get_package_namespaces` | Downloads and unpacks the `.nupkg`, selects the correct target framework folder, and returns all exposed namespaces. |
| `get_namespace_summary` | Reflects the actual DLL via `MetadataLoadContext` — returns all public types with full method, property, field, and event signatures. Cached in Redis for 7 days. |
| `get_method_overloads` | Expands overload groups collapsed in `get_namespace_summary` into individual signatures with full parameter lists. |

### Utility

| Tool | Description |
|------|-------------|
| `get_date_time` | Current date/time in UTC, server local time, or any IANA timezone (e.g. `Asia/Kolkata`). |

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
7. analyze_c_sharp_file(...)                     verify the result
```

### Looking up a library API

```
1. search_nu_get_packages("Microsoft.EntityFrameworkCore")   confirm the package ID
2. get_nu_get_package_namespaces(packageId, version)         discover namespaces
3. get_namespace_summary(namespace, packageId, version)      get all signatures
4. get_method_overloads(...)                                 expand if needed
```

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
