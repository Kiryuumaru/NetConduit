# NetConduit Modular Restructure Plan

Breaking rename + package split of NetConduit into three tiers: **Core**, **Transits**, **Transports**.

Project policy: pre-release, no production users, no backward-compatibility constraint. Old NuGet IDs and namespaces will be retired.

---

## Audit Findings

### Current Layout

**Core (`src/NetConduit/`):**
- Pure core: `StreamMultiplexer.cs`, `StreamMultiplexerExtensions.cs`, `StreamPair.cs`, plus folders `Constants/`, `Enums/`, `Events/`, `Exceptions/`, `Interfaces/`, `Internal/`, `Models/`.
- Co-located transits (to be extracted): `Transits/DeltaTransit.cs`, `Transits/DuplexStreamTransit.cs`, `Transits/MessageTransit.cs`, `Transits/StreamTransit.cs`, `Transits/TransitExtensions.cs`.
- `InternalsVisibleTo NetConduit.UnitTests`.

**Transports (`src/NetConduit.{Tcp,Udp,Quic,Ipc,WebSocket}/`):**
- Each depends only on core `NetConduit`. No cross-transport coupling.
- Each uses its own namespace (`NetConduit.Tcp`, `NetConduit.Udp`, `NetConduit.Quic`, `NetConduit.Ipc`, `NetConduit.WebSocket`).
- Contains transport multiplexer + transport-specific helpers (`ReliableUdpStream`, `ReliableUdpOptions`, `WebSocketStream`, `WebSocketMuxListener`).

**Transit dependencies (verified):**
- `StreamTransit`, `DuplexStreamTransit`, `MessageTransit`: depend only on `NetConduit.Events` + `NetConduit.Interfaces` (public surface). Clean split.
- `DeltaTransit`: imports `NetConduit.Internal` but the `using` is vestigial (no `Internal.`-qualified references). Clean split — no `InternalsVisibleTo` needed.
- `TransitExtensions`: depends on `NetConduit.Interfaces` + `NetConduit.Models` (public). References all four transit types — must be split per-transit package.

**Tests:**
- `tests/NetConduit.UnitTests/` mixes core + transit tests: `TransitTests.cs`, `DeltaTransitEdgeTests.cs`, `TransitEagernessTests.cs`, transit portions of `ApiMisuseTests.cs`, `StreamFactoryTests.cs`. Namespace `NetConduit.UnitTests`.
- Transport integration tests: one project per transport, already isolated.

**Samples (`samples/NetConduit.Samples.*`):**
- 7 samples. No explicit `namespace` declaration in code (top-level statements). References mix of `NetConduit.Tcp` and `NetConduit.WebSocket`.

**Build / CI (`build/Build.cs`):**
- `DeploymentApps[]` enumerates each library (`AppId`, `ProjectName`, `ProjectTestName`). Drives test/build/publish matrices. Must be regenerated.
- Conditional `UseLocalNetConduit` flag controls dev (ProjectReference) vs CI (PackageReference) wiring per transport csproj.

**Docs (`docs/`):**
- `docs/transits/{delta,duplex-stream,message,stream,index}.md`
- `docs/transports/{tcp,udp,quic,ipc,websocket,index}.md`
- `docs/index.md`, `docs/getting-started.md`, `docs/benchmarks.md`, `docs/concepts/`, `docs/api/`, `docs/samples/`.

**Benchmarks:**
- Live: `benchmarks/docker/netconduit-comparison/NetConduit.ComparisonBench.csproj` references core only.
- `benchmarks/NetConduit.Benchmarks/` has no `.csproj` — only artifact folders (cruft).

**Old/historic:** `old/v1/`, `old/v2/` — untouched.

---

## Tier 1 — Core (unchanged identity)

| Item | Value |
|------|-------|
| NuGet ID | `NetConduit` |
| Folder | `src/NetConduit/` |
| Root namespace | `NetConduit` (+ `.Constants`, `.Enums`, `.Events`, `.Exceptions`, `.Interfaces`, `.Internal`, `.Models`) |
| Action | Remove `Transits/` folder — those files move to Tier 2 packages |
| InternalsVisibleTo | Keep `NetConduit.UnitTests` only |

---

## Tier 2 — Transits (new packages, extracted from core)

Each transit becomes its own package depending **only on `NetConduit`** (Core). No transit references another transit. `TransitExtensions.cs` is split — each package exposes its own extensions class on `IStreamMultiplexer`.

| New Package / Project | Root Namespace | Source Files (moved from core) |
|----------------------|----------------|--------------------------------|
| `NetConduit.Transit.Stream` | `NetConduit.Transit.Stream` | `StreamTransit.cs` + Stream extensions split from `TransitExtensions.cs` |
| `NetConduit.Transit.DuplexStream` | `NetConduit.Transit.DuplexStream` | `DuplexStreamTransit.cs` + Duplex extensions |
| `NetConduit.Transit.Message` | `NetConduit.Transit.Message` | `MessageTransit.cs` + Message extensions |
| `NetConduit.Transit.DeltaMessage` | `NetConduit.Transit.DeltaMessage` | `DeltaTransit.cs` (renamed `DeltaMessageTransit.cs`) + Delta extensions |

