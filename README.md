# Lantern

A personal-use, self-hosted web app to play **Kingdom Death: Monster** remotely with friends. Lantern is a **play dashboard / game engine** — it runs the campaign turn flow and the showdown loop and syncs it live to all players. It is **not** a survivor/settlement bookkeeping tool: keep managing sheets in your existing tracker (e.g. Scribe / Black Ledger).

Guiding principle: **preserve the spirit of physical play.** Lantern is a shared *state tracker*, not a simulator — players roll their own physical dice and enter results; Lantern shows the math ("hits on X+"), runs the digital AI / Hit-Location decks, and keeps everyone in sync.

> Full design: [`docs/PLAN.md`](docs/PLAN.md).

## Stack

- **Backend:** ASP.NET Core (.NET 10) + SignalR (authoritative real-time server), orchestrated with .NET Aspire.
- **Engine:** `Lantern.Engine` — a pure, dependency-free, unit-tested C# domain library.
- **Frontend:** Vite + Svelte SPA (`@microsoft/signalr` client).
- **Persistence:** SQLite (state between sessions).

## Layout

```
src/Lantern.Engine      pure domain: phase/showdown FSM, decks, RNG (no deps)
src/Lantern.Contracts   versioned wire contract: intents + state deltas
src/Lantern.Server      ASP.NET Core: SignalR hub, minimal API, serves the SPA (feature folders)
tests/Lantern.Engine.Tests   TUnit
web/                    Vite + Svelte SPA (built into the server's wwwroot)
content/packs/          shippable "Scribe-scope" mechanical data (names, stats, deck-build)
content/assets/         HOST-PROVIDED card images — gitignored, never redistributed
tools/                  utilities (e.g. card ingest / OCR) — off the app's critical path
```

## Bring Your Own Cards (content policy)

Lantern ships **no Kingdom Death gameplay artifacts you'd need to play without owning the game.** Specifically:

- **Shippable ("Scribe-scope"):** card *names*, deck-build composition, and mechanical *numbers* (gear stats, monster stat lines, hit numbers) — the same class of data Scribe already exposes.
- **You must provide (never shipped):** the **AI (monster) decks** and **Hit-Location decks** — the only core-gameplay cards Scribe lacks — plus any **card images/art** and verbatim **effect text**.

Host-provided content lives under `content/local/` and `content/assets/` (and is shared only with the players in your live room, who own the game). It is **gitignored and never source-controlled or redistributed** — `.gitignore` blocks raster image formats repo-wide (with an allowlist for the app's own UI assets). A pre-commit / CI guard that also rejects verbatim card prose is a recommended follow-up.

## Development

Toolchain (installed on the dev VM; on `PATH` via `~/.bashrc`): **.NET 10 SDK**, **.NET Aspire CLI 13.4**, **Node LTS + pnpm**.

```bash
dotnet tool restore        # restore pinned tools (CSharpier)
dotnet build Lantern.slnx

# Run the whole app. Aspire orchestrates BOTH resources — the ASP.NET server AND the Vite
# frontend (AppHost: AddViteApp("web", ...).WithPnpm()) — plus the dashboard. Open the "web"
# resource URL from the dashboard. In dev the Vite resource serves the SPA (HMR) and proxies
# /hub + /api to the server (Aspire injects VITE_SERVER_URL); the client uses relative /hub/v1.
aspire run

# engine tests (TUnit uses Microsoft.Testing.Platform — run, don't `dotnet test`)
dotnet run --project tests/Lantern.Engine.Tests -c Release
```

Running a piece standalone is rarely needed but possible: `dotnet run --project src/Lantern.Server`
and `pnpm -C web dev`.

## Code style

Microsoft / Roslyn conventions (latest C# features). Formatting is owned by **CSharpier**
(a pinned local tool); language style + naming live in `.editorconfig` and run in-build
(`EnforceCodeStyleInBuild`).

```bash
dotnet tool restore        # once, to install the pinned CSharpier
dotnet csharpier format .  # auto-format all C#
dotnet csharpier check .   # verify formatting (CI-style)
```

A pre-commit/CI hook running `dotnet csharpier check .` + `dotnet build` is a recommended
follow-up (needs the GitHub token's Workflows scope to commit `.github/workflows/`).

## Test hosting (exe.dev)

This VM can expose the app over the exe.dev HTTPS proxy for a real remote session. Run the
server in **Production** — where it's the single origin (serves the built SPA from `wwwroot`
*and* the SignalR hub) — then map the port. (Dev `aspire run` is for local work; for a shared
test you want the single-origin Production server, not the Vite dev server + dashboard.)

```bash
pnpm -C web build                       # build SPA -> Lantern.Server/wwwroot

LANTERN_HOST_PASSWORD='<pick-a-secret>' \
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS='http://0.0.0.0:8080' \
  dotnet run --no-launch-profile --project src/Lantern.Server -c Release

# Expose port 8080 at https://slopdom-slop.exe.xyz/ (PRIVATE by default — exe.dev login required):
ssh exe.dev share port slopdom-slop 8080
# Make it reachable by friends without exe.dev accounts (revert with set-private):
ssh exe.dev share set-public slopdom-slop
```

- **Bind `0.0.0.0`** so the proxy can reach the service.
- The proxy terminates TLS and forwards `X-Forwarded-*`, which the server honors
  (`UseForwardedHeaders`), so **SignalR runs over WSS at the same origin** — no CORS, relative
  `/hub/v1`. Room creation is gated by `LANTERN_HOST_PASSWORD`; players join with the room code.
- Ports **3000–9999** are also directly reachable at `https://slopdom-slop.exe.xyz:PORT/`
  without `share port`.
- A persistent host should set a stable `Lantern:DbPath` (the SQLite file) on a kept volume.
