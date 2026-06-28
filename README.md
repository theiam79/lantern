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

Host-provided content lives under `content/assets/` (and is shared only with the players in your live room, who own the game). It is **gitignored and never source-controlled or redistributed.** A CI check enforces that no card art or verbatim card prose lands in the repo.

## Development

Toolchain (installed on the dev VM; on `PATH` via `~/.bashrc`): **.NET 10 SDK**, **.NET Aspire CLI 13.4**, **Node LTS + pnpm**.

```bash
# build everything
dotnet build Lantern.slnx

# run the engine tests (TUnit uses Microsoft.Testing.Platform — run, don't `dotnet test`)
dotnet run --project tests/Lantern.Engine.Tests -c Release

# run the whole app via Aspire (dashboard + server resource)
aspire run

# the Svelte client on its own (dev server + HMR)
pnpm -C web dev
```

Next steps: serve the Vite build from `Lantern.Server/wwwroot` for single-origin prod hosting, and add the SignalR `GameHubV1` + room registry (see [`docs/PLAN.md`](docs/PLAN.md) §3).
