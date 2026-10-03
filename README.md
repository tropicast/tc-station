# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with
[Avalonia](https://avaloniaui.net/) on .NET 10. It captures station audio
(microphone, mixer or application output), encodes it and streams it live to
an Icecast mount.

> Status: desktop scaffold, manual connection profiles, and shared audio
> capture/device-picker pipeline, Windows WASAPI and Linux PulseAudio/PipeWire adapters (issues #2–#6). The macOS adapter (#7),
> encoding, meters and Go Live controls remain in the MVP epic, #1.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned by `global.json`)
- Windows 10+, Linux with X11 or Wayland (XWayland), or macOS 13+
- Linux credentials require **libsecret's `secret-tool`** (`libsecret-tools` on
  Debian/Ubuntu, `libsecret` on Arch) and an unlocked Secret Service keyring,
  such as GNOME Keyring. There is no plaintext fallback.
- Linux audio requires **`pactl` and `parec`** (`pulseaudio-utils` on
  Debian/Ubuntu, `libpulse` on Arch), plus a running PulseAudio server or
  PipeWire with **`pipewire-pulse`**. Use a recent `pactl` supporting JSON output
  (PulseAudio 15+). No root access is required for capture.

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
| `src/Tropicast.Station.Audio.Windows` | WASAPI shared-mode input and render-endpoint loopback via NAudio |
| `src/Tropicast.Station.Audio.Linux` | PulseAudio/pipewire-pulse sources and sink monitors via libpulse clients |
| `src/Tropicast.Station.Encoding` | Encoder pipeline (bundled FFmpeg supervision) |
| `src/Tropicast.Station.Infrastructure` | JSON profiles, OS credential stores and Icecast connection testing |
| `tests/Tropicast.Station.Core.Tests` | Unit tests for the class libraries |
| `tests/Tropicast.Station.App.Tests` | Headless Avalonia UI tests |
| `tests/Tropicast.Station.Audio.Tests` | PCM conversion, synthetic capture and hot-plug lifecycle tests |
| `tests/Tropicast.Station.Audio.Windows.Tests` | Windows adapter queue/lifecycle tests and opt-in hardware qualification |
| `tests/Tropicast.Station.Audio.Linux.Tests` | Linux parser/session tests and opt-in synthetic native capture integration |
| `tests/Tropicast.Station.Infrastructure.Tests` | Persistence, source handshake and native credential-store tests |

Dependencies point inward: `App` → `Audio` / `Encoding` / `Infrastructure` → `Core`.

## Audio sources and preview

The **Audio source** tab groups available devices into **Microphones / inputs**
and **Application / system output**. Choose one device, the encoder sample rate
(44.1 or 48 kHz) and mono/stereo, then **Start preview**. This consumes captured
PCM through the shared conversion pipeline; it does not play audio, encode it,
or publish to Icecast. Levels and broadcasting controls are separate MVP issues.

On Windows, the normal app lists active WASAPI inputs and playback devices
(loopback sources). On Linux, it lists PulseAudio/PipeWire inputs and sink
monitor sources. The macOS adapter is not installed yet: the app explicitly
says so and lists no hardware devices on macOS.
Run the synthetic adapter on any OS for demos:

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
The Windows WASAPI and Linux PulseAudio adapters are registered before the
shared audio services; the Core Audio adapter will follow the same pattern.
Use `TryAdd` registration so an explicitly registered adapter is not overwritten.

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

### Windows WASAPI capture

The app automatically selects `WindowsAudioCaptureProvider` on Windows unless
`--demo-audio` is passed. Active capture endpoints (including USB mixer/interface
inputs) and render endpoints are listed separately; the Windows **Multimedia**
default is labelled. Capture targets an explicit endpoint ID and never silently
follows a different default. Add/remove/state/default/property notifications
trigger refresh through `IMMNotificationClient`. Removing an active endpoint
stops capture and shows guidance; replugging it refreshes the picker.

Render-endpoint loopback captures **all audio played through that endpoint**,
not a single application/process. Choose the endpoint used by your audio player.
WASAPI may deliver no packets while a render endpoint is idle; this adapter
does not invent silence or play a keepalive tone. Continuous loopback audio
requires an actively playing source.

Capture uses WASAPI **shared mode**, with 100 ms native buffers and worker-thread
initialization (no captured UI synchronization context). PCM16 and float32 mix
formats are preserved. PCM24/PCM32 mix formats request float32 using WASAPI's
shared-mode conversion, preserving the endpoint's sample rate/channel count.
The device format in the picker therefore describes the PCM supplied by the
adapter, not necessarily the physical interface bit depth. The common
`PcmConverter` handles conversion to the chosen encoder format. Non-PCM formats
and rates/channel counts outside the shared contract are rejected explicitly.

NAudio callback buffers are copied before reuse. A bounded queue (256 packets,
maximum two seconds of PCM by byte budget) fails explicitly on overruns rather
than silently dropping data. Capture errors and device invalidation close the
stream; cancellation/stop unblocks readers, and disposal joins the native
capture thread on a worker before releasing its endpoint.

If Windows denies microphone access, the app displays the settings path:
**Settings → Privacy & security → Microphone** (Windows 10:
**Settings → Privacy → Microphone**). Enable **Microphone access** and
**Let desktop apps access your microphone**. The adapter checks explicit
Windows microphone-consent denials as a preflight hint, then treats WASAPI
access-denied errors as authoritative. Loopback is not blocked by a
microphone-only denial. Exclusive-device use, stopped Windows Audio service,
unsupported format and invalidation have separate guidance.

### Windows hardware qualification (not exercised by hosted CI)

Hosted CI runs the adapter with an injected fake native backend: it cannot
verify USB interfaces or audible glitches. On a Windows station, use the app
without `--demo-audio` to check an actual mic/interface and playback endpoint.
For a sustained qualification, feed continuous audio into the input and play
continuous audio through the loopback endpoint for the entire test.

Set the endpoint IDs (shown in test output; also exposed by `GetDevicesAsync`)
and run the opt-in test in PowerShell:

```powershell
$env:TC_TEST_WASAPI = "1"
$env:TC_WASAPI_INPUT_ID = "<USB interface capture endpoint ID>"
$env:TC_WASAPI_LOOPBACK_ID = "<player render endpoint ID>"
$env:TC_WASAPI_DURATION_SECONDS = "1800"
dotnet test tests/Tropicast.Station.Audio.Windows.Tests -c Release `
  --filter FullyQualifiedName~WindowsHardwareTests --logger "console;verbosity=detailed"
```

The test captures each endpoint for 30 minutes (one hour total), requires at
least 98% of the expected PCM frame count, and fails on malformed packets,
capture errors or queue overruns. It is a continuity check, **not proof that
the result is glitch-free**: listen to or analyze an encoded recording during
station qualification once the encoder is integrated (#8). Publishing a
broadcast is not available yet.

NAudio.Wasapi/NAudio.Core 2.4.0 are MIT-licensed; package license metadata and
upstream attribution are available at <https://github.com/naudio/NAudio>.

### Linux PulseAudio / PipeWire capture

The app automatically selects `LinuxAudioCaptureProvider` on Linux unless
`--demo-audio` is passed. It supervises `pactl` (JSON enumeration and topology
subscription) and `parec` (raw PCM capture), both libpulse clients. This uses
the same PulseAudio protocol on PulseAudio and on PipeWire's `pipewire-pulse`
compatibility server; no shell commands or device-name interpolation are used.

Microphones and mixer/interface sources appear under inputs. Sink **monitor**
sources appear under system output: choose the monitor of the output used by
your player. A monitor captures the whole sink, not one application. Route the
player to a dedicated sink for application-only capture. Suspended/idle sources
remain selectable; recording starts only when you click **Start preview**.

Default source and sink changes refresh their respective default labels
automatically. Selection stays pinned to an explicit source name, rather than
silently following a new default. `stream.dont-move=true` prevents automatic
fallback when the selected endpoint disappears. Device removal or native format
changes stop preview with an error; replugging updates the picker. Audio-server
or subscription failures are visible, and notifications reconnect every two
seconds. Missing packages, server access failures and capture failures report
guidance rather than returning an empty success.

`parec` supplies float32 little-endian PCM at the source's advertised rate and
channel count (libpulse converts its original encoding). The shared converter
handles the encoder target format. Capture requests 40 ms server latency and
20 ms processing; actual latency depends on the server. Owned 20 ms packets
enter a bounded 100-packet queue (approximately two seconds); overload fails
explicitly. Stop/cancellation terminates the owned child and disposal reaps it;
shutdown also terminates the topology subscription.

Run the synthetic native integration test on a running audio server:

```bash
TC_TEST_PULSE=1 dotnet test tests/Tropicast.Station.Audio.Linux.Tests -c Release
```

The test additionally needs `pacat` (in the same utils/libpulse package).
It creates a uniquely named null sink and remapped input, plays only a generated
600 Hz tone into that sink, checks input/monitor signal and stop/restart,
then removes its sources and checks automatic capture shutdown. It does not
record personal microphones or change desktop default devices, and removes its
modules/child processes afterwards. CI runs it against an isolated PulseAudio
server; `TC_TEST_PULSE_DEFAULTS=1` additionally checks default changes and must
only be enabled on an isolated server. PipeWire capture is also exercised
locally. Real USB/mixer hardware and sustained audible-glitch qualification
remain station-side checks; these synthetic tests do not prove those.

This issue delivers PCM preview/capture, not broadcasting; FFmpeg encoding and
Go Live are subsequent issues (#8–#9).

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
