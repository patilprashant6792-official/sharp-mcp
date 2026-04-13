# sharp-mcp

> **The AI-native C# dev assistant** — real NuGet DLL reflection, Roslyn codebase analysis, surgical file editing, and live build diagnostics. One self-hosted MCP server. Zero code leaves your machine.

Built on **.NET 10** · Powered by **Roslyn** · Backed by **Redis** · Reflected via **MetadataLoadContext**

---

## Why this exists

AI coding tools have three silent killers in .NET:

1. **Hallucinated APIs** — every LLM is frozen at its training cutoff. NuGet ships breaking changes constantly. The model confidently generates code that hasn't compiled since .NET 6.
2. **Context bloat** — a 500-line service class costs ~2,000 tokens raw. Ten files and you're done before writing a single line.
3. **Your code leaves your machine** — every cloud AI tool sends source to an external server. Every request.

`sharp-mcp` fixes all three. It runs locally, exposes your codebase as structured MCP tools, and — most importantly — **reflects your actual installed NuGet DLLs** using `MetadataLoadContext`. Not training data. Your exact pinned binary.

---

## The novel feature: real NuGet DLL reflection

When you ask any other AI tool "how do I use method X from package Y", the answer comes from training data — which may be months or years behind the version in your `.csproj`. This server eliminates that problem entirely.

For every NuGet package you explore, it:

1. **Resolves the exact version** from your `.csproj` via `NuGet.Protocol` — no guessing
2. **Downloads the `.nupkg`** and selects the right `net*/` target framework folder with an automatic fallback chain
3. **Resolves all dependencies** — downloads transitive packages so `MetadataLoadContext` can resolve cross-assembly types correctly
4. **Loads the DLL into an isolated `MetadataLoadContext`** — binary inspection only, never executed, zero risk of static constructors or process pollution
5. **Returns valid, copy-paste-ready C# signatures** from the exact binary you are shipping against
6. **Disposes the context immediately** — no assembly leaks, no AppDomain side effects
7. **Caches the result in Redis for 7 days** — keyed on `packageId:version:targetFramework`; second call is a Redis read, not a download

No training cutoff. No hallucinated overloads. No deprecated methods that "still work" in the model's memory. Your DLL. Your truth.

---

## The complete loop — all tools are interdependent

NuGet reflection is the most novel piece, but it only matters inside a complete dev loop. These 22 tools form a closed circuit — each one makes the others more powerful:

```
┌─────────────────────────────────────────────────────────────┐
│                       sharp-mcp loop                       │
│                                                             │
│  understand          explore          edit          verify  │
│  codebase    ──►    NuGet APIs  ──►  files    ──►   build   │
│     ▲                                                  │    │
│     └──────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

| Stage | Tools | What they give you |
|-------|-------|--------------------|
| **Understand** | `get_project_skeleton`, `analyze_c_sharp_file`, `fetch_method_implementation`, `read_file_content`, `search_folder_files` | Full codebase orientation without dumping raw files into context |
| **Search & impact** | `search_code_globally`, `analyze_method_call_graph` | Find anything across all projects; know every caller before you touch a signature |
| **Explore NuGet** | `get_package_namespaces` → `get_namespace_types` → `get_type_surface` → `get_type_shape` → `get_method_overloads` | Real signatures from your actual DLL — IDE-style progressive discovery |
| **Edit** | `write_file`, `edit_lines`, `move_file`, `delete_file`, `create_folder`, `move_folder`, `delete_folder` | Surgical, safe file operations with semaphore locking, path sandboxing, and atomic batch validation |
| **Verify** | `execute_dotnet_command` | `dotnet clean + build` with structured Roslyn diagnostics — catch errors before moving on |

None of these works as well alone. You explore a NuGet API, write code against the real signatures, edit the right file using line numbers from Roslyn analysis, then immediately build to verify. That's the product.

---

## How it works

```
Claude.ai ──HTTPS──► ngrok tunnel ──SSE──► sharp-mcp ──Roslyn──► Your C# source
                                                   │
                                                   ├── Redis ──► AST cache + project index
                                                   │
                                                   └── NuGet ──► MetadataLoadContext DLL reflection
