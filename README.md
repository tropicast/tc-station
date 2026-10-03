# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with
[Avalonia](https://avaloniaui.net/) on .NET 10. It captures station audio
(microphone, mixer or application output), encodes it and streams it live to
an Icecast mount.

> Status: project setup (issue #2). Broadcasting features are tracked in the MVP epic, #1.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned by `global.json`)
- Windows 10+, Linux with X11 or Wayland (XWayland), or macOS 13+

## Build, test and run

```bash
dotnet build
dotnet test
dotnet run --project src/Tropicast.Station.App
```

For an optimized build, use `-c Release`. Debug builds enable Avalonia
Developer Tools (F12).

## Solution layout

| Project | Responsibility |
|---|---|
| `src/Tropicast.Station.App` | Avalonia UI (views, view models), composition root (`AppHost`) |
| `src/Tropicast.Station.Core` | Domain model and shared services; no UI or platform dependencies |
| `src/Tropicast.Station.Audio` | Audio capture abstractions and platform adapters |
| `src/Tropicast.Station.Encoding` | Encoder pipeline (bundled FFmpeg supervision) |
| `tests/Tropicast.Station.Core.Tests` | Unit tests for the class libraries |
| `tests/Tropicast.Station.App.Tests` | Headless Avalonia UI tests |

Dependencies point inward: `App` → `Audio` / `Encoding` → `Core`.

## Conventions

- **MVVM** with [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/).
  Bindings are compiled by default, so views declare `x:DataType`.
- **Dependency injection, configuration and logging** via
  `Microsoft.Extensions.Hosting`. Each library exposes an
  `AddStation<Layer>()` registration extension, and the app wires them together
  in `AppHost`.
- **Central package management**: versions live in `Directory.Packages.props`.
- **Strict builds**: nullable reference types, recommended .NET analyzers,
  code style enforced at build time (`.editorconfig`) and warnings treated as
  errors.

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`) restores, builds and tests the
solution on `windows-latest`, `ubuntu-latest` and `macos-latest` for every push
to `main` and every pull request. UI tests use Avalonia Headless, so no display
server is required.
