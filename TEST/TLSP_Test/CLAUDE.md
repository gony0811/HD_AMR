# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WPF desktop application targeting .NET 9.0 (Windows). Developed in JetBrains Rider.

## Build & Run

```bash
dotnet build TLSP_Test.csproj
dotnet run --project TLSP_Test.csproj
```

## Architecture

- **App.xaml / App.xaml.cs** — Application entry point and global resources
- **MainWindow.xaml / MainWindow.xaml.cs** — Primary window (XAML UI + code-behind)
- **AssemblyInfo.cs** — WPF theme assembly attributes

Standard WPF pattern: XAML defines UI layout, `.xaml.cs` files contain code-behind logic.

## Key Details

- Target framework: `net9.0-windows`
- Nullable reference types enabled
- Implicit usings enabled
- No third-party NuGet dependencies currently