Class renames:
- `DeltaTransit<T>` → `DeltaMessageTransit<T>` (aligns class to package name)
- `StreamTransit`, `DuplexStreamTransit`, `MessageTransit`: class names unchanged
- `TransitExtensions` split into four classes — `StreamTransitExtensions`, `DuplexStreamTransitExtensions`, `MessageTransitExtensions`, `DeltaMessageTransitExtensions` — each `static`, each in its own package namespace, all extending `IStreamMultiplexer`

Csproj template per transit:
- `TargetFrameworks`: `net8.0;net9.0;net10.0`
- `IsAotCompatible=true`, `EnableTrimAnalyzer=true`, `GenerateDocumentationFile=true`
- Same `UseLocalNetConduit` conditional pattern as transports (ProjectReference for Debug, PackageReference for Release)
- Package metadata mirroring transport pattern (Authors, Tags, etc.)

---

## Tier 3 — Transports (rename only)

| Old | New | Old Namespace | New Namespace |
|-----|-----|---------------|---------------|
| `src/NetConduit.Tcp/` | `src/NetConduit.Transport.Tcp/` | `NetConduit.Tcp` | `NetConduit.Transport.Tcp` |
| `src/NetConduit.Udp/` | `src/NetConduit.Transport.Udp/` | `NetConduit.Udp` | `NetConduit.Transport.Udp` |
| `src/NetConduit.Quic/` | `src/NetConduit.Transport.Quic/` | `NetConduit.Quic` | `NetConduit.Transport.Quic` |
| `src/NetConduit.Ipc/` | `src/NetConduit.Transport.Ipc/` | `NetConduit.Ipc` | `NetConduit.Transport.Ipc` |
| `src/NetConduit.WebSocket/` | `src/NetConduit.Transport.WebSocket/` | `NetConduit.WebSocket` | `NetConduit.Transport.WebSocket` |

NuGet `PackageId` matches each new project name. Existing class names (`TcpMultiplexer`, `UdpMultiplexer`, `QuicMultiplexer`, `IpcMultiplexer`, `WebSocketMultiplexer`, `WebSocketStream`, `WebSocketMuxListener`, `ReliableUdpStream`, `ReliableUdpOptions`) unchanged.

---

## Tests

| Old | New |
|-----|-----|
| `tests/NetConduit.UnitTests/` | Split: core tests stay; transit tests move to per-package test projects |
| `tests/NetConduit.Tcp.IntegrationTests/` | `tests/NetConduit.Transport.Tcp.IntegrationTests/` |
| `tests/NetConduit.Udp.IntegrationTests/` | `tests/NetConduit.Transport.Udp.IntegrationTests/` |
| `tests/NetConduit.Quic.IntegrationTests/` | `tests/NetConduit.Transport.Quic.IntegrationTests/` |
| `tests/NetConduit.Ipc.IntegrationTests/` | `tests/NetConduit.Transport.Ipc.IntegrationTests/` |
| `tests/NetConduit.WebSocket.IntegrationTests/` | `tests/NetConduit.Transport.WebSocket.IntegrationTests/` |
| *(new)* | `tests/NetConduit.Transit.Stream.UnitTests/` |
| *(new)* | `tests/NetConduit.Transit.DuplexStream.UnitTests/` |
| *(new)* | `tests/NetConduit.Transit.Message.UnitTests/` |
| *(new)* | `tests/NetConduit.Transit.DeltaMessage.UnitTests/` |

Transit test split from `NetConduit.UnitTests`:
- `TransitTests.cs` → split by transit type into four files across four test projects
- `DeltaTransitEdgeTests.cs` → `NetConduit.Transit.DeltaMessage.UnitTests`
- `TransitEagernessTests.cs` → split per transit
- Transit portions of `ApiMisuseTests.cs` → split per transit; core misuse remains in `NetConduit.UnitTests`
- `StreamFactoryTests.cs` → `NetConduit.Transit.Stream.UnitTests` (or whichever transit it primarily covers)

Each new test csproj: public-API-only by default; no `InternalsVisibleTo` unless proven needed.

---

## Samples (rename + simpler names + dedicated namespaces)

| Old Folder | New Folder | New Project / RootNamespace |
|-----------|------------|------------------------------|
| `samples/NetConduit.Samples.TcpTunnel/` | `samples/SimpleTcpTunnel/` | `SimpleTcpTunnel` |
| `samples/NetConduit.Samples.Pong/` | `samples/PongGame/` | `PongGame` |
| `samples/NetConduit.Samples.GroupChat/` | `samples/GroupChatSample/` | `GroupChatSample` |
| `samples/NetConduit.Samples.FileTransfer/` | `samples/FileTransferSample/` | `FileTransferSample` |
| `samples/NetConduit.Samples.RemoteShell/` | `samples/RemoteShellSample/` | `RemoteShellSample` |
| `samples/NetConduit.Samples.RpcFramework/` | `samples/RpcFrameworkSample/` | `RpcFrameworkSample` |
| `samples/NetConduit.Samples.Scoreboard/` | `samples/ScoreboardSample/` | `ScoreboardSample` |