```

1. **Claude calls a tool** — e.g. `analyze_c_sharp_file`.
2. **Redis is checked first.** Cache hit → response in milliseconds, zero disk I/O.
3. **On a miss**, Roslyn parses the `.cs` file and extracts structured metadata: namespaces, classes, methods with line ranges, DI constructor graphs, attributes, XML docs. Serialized and stored in Redis with a configurable TTL.
4. **A `FileSystemWatcher`** monitors every registered project path. On any `Create`, `Change`, `Delete`, or `Rename` event, a 300 ms debounce fires and the affected file is re-analysed and written back to Redis — so Claude always sees the code as it exists on disk right now.
5. **A background indexer** runs on startup and on a configurable schedule (default: every 60 minutes), walking every `.cs` file across all registered projects and pre-warming the cache. The first tool call is never cold.
6. **Claude gets a compact representation** — never raw source — and uses it to reason, suggest edits, check impact, or look up exact library signatures.

---

## Token efficiency

A 500-line service class costs roughly 2,000 tokens raw. Ten files and you've burned your entire context budget before writing a single line.

This server uses a tiered reading strategy instead:

| Step | Tool | What you get | Typical token cost |
|------|------|--------------|--------------------|
| 1 | `get_project_skeleton` | Full folder tree, file sizes, NuGet packages | ~200 |
| 2 | `analyze_c_sharp_file` | Class API surface: methods, properties, DI graph | ~300–500 |
| 3 | `fetch_method_implementation` | Exact method body with line numbers | ~80–150 |
| 4 | `read_file_content` | Raw content — non-C# files or targeted slices via line range / grep mode | varies |
| 5 | `search_folder_files` | Paginated file listing when a folder has 50+ files | ~50–100 |
| 6 | `search_code_globally` | Name/keyword search across one or all projects | ~100–200 |
| NuGet | `get_namespace_types` | Type index for a namespace — names + member count hints | ~10/type |
| NuGet | `get_type_surface` | Constructors + methods of one type | ~300 |

---

## Deep-dive: tool capabilities

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

Before touching a method signature, run `analyze_method_call_graph`. It walks every `.cs` file using a Roslyn syntax walker and returns:

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

### NuGet IntelliSense — IDE-style progressive exploration

The NuGet exploration tools mirror exactly what a developer does in an IDE — not a bulk dump of everything at once:

```
get_package_namespaces          ← "I installed OpenAI — what namespaces does it expose?"
get_namespace_types             ← "I added using OpenAI.Chat — what types exist?" (~10 tokens/type)
get_type_surface(typeName)      ← "I picked ChatClient — what can I call on it?"
get_type_shape(typeName)        ← "CompleteChat returned ChatCompletion — what's on it?"
get_method_overloads(...)       ← expand collapsed overloads on demand
```

Each step returns only what is needed at that moment. Nothing is dumped until asked for.

**Token cost comparison on `OpenAI.Responses`:**

| Approach | Tokens |
|----------|---------|
| Full namespace dump | ~6,000 |
| `get_package_namespaces` + `get_namespace_types` | ~250 |
| + `get_type_surface(ResponsesClient)` | ~550 |
| + `get_type_shape(CreateResponseOptions)` | ~800 total |

### Global code search

`search_code_globally` finds classes, interfaces, methods, properties, and fields by name or keyword. Pass `projectName="*"` to search across every registered project simultaneously. Paginated up to 200 per page.

Practical uses:
- Security audit: `search_code_globally("*", "Authorize")` — find every authorization point
- Dependency check: `search_code_globally("*", "IOrderService")` — find every consumer before renaming
- Pre-refactor impact: locate all references to a class before splitting it

### Multi-project support from day one

Register as many projects as you have. Every tool accepts `projectName`. Claude can reason across your entire microservices solution in a single conversation — skeleton one service, read a method from another, edit a third — with no context switching.

### Background indexing and real-time cache coherence

Two background services run independently:

- **`CSharpAnalysisBackgroundService`**: on startup, walks all registered projects and pre-warms Redis with full Roslyn analysis and method bodies. Re-runs on a configurable schedule (default: 60 minutes). Concurrency bounded by `IndexingConcurrency` (default: 4 parallel Roslyn parses).
- **`CSharpFileWatcherService`**: registers a `FileSystemWatcher` per project. On any `.cs` file event, debounces 300 ms, then re-analyses only the changed file and updates Redis. Delete events evict the key. Rename events evict the old key and index the new path.

Both services coexist without blocking each other.

---
---

## Why this matters for .NET developers specifically

Every major AI coding tool in 2025–2026 is a general-purpose assistant designed primarily around JavaScript, Python, and TypeScript. They work on .NET, but with measurable blind spots that cost real development time. This section breaks down where those gaps are and what this server does differently.

### The hallucination problem in .NET is worse than the averages suggest

A March 2025 developer analysis on Medium documented that GitHub Copilot "tends to hallucinate about C# methods and properties that do not exist" — generated code that doesn't compile, requiring manual correction every time. A 2025–2026 study measuring AI coding tool quality put Copilot's wrong-dependency rate at roughly 15%, and a GitHub community thread titled *"What happened to Copilot? Hallucinatory, complicating, wrong, sycophantic, forgetful"* accumulated hundreds of upvotes from developers describing exactly this experience for strongly-typed languages like C#.

The core reason is structural: every LLM is frozen at its training cutoff. NuGet packages release breaking changes constantly — `System.Text.Json` changed nullable handling between 6.0 and 8.0, EF Core changed `DbContext` configuration patterns between 7.0 and 8.0, `IgnoreNullValues` was deprecated mid-lifecycle. A model trained before those changes will confidently generate code that no longer compiles against the version in your `.csproj`.

`sharp-mcp` doesn't use training data for library APIs. It downloads the `.nupkg`, reflects the actual DLL binary using `MetadataLoadContext`, and returns signatures from the version you have installed. There is no training cutoff. The answer is always current to your exact dependency lock.

### Context window limits hit .NET solutions harder than small projects

GitHub Copilot's effective context cap is 64K tokens on standard plans, expanding to 128K on VS Code Insiders (confirmed by GitHub's own API — `max_prompt_tokens: 128000`). At roughly 4 tokens per line of C#, that's approximately 32,000 lines before the context window is full — which sounds like a lot until you're working across a microservices solution with multiple projects, each with their own services, repositories, controllers, and models.

More critically, even within that limit, Copilot's `#codebase` search is widely documented as poor quality. A filed VS Code issue with 23 upvotes from April 2025 described it as "quite poor and doesn't seem to include all the necessary files as context" — leading developers to manually attach files and burn through their context budget before getting a useful answer.

This server inverts that model. Claude never receives a full file unless you explicitly ask for raw content. A complete class analysis costs ~300–500 tokens. A single method body costs ~80–150 tokens. You can work across an entire 5-project solution without approaching any context limit because the server delivers only what is needed, structured, on demand.

### Your code never leaves your machine

Every cloud-based AI coding tool — Copilot, Cursor, Windsurf, Tabnine cloud — sends your source code to an external server to generate completions. GitHub Copilot's privacy documentation confirms that code snippets are transmitted to GitHub's AI servers with each request. A 2026 analysis found that 79% of organizations using AI for automated workflows have no visibility into what data those systems actually touch or where they send it.

For .NET developers working in finance, healthcare, or any regulated industry, this is not a theoretical concern. The EU AI Act entered phased enforcement in August 2025. The EU-US Data Privacy Framework collapsed in late 2025, leaving organizations without a clear legal mechanism for cross-border data transfers. A Gartner projection put 75% of organizations demanding AI solutions with strong data residency guarantees by 2026.

This server runs entirely on your machine. Claude.ai receives structured metadata — class names, method signatures, line ranges — never raw source. Your business logic, your proprietary algorithms, and your customer data never travel anywhere.


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
