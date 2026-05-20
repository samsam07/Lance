# Lance — Claude Code Instructions

You are picking up a project that was planned in a prior Claude conversation. This file is your primary entry point. Read it in full before doing anything.

## Project at a glance

**Lance** is a two-component tool that orchestrates [Apollo](https://github.com/ClassicOldSong/Apollo) (a Sunshine fork) and [Moonlight](https://moonlight-stream.org/) to provide a smooth, low-latency multi-monitor remote desktop experience.

- **`lance-agent`** — runs on the **server machine** (where Apollo lives). HTTP service that manages multiple Apollo instances. Phase 1: Windows only. Phase 3: cross-platform.
- **`lance`** — runs on the **client machine** (where Moonlight lives). CLI tool that talks to the agent and spawns Moonlight processes locally. **Phase 1: Linux AND Windows.** The user moves between both as client OS. macOS in Phase 3.

The user's existing pain: FreeRDP on Linux has jitter and freezes (VAAPI/NVIDIA bridge issues, software H.264 decode fallbacks). Apollo + Moonlight has clean hardware-accelerated paths (QuickSync encode on Intel iGPU → NVDEC decode on RTX 2060 Super) but vanilla Apollo/Moonlight don't handle multi-instance multi-monitor cleanly. Lance is the orchestration layer.

## Read these files in order

1. **`CLAUDE.md`** (this file) — orientation and conventions
2. **`DECISIONS.md`** — *why* we made the choices we made. Don't re-litigate these without explicit user input.
3. **`PLAN.md`** — three-phase roadmap. **You are implementing Phase 1.** Do not implement Phase 2/3 features even if they seem easy.
4. **`SPEC.md`** — API contracts, CLI surface, JSON schemas, behavior specifications
5. **`README.md`** — user-facing setup docs

## Critical conventions

### Terminology (do not mix these up)

- **Server / Apollo side** = the machine being remoted INTO. Runs `lance-agent`. Phase 1: always Windows.
- **Client / Moonlight side** = the machine the user sits AT. Runs `lance`. Phase 1: Linux or Windows.
- **Slot** = a configured Apollo instance. Has an ID (0-based), a config file, a port, and a state.
- **Slot 0** = the template = the user's original `sunshine.conf`. A normal slot in every way EXCEPT it cannot be deallocated (that would delete the template).
- **Slots 1..N** = clones of slot 0 created by Lance, fully manageable.
- **Allocated** = config file exists for this slot. Slot 0 is always allocated.
- **Running** = Apollo process is running for this slot.
- **Template** = slot 0's config file. Lance NEVER modifies the template file. It only reads from it to create slots 1..N.
- **Monitor IDs are 1-based** (1 = first physical client monitor). **Slot IDs are 0-based** (0 = template).

The user's original framing called the Windows machine the "remote client" and the Linux machine the "host." This is opposite to standard remote-desktop terminology. We use the standard: Apollo machine = server, Moonlight machine = client. If the user reverts to their original wording, gently confirm which they mean.

### Slot assignment in `lance connect`

This caught the user and me both during planning, so spell it out clearly:

`lance connect` with N monitors → uses slots 0..(N-1). Slot 0 is the lowest-numbered slot and is part of EVERY session. There is no "reserved" slot 0 — it's just the first slot, used every time.

- 1 monitor → slot 0
- 2 monitors → slots 0, 1
- 3 monitors → slots 0, 1, 2

The user's pre-paired `sunshine.conf` (template) becomes the streaming instance for the first monitor on every connect.

### Phasing discipline

The user explicitly wants three phases. **Phase 1 is intentionally minimal.** Resist the urge to add features that "would only take 10 more lines." Examples of Phase 2/3 features that Phase 1 must NOT include:

- Windows service / systemd daemon support
- Window placement on physical monitors
- Persistent state across agent restarts
- Process adoption on agent startup (Phase 1: clean-slate kill)
- Multi-client support
- Idle timeouts
- WebSocket / live status push
- Best-effort partial failure (Phase 1 is all-or-nothing)
- Interactive prompts (Phase 1 fails cleanly on errors)
- Reconnect logic
- TLS verification (Phase 1 ignores certs)
- Installers
- Cross-platform `lance-agent` (Phase 1 server = Windows only)
- macOS client
- `lance slot` subcommands (Phase 2)
- Env var for token (Phase 2)
- Auto-detection of Apollo install path (Phase 2)

If you find yourself building one of these, stop and check PLAN.md.

### Cross-platform discipline (Phase 1)

The **client `lance` runs on Linux AND Windows** in Phase 1. Don't hardcode Linux-isms.

- File paths via `Environment.SpecialFolder` and runtime checks (`OperatingSystem.IsWindows()`, `OperatingSystem.IsLinux()`).
- Browser-open: `xdg-open` (Linux), `Process.Start` with `UseShellExecute=true` (Windows). On failure, print URL for manual open.
- Moonlight executable: name and lookup differ per OS. Config-driven.
- State file: `$XDG_RUNTIME_DIR/lance/state.json` (Linux), `%LOCALAPPDATA%\Lance\state.json` (Windows).
- Config file: `~/.config/lance/config.json` (Linux), `%APPDATA%\Lance\config.json` (Windows). Plus "beside the executable" as portable fallback.

`lance-agent` itself stays Windows-only in Phase 1. Don't waste effort cross-compiling it yet.

### Code quality bar

This is a personal tool for one user in Phase 1, but it will be productized in Phase 3. Write code as if Phase 3 is coming:

- Don't hardcode paths — everything is config-driven with sensible defaults
- Don't hardcode the user's specific setup (3 monitors, RTX 2060, etc.)
- Structured logging from the start (Serilog)
- Cancellation tokens propagated properly
- All async/await, no `.Result` or `.Wait()`
- Tests for non-trivial logic (be conservative on test coverage in Phase 1 — focus on the slot lifecycle state machine and config templating)

### AOT compilation is a hard requirement from Phase 1

Every dependency choice must be AOT-compatible. The user will publish with:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishAot=true
```

Implications:
- JSON serialization MUST use source generators (`[JsonSerializable]` context classes)
- No runtime reflection
- No `dynamic`
- No `Expression<T>` compilation
- Dependency injection: use `Microsoft.Extensions.DependencyInjection` (AOT-friendly with explicit registration)

If you add a NuGet package, verify its AOT compatibility. If unclear, ask the user. **Newtonsoft.Json is forbidden** (not AOT-compatible).

## Stack

- **.NET 10** (LTS, released November 2025)
- **ASP.NET Core Minimal APIs** for `lance-agent`'s HTTP service (HTTPS with self-signed cert)
- **System.CommandLine** (GA Feb 2025) for `lance`'s CLI parsing
- **Spectre.Console** (rendering library only, NOT Spectre.Console.Cli) for pretty output in `lance`
- **Serilog** with console + rolling file sinks for logging
- **System.Text.Json** with source generators for serialization
- **xUnit** for tests when we add them

## Repository layout

```
lance/
├── CLAUDE.md, PLAN.md, SPEC.md, DECISIONS.md, README.md
├── Lance.sln
├── src/
│   ├── Lance.Agent/                 # Server-side HTTP service (Windows in Phase 1)
│   │   ├── Lance.Agent.csproj
│   │   ├── Program.cs
│   │   ├── Configuration/           # IOptions config models
│   │   ├── Endpoints/               # Minimal API endpoint groups
│   │   ├── Slots/                   # Slot lifecycle (allocate/start/stop/deallocate)
│   │   ├── Apollo/                  # Apollo config parsing/cloning/mutation
│   │   ├── Processes/               # Apollo process management
│   │   ├── Auth/                    # Bearer token middleware
│   │   ├── Lifetime/                # Graceful shutdown hooks
│   │   └── Serialization/           # JSON source gen context
│   ├── Lance.Client/                # CLI tool ("lance"), cross-platform from Phase 1
│   │   ├── Lance.Client.csproj
│   │   ├── Program.cs
│   │   ├── Commands/                # System.CommandLine command handlers
│   │   ├── AgentClient/             # HttpClient wrapper for talking to agent
│   │   ├── Moonlight/               # Moonlight process spawn/track
│   │   ├── Display/                 # Local display enumeration (platform-aware)
│   │   ├── Platform/                # OS-specific abstractions (paths, browser-open)
│   │   ├── State/                   # Runtime state file + locking
│   │   ├── Configuration/           # Config file loading with precedence resolution
│   │   └── Rendering/               # Spectre.Console output helpers
│   └── Lance.Shared/                # Shared DTOs, JSON contexts
│       ├── Lance.Shared.csproj
│       ├── Contracts/               # Request/response records
│       └── Errors/                  # Common error types
└── tests/
    ├── Lance.Agent.Tests/
    └── Lance.Client.Tests/
```

## Working with the user

- The user has strong .NET experience (C# is their primary language)
- They are NOT familiar with Apollo/Moonlight internals beyond what's needed for this project
- They prefer concise, technical communication
- They will catch architectural inconsistencies and push back — engage with the pushback, don't capitulate just to be agreeable
- They want pretty CLI output (it's part of why we chose Spectre.Console for rendering)
- They want this to be a real tool, not a toy — but only what's needed for Phase 1
- They juggle between Linux and Windows as their client OS, so test `lance` on both

## Definition of Phase 1 done

The user can:

1. Manually install + pair Apollo once on Windows
2. Manually configure + start `lance-agent` on Windows (foreground process, no service)
3. From their client machine (Fedora or Windows), run `lance connect` and have N Moonlight windows appear, each streaming a virtual display from the Windows machine
4. Use either inline target (`lance connect 192.168.1.25 --token xxx`) or config file
5. Work productively for an hour
6. Run `lance disconnect` to clean up

Bugs and rough edges are acceptable in Phase 1 if they don't block this flow. Polish comes in Phase 2.

## First steps for this session

1. Read all the seed `.md` files
2. Create the .NET solution structure described above
3. Set up project files with the right SDK versions and package references
4. Implement the Shared contracts first (DTOs, JSON context)
5. Then the Agent's slot management core (Apollo config parsing, slot state machine including slot 0 semantics)
6. Then the Agent's HTTP endpoints + graceful-shutdown wiring
7. Then the Client's agent communication
8. Then the Client's CLI commands (with config resolution precedence)
9. Then the Moonlight process management on the client (with state file locking)

Build up vertically: get the simplest end-to-end flow working (one slot, connect, disconnect) before adding the rest.

Confirm understanding before writing significant code.