Each sample csproj sets `<RootNamespace>` explicitly to the new name and updates `ProjectReference` paths to the new transport folder names. Samples may also reference appropriate transit packages (e.g., `NetConduit.Transit.Message`).

---

## Solution & Build Wiring

`NetConduit.slnx` rewrite:
- `src/` folder: core + 4 transits + 5 transports = 10 projects
- `tests/` folder: 1 core + 4 transit + 5 transport integration = 10 projects
- `samples/` folder: 7 renamed projects

`build/Build.cs` `DeploymentApps` updated to 10 entries:

| AppId | ProjectName | ProjectTestName |
|-------|-------------|------------------|
| `net_conduit` | `NetConduit` | `NetConduit.UnitTests` |
| `net_conduit_transit_stream` | `NetConduit.Transit.Stream` | `NetConduit.Transit.Stream.UnitTests` |
| `net_conduit_transit_duplex_stream` | `NetConduit.Transit.DuplexStream` | `NetConduit.Transit.DuplexStream.UnitTests` |
| `net_conduit_transit_message` | `NetConduit.Transit.Message` | `NetConduit.Transit.Message.UnitTests` |
| `net_conduit_transit_delta_message` | `NetConduit.Transit.DeltaMessage` | `NetConduit.Transit.DeltaMessage.UnitTests` |
| `net_conduit_transport_tcp` | `NetConduit.Transport.Tcp` | `NetConduit.Transport.Tcp.IntegrationTests` |
| `net_conduit_transport_udp` | `NetConduit.Transport.Udp` | `NetConduit.Transport.Udp.IntegrationTests` |
| `net_conduit_transport_quic` | `NetConduit.Transport.Quic` | `NetConduit.Transport.Quic.IntegrationTests` |
| `net_conduit_transport_ipc` | `NetConduit.Transport.Ipc` | `NetConduit.Transport.Ipc.IntegrationTests` |
| `net_conduit_transport_websocket` | `NetConduit.Transport.WebSocket` | `NetConduit.Transport.WebSocket.IntegrationTests` |

The existing memory-leak unit-test categorization logic (`Category=HighMemory`, `HighMemory.HappyPath`) stays — applies only to `NetConduit.UnitTests`.

---

## Docs

| Old | New |
|-----|-----|
| `docs/transits/delta.md` | `docs/transits/delta-message.md` |
| `docs/transits/stream.md` | (unchanged path) — update namespace examples |
| `docs/transits/duplex-stream.md` | (unchanged path) — update namespace examples |
| `docs/transits/message.md` | (unchanged path) — update namespace examples |
| `docs/transits/index.md` | Update package IDs + install commands |
| `docs/transports/{tcp,udp,quic,ipc,websocket}.md` | Update package IDs + namespaces |
| `docs/transports/index.md` | Update package IDs |
| `docs/index.md`, `docs/getting-started.md`, `docs/benchmarks.md`, `README.md` | Update all package IDs, namespaces, install commands |
| `docs/samples/*` | Update sample names if referenced |

---

## Benchmarks

- `benchmarks/docker/netconduit-comparison/Program.cs` + csproj: only references core. Verify after rename, no changes expected.
- Delete `BenchmarkDotNet.Artifacts/results/` (regenerated on next run) and the empty `benchmarks/NetConduit.Benchmarks/` folder.

---

## Execution Order

Each step builds clean before moving to the next.

1. **Rename transports** (folders, csproj, namespaces) — lowest blast radius.
2. **Update transport integration test projects** + their `using` lines.
3. **Update samples** — rename folders/csproj/RootNamespace + update transport `ProjectReference` paths.
4. **Extract transits** — create 4 new transit projects, move source files, split `TransitExtensions` into 4 per-package extension classes, rename `DeltaTransit` → `DeltaMessageTransit`. Remove `Transits/` from core.
5. **Create 4 transit unit-test projects**, move transit tests out of `NetConduit.UnitTests`.
6. **Update `NetConduit.slnx`** with all renames and additions.
7. **Update `build/Build.cs`** `DeploymentApps` to the 10-entry matrix.
8. **Update docs + README** package IDs, namespaces, install commands.
9. **Build + test verification**: `dotnet build` (0 warnings), `dotnet test` (100% pass).
10. **Generate CI workflow**: `.\build.ps1 githubworkflow`.

---

## Risks & Notes

- **Public API break**: All consumer `using NetConduit.Tcp;`, `using NetConduit.Transits;`, etc., will break. Permitted by project policy.
- **NuGet ID changes**: New package listings on nuget.org; old IDs (`NetConduit.Tcp`, etc.) orphaned.
- **`InternalsVisibleTo`**: Transit extraction verified to need only public APIs. The vestigial `using NetConduit.Internal` in `DeltaTransit` will be removed. No new internals leakage.
- **Test file splits**: Files like `ApiMisuseTests.cs` and `TransitTests.cs` cover multiple transits. They will be split rather than duplicated.
- **Class rename `DeltaTransit` → `DeltaMessageTransit`** is the only class-level rename; all other transit class names already match their package suffix.
