# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with
[Avalonia](https://avaloniaui.net/) on .NET 10. It captures station audio
(microphone, mixer or application output), encodes it and streams it live to
an Icecast mount.

> Status: desktop scaffold, manual connection profiles, and shared audio
> capture/device-picker pipeline (issues #2–#4). Native adapters (#5–#7),
> encoding, meters and Go Live controls remain in the MVP epic, #1.

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
| `tests/Tropicast.Station.Audio.Tests` | PCM conversion, synthetic capture and hot-plug lifecycle tests |
| `tests/Tropicast.Station.Infrastructure.Tests` | Persistence, source handshake and native credential-store tests |

Dependencies point inward: `App` → `Audio` / `Encoding` / `Infrastructure` → `Core`.

## Audio sources and preview

The **Audio source** tab groups available devices into **Microphones / inputs**
and **Application / system output**. Choose one device, the encoder sample rate
(44.1 or 48 kHz) and mono/stereo, then **Start preview**. This consumes captured
PCM through the shared conversion pipeline; it does not play audio, encode it,
or publish to Icecast. Levels and broadcasting controls are separate MVP issues.

Native capture adapters are not installed yet. The normal app explicitly says
so and lists no hardware devices. Run the synthetic adapter for demos:

```bash
dotnet run --project src/Tropicast.Station.App -- --demo-audio
```

Demo mode supplies a 440 Hz, 44.1 kHz mono signed-16 input and a 660 Hz, 48 kHz
stereo float32 loopback source, both clearly labelled **Demo**. These are generated
tones, not recordings of microphones or application audio.

### Adapter contract and pipeline

`IAudioCaptureProvider` exposes enumeration, `DevicesChanged` notifications and
`StartAsync`. An `AudioDevice` carries a stable ID, display name, kind, default
flag and native `AudioFormat`. `IAudioCaptureSession` delivers interleaved
little-endian signed16 or float32 PCM through a single-consumer async stream.
Each `PcmFrame` owns its buffer; adapters must not reuse that memory. Stop,
disposal and reader cancellation must unblock capture and be safe to call
concurrently/repeatedly. Bounded native queues must report overruns, not silently
discard audio. Device loss throws `IOException`.

`AudioCaptureService` serializes lifecycle/device refreshes, cancels and disposes
capture on removal or native-format changes, and emits a visible error without
switching to another device. Idle hot-plug/default-device changes refresh the
picker automatically. The selected device is preserved by ID on renames.
Adapters for WASAPI, PipeWire/PulseAudio and Core Audio will replace the default
provider via DI; use `TryAdd` registration so an explicitly registered adapter
is not overwritten.

`PcmConverter` produces float32 mono/stereo PCM for the future encoder using
a streaming windowed-sinc low-pass resampler with 32 input-frame lookahead
(about 0.73 ms at 44.1 kHz). Mono is duplicated to stereo; stereo is averaged
to mono. With more channels, mono averages all channels and stereo averages
alternating channel indices. This is deliberately **not** a speaker-layout-aware
surround downmix. No gain/limiting is applied. Conversion state carries across
chunks; `Flush()` drains a finite source, while device-loss/stop discards the
tail. Invalid PCM alignment, non-finite float samples or midstream format
changes produce explicit errors.

`AudioCaptureService.FrameAvailable` is the future encoder integration point:
frames are already in the requested format. Subscribers run on the capture
thread and must neither block nor throw. Marshal UI work to the UI dispatcher;
keep any encoder handoff bounded and report overloads. `ToneAudioCaptureProvider`
also exposes `SetDevices` for deterministic hot-plug tests and demos.

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
