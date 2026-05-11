# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

All commands run from the solution folder (`/Users/sean/Documents/GitHub/HD_AMR/HD_AMR`), which sits one level below this file — `cd HD_AMR` from the git root before running the commands below.

- **Run the web app (dev)**: `dotnet run --project HD_AMR.Web` — defaults to the `http` profile (`http://localhost:5253`). Use `--launch-profile https` for `https://localhost:7278`.
- **Build the whole solution**: `dotnet build HD_AMR.sln`
- **Restore**: `dotnet restore HD_AMR.sln`
- **Clean**: `dotnet clean HD_AMR.sln`
- **Watch (hot reload)**: `dotnet watch --project HD_AMR.Web`

No test project exists yet. When one is added, `dotnet test` from the root will pick it up; a single test runs with `dotnet test --filter "FullyQualifiedName~MyTest"`.

The SDK is pinned by `HD_AMR/global.json` to `8.0.0` with `rollForward: latestMinor` — install .NET 8 SDK 8.0.x.

## Architecture

Two projects in `HD_AMR.sln`:

- **`HD_AMR.Web/`** — Blazor Server app (.NET 8). Uses the unified Razor Components hosting model with `InteractiveServer` render mode (`Program.cs`). Components live under `HD_AMR.Web/Components/` split into `Layout/` and `Pages/`; the root is `Components/App.razor` and routing is in `Components/Routes.razor`. Because the render mode is `InteractiveServer`, all interactivity runs on the server over a SignalR circuit — UI events round-trip and component state lives in server memory per circuit.
- **`HD_AMR/`** — empty class library intended for domain/business logic. **It is not currently referenced by `HD_AMR.Web`.** When you put code here that the web app needs, add a `<ProjectReference Include="..\HD_AMR\HD_AMR.csproj" />` to `HD_AMR.Web/HD_AMR.Web.csproj`, otherwise the web project won't see the types.
### HD_AMR project structure
- Class library
- Communication layer (e.g. MQTT client, Modbus client, RS232C)
    . AMR : ModbusTCP
-   . Cobot : ModbusTCP
-   . LS산전 I/O Module : ModbusTCP
-   . Telescopic Module : RS232C
- Data Layer 
-   . DB Context (EF Core + SQLite)
- Service Layer 
-   . 

The repo is at template-stage: no data access, no authentication, no third-party NuGet packages, no tests, no Dockerfile, no CI. Most non-trivial features will require introducing those from scratch.



## Conventions

Both `.csproj` files have `Nullable` and `ImplicitUsings` enabled — write nullable-aware C# and rely on the implicit `global using` set rather than per-file `using` directives for common BCL namespaces.

`HD_AMR/.idea/` and `HD_AMR/HD_AMR.sln.DotSettings.user` (both inside the solution folder) indicate the project is developed in JetBrains Rider; the `.idea/` directory should generally not be committed but currently is.
