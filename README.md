# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with
[Avalonia](https://avaloniaui.net/) on .NET 10. It captures station audio
(microphone, mixer or application output), encodes it and streams it live to
an Icecast mount.

> Status: desktop scaffold and manual connection profiles (issues #2 and #3).
> Audio capture and Go Live controls are tracked in the MVP epic, #1.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned by `global.json`)
- Windows 10+, Linux with X11 or Wayland (XWayland), or macOS 13+
- Linux credentials require **libsecret's `secret-tool`** (`libsecret-tools` on
  Debian/Ubuntu, `libsecret` on Arch) and an unlocked Secret Service keyring,
  such as GNOME Keyring. There is no plaintext fallback.

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
| `src/Tropicast.Station.Infrastructure` | JSON profiles, OS credential stores and Icecast connection testing |
| `tests/Tropicast.Station.Core.Tests` | Unit tests for the class libraries |
| `tests/Tropicast.Station.App.Tests` | Headless Avalonia UI tests |
| `tests/Tropicast.Station.Infrastructure.Tests` | Persistence, source handshake and native credential-store tests |

Dependencies point inward: `App` → `Audio` / `Encoding` / `Infrastructure` → `Core`.

## Connection profiles

Create a profile with a name, hostname/IP (no scheme or port), port, mount path
(for example `/live.mp3`), source username (`source` by default), password,
TLS selection and content type. **Save profile** writes non-secret settings
to the user's application-data directory:

- Windows: `%APPDATA%\Tropicast\Station\profiles.json`
- Linux: `$XDG_CONFIG_HOME/Tropicast/Station/profiles.json` (normally `~/.config`)
- macOS: `~/Library/Application Support/Tropicast/Station/profiles.json`

Passwords live separately in Windows Credential Manager, macOS Keychain, or
the Linux Secret Service (libsecret). Saved passwords are never loaded into
the editor; leave the password field blank to retain the existing password.
Delete removes the selected profile and its credential. A failed settings
write attempts to restore the previous credential.

Use TLS for non-local connections: with TLS disabled, HTTP Basic source
credentials and audio travel unencrypted. TLS certificates must be trusted
and match the host; the app does not bypass certificate checks.

**Test connection** uses the current editor values (without saving them).
It performs an authenticated Icecast `PUT` with `Expect: 100-continue`, sends
no audio, and immediately closes the connection. This briefly reserves a
free mount; a successful test is only a point-in-time check, not a reservation
for a later broadcast. Authentication failure (401), mount in use (409),
publishing denied (403), network failure and TLS errors are reported separately.
Do not use an Icecast admin or shared production source password.

Future broadcasting code must resolve a saved profile through
`IBroadcastTargetProvider`. `ManualBroadcastTargetProvider` rejects invalid
profiles and missing credentials before returning an ephemeral target; a
future API-backed provider can replace it without changing the capture pipeline.

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

CI also exercises the real OS credential stores using unique test entries
which are deleted afterwards (an isolated keyring/keychain on Linux/macOS).
Locally, those integration tests are opt-in:

```bash
TC_TEST_OS_SECRETS=1 dotnet test
```

To run the optional real Icecast handshake test, start a local Icecast instance
configured with source username `source` and password `tc-test-source`, then
set `TC_TEST_ICECAST_PORT` to its port when running `dotnet test`. It uses a
unique temporary mount and tests accepted credentials, wrong credentials,
an occupied mount and release after disconnect.
