# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

All commands run from the solution folder (`/Users/sean/Documents/GitHub/HD_AMR/HD.AMR`), which sits one level below this file — `cd HD.AMR` from the git root before running the commands below.

- **Run the web app (dev)**: `dotnet run --project HD.AMR.Web` — defaults to the `http` profile (`http://localhost:5253`). Use `--launch-profile https` for `https://localhost:7278`.
- **Build the whole solution**: `dotnet build HD.AMR.sln`
- **Restore**: `dotnet restore HD.AMR.sln`
- **Clean**: `dotnet clean HD.AMR.sln`
- **Watch (hot reload)**: `dotnet watch --project HD.AMR.Web`

Tests live in `HD.AMR.Tests` (xUnit). Run them with `dotnet test HD.AMR.sln`; a single test runs with `dotnet test --filter "FullyQualifiedName~MyTest"`.

The SDK is pinned by `HD.AMR/global.json` to `8.0.0` with `rollForward: latestMinor` — install .NET 8 SDK 8.0.x.

### Intel RealSense SDK (D435/D435i depth camera)

The depth camera is an Intel RealSense D435/D435i, integrated via the `Intel.RealSenseWithNativeDll` NuGet package (official `Intel.RealSense` C# wrapper + native `realsense2.dll` for win-x64, librealsense 2.51.1). No manual SDK install is needed — the native DLL is copied to the build output automatically. The package is referenced by both `HD.AMR.App` and `HD.AMR.Web` (the native DLL flows via a `build/` .targets file, which is not transitive through ProjectReference). Target platform is **Windows x64 only**; the camera wrapper lives in `HD.AMR/HD.AMR.App/Communication/RealSenseClient.cs` and requires a USB 3.0 port (USB 2.x falls back to reduced SDK-default modes with a logged warning).

## Architecture

Three projects in `HD.AMR.sln`:

- **`HD.AMR.Web/`** — Blazor Server app (.NET 8). Uses the unified Razor Components hosting model with `InteractiveServer` render mode (`Program.cs`). Components live under `HD.AMR.Web/Components/` split into `Layout/` and `Pages/`; the root is `Components/App.razor` and routing is in `Components/Routes.razor`. Because the render mode is `InteractiveServer`, all interactivity runs on the server over a SignalR circuit — UI events round-trip and component state lives in server memory per circuit.
- **`HD.AMR.App/`** — class library holding the domain/business logic (communication clients, data layer, services). Referenced by `HD.AMR.Web` and `HD.AMR.Tests` via `<ProjectReference Include="..\HD.AMR.App\HD.AMR.App.csproj" />`.
- **`HD.AMR.Tests/`** — xUnit test project referencing `HD.AMR.App`.
### HD.AMR.App project structure
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

`HD.AMR/.idea/` and `HD.AMR/HD.AMR.sln.DotSettings.user` (both inside the solution folder) indicate the project is developed in JetBrains Rider; both are gitignored.
