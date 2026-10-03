# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with
[Avalonia](https://avaloniaui.net/) on .NET 10. It captures station audio
(microphone, mixer or application output), encodes it and streams it live to
an Icecast mount.

> Status: desktop scaffold, manual connection profiles, and shared audio
> capture/device-picker pipeline, Windows WASAPI, Linux PulseAudio/PipeWire and
> macOS Core Audio/ScreenCaptureKit adapters and supervised FFmpeg/Icecast
> publishing backend, Go Live workflow, audio meters and automatic reconnect
> and per-profile stream quality/metadata (issues #2–#12). Remaining features
> are tracked in the MVP epic, #1.

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
- Building native macOS capture requires **Xcode Command Line Tools**
  (`xcode-select --install`). No third-party audio library is required.
  Run the `.app` bundle described below for privacy permission requests.

## Build, test and run

```bash
dotnet build
dotnet test
dotnet run --project src/Tropicast.Station.App
```

For an optimized build, use `-c Release`. Debug builds enable Avalonia
Developer Tools (F12).

For encoding, build the pinned native bundle first (C compiler, make, Perl,
pkg-config, curl, tar and an xz-capable tar are required):

```bash
bash scripts/build-ffmpeg.sh linux-x64  # or linux-arm64, osx-arm64, osx-x64
dotnet build -c Release
```

On Windows, run `bash scripts/build-ffmpeg.sh win-x64` from an MSYS2 **MINGW64**
shell with MinGW GCC/pkgconf, make, Perl, curl and tar installed. The script
builds only FFmpeg/LAME and Linux OpenSSL, not the .NET app. Native Linux builds
use the build host's libc baseline; build release bundles on the oldest
supported distribution. Linux arm64 is a native build, not an x64 cross-build.
macOS builds can target either CPU architecture and require Xcode tools.
Sources are checksum-pinned; intermediate outputs stay under ignored `artifacts/`.

## Solution layout

| Project | Responsibility |
|---|---|
| `src/Tropicast.Station.App` | Avalonia UI (views, view models), composition root (`AppHost`) |
| `src/Tropicast.Station.Core` | Domain model and shared services; no UI or platform dependencies |
| `src/Tropicast.Station.Audio` | Audio capture abstractions and platform adapters |
| `src/Tropicast.Station.Audio.Windows` | WASAPI shared-mode input and render-endpoint loopback via NAudio |
| `src/Tropicast.Station.Audio.Linux` | PulseAudio/pipewire-pulse sources and sink monitors via libpulse clients |
| `src/Tropicast.Station.Audio.MacOS` | Core Audio input and ScreenCaptureKit system audio via a native Apple-framework bridge |
| `src/Tropicast.Station.Encoding` | Bounded float32→MP3 FFmpeg child and credential-safe Icecast publisher |
| `src/Tropicast.Station.Infrastructure` | JSON profiles, OS credential stores and Icecast connection testing |
| `tests/Tropicast.Station.Core.Tests` | Unit tests for the class libraries |
| `tests/Tropicast.Station.App.Tests` | Headless Avalonia UI tests |
| `tests/Tropicast.Station.Audio.Tests` | PCM conversion, synthetic capture and hot-plug lifecycle tests |
| `tests/Tropicast.Station.Audio.Windows.Tests` | Windows adapter queue/lifecycle tests and opt-in hardware qualification |
| `tests/Tropicast.Station.Audio.Linux.Tests` | Linux parser/session tests and opt-in synthetic native capture integration |
| `tests/Tropicast.Station.Audio.MacOS.Tests` | macOS adapter lifecycle/permission tests and native enumeration smoke test |
| `tests/Tropicast.Station.Encoding.Tests` | Encoder settings, real bundled codec/lifecycle tests and opt-in Icecast listener POC |
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
monitor sources. On macOS, it lists Core Audio inputs (including virtual inputs)
and a ScreenCaptureKit **System audio** source.
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
shared audio services; the macOS Core Audio adapter follows the same pattern.
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

Audio preview remains local-only. Use **Go Live** in the Broadcast tab to
publish the selected source through the encoding backend.

### macOS Core Audio / system audio capture

Build the app bundle **on a Mac**, then launch it instead of `dotnet run` for
native capture. Use `osx-arm64` on Apple Silicon or `osx-x64` on Intel:

```bash
bash scripts/build-macos.sh osx-arm64
open "artifacts/osx-arm64/Tropicast Station.app"
```

The script publishes a self-contained .NET app and ad-hoc signs the bundle.
The native Objective-C bridge is universal (arm64/x86_64); CI compiles it and
builds both application architectures. The bundle includes microphone and
screen/system-audio usage descriptions. Unbundled execution refuses permission
requests with guidance rather than letting macOS terminate a process missing
its privacy declarations. Ad-hoc builds are for development, not distribution;
stable developer signing/notarization is part of packaging (#14), and rebuilding
may require reauthorizing privacy permissions.

Core Audio enumerates live input devices by persistent device UID, native rate
and input channel count. This includes built-in microphones, USB interfaces and
virtual inputs. An Audio Queue records interleaved float32 at that rate/channel
count; Core Audio converts the hardware encoding. Device/default/format changes
are polled every two seconds. Default changes update labels but never change
the selected source. Device removal or format changes stop capture with an
error instead of switching to another mic.

**Start preview** requests microphone access only for input capture. If denied,
enable **Tropicast Station** under **System Settings → Privacy & Security →
Microphone**, then restart the app. No audio is recorded during enumeration.
The OS permission dialog must be answered before a pending start completes.

The **System audio (ScreenCaptureKit)** source captures whole-system playback
at 48 kHz stereo on macOS 13+, excluding this application's own playback.
It requires Screen Recording permission on macOS 13; newer macOS versions
label this **Screen & System Audio Recording**. macOS may prompt for access;
if capture fails, enable Tropicast Station there and restart. A display must
be available. No screen output callback is registered, and no screen images
are retained, displayed, encoded or broadcast. This is whole-system capture,
not per-application selection or a separate loopback source for each output.
Idle output may provide no samples.

For headless use, unavailable/denied system capture, or application-only routing,
use a virtual input such as [BlackHole](https://github.com/ExistentialAudio/BlackHole).
After installing its driver, route your player's output to BlackHole and select
the **BlackHole input** in Tropicast. To hear it locally too, create a Multi-Output
Device in Audio MIDI Setup combining BlackHole and your speakers; match sample
rates and configure drift correction there. Tropicast does not install drivers,
alter output routing or mix the microphone with system audio automatically.
Virtual inputs still require microphone permission.

Native callbacks are copied into owned PCM packets. A bounded queue (256 packets,
maximum two seconds by byte budget) reports overload instead of dropping samples.
System audio's planar float buffers are interleaved before entering the shared
converter. Stop/cancellation closes the reader immediately and releases native
capture on a worker; disposal drains callbacks before managed delegates are
released. Failed native start and shutdown errors are surfaced explicitly.

CI exercises managed lifecycle, permission/failure mapping, hot-plug/default
refresh and native permission-free enumeration; it cannot grant interactive
privacy permissions or verify physical microphones/interfaces. **Real capture
and hardware qualification are pending on a Mac.** Check the signed development
bundle with a built-in mic and an external interface, permission grant/denial,
system playback, USB unplug/replug, default changes during capture, stop/restart
and sustained preview on both hardware architectures. PCM preview is not yet
broadcasting; use the separate **Go Live** control to publish it.

## FFmpeg / Icecast publishing backend

`IBroadcastEncoder.StartAsync` accepts a resolved `BroadcastTarget` and
optional `EncoderOptions` (float32, mono/stereo, 44.1/48 kHz,
64/96/128/192/320 kbps). Without an override it uses the saved profile's settings;
new profiles default to **44.1 kHz stereo/128 kbps**. The profile must use
**audio/mpeg**. It returns an
`IEncoderSession`; hand normalized `AudioCaptureService.FrameAvailable` frames
to `Submit`, observe `Completion` and `Snapshot`, then stop/dispose the session.
`Submit` copies PCM and never blocks a capture callback. Invalid format,
non-finite samples, 256-packet capacity or a two-second byte-budget overrun
explicitly fail the stream. One publisher per encoder service is allowed.
The Go Live controller connects these services to the UI; starting preview
does **not** broadcast.

FFmpeg reads raw float32 on stdin and writes MP3 on stdout. The managed
publisher shares its authenticated PUT/`Expect: 100-continue` handshake with
**Test connection**, then sends the encoded audio directly to Icecast. Passwords
never appear in FFmpeg arguments, environment, URLs, temporary files or stderr.
Authentication failure, occupied mount and denied publishing have specific
statuses. Both Icecast 2.5's 409 and Icecast 2.4's 403 with the fixed
`Mountpoint in use` plaintext reason are recognized without logging response
text; 2.4's `100 Continue` followed by `200 OK` keeps the stream active.
Disconnect, invalid responses, TLS certificate failures, pipe errors
and ten-second network-write stalls are explicit failures. No redirects or
certificate bypasses are allowed. FFmpeg stderr is drained/classified without
retaining native text. State becomes **Streaming** only after encoded bytes
are sent (not proof that a listener has received them).

Stop completes the PCM queue and closes stdin, allowing FFmpeg to flush MP3.
If shutdown exceeds five seconds, the owned process tree is killed and reaped.
DI host disposal stops an active session even if its caller forgot to do so.
The child owns no server connection: if the parent exits abruptly, its redirected
stdin/stdout/stderr pipes close; EOF/broken pipes make FFmpeg exit rather than
leave a connected source behind. The publisher itself disconnects with the
parent's socket. Graceful exit and failures also observe all pipe tasks before
disposing native streams.

The app loads only `ffmpeg/ffmpeg` (`ffmpeg.exe` on Windows) relative to its
application directory, never an arbitrary PATH executable. Build/publish copies
the RID-specific bundle, **including corresponding sources and full licenses**.
The minimal build includes LAME and TLS (OpenSSL on Linux, Schannel on Windows,
Secure Transport on macOS), with no GPL-only/nonfree features. See
[`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES) for versions, hashes, licensing
and redistribution obligations. Users can replace/rebuild this separate
executable; commercial distribution still requires packaging/license review.

After building the bundle, `dotnet test tests/Tropicast.Station.Encoding.Tests`
exercises real encoding, decode, auth/mount status mapping, queue errors,
network disconnect, graceful/forced process cleanup and owner disposal.
Without a bundle, native codec tests explicitly skip; CI requires one.
For the real listener POC, run a local Icecast with source password
`tc-test-source` and set its port:

```bash
TC_TEST_ICECAST_PORT=18000 dotnet test tests/Tropicast.Station.Encoding.Tests -c Release
```

The POC sends a generated 600 Hz tone to a unique mount, checks the listener's
HTTP success and `audio/mpeg`, captures over four seconds of MP3, decodes it
with the bundled FFmpeg, and verifies stop. No microphone is recorded.
Linux CI runs this against an isolated localhost Icecast in addition to the
PulseAudio/keyring tests; all three OS jobs build and exercise native FFmpeg.

## Audio levels and warnings

The Broadcast tab keeps per-channel **peak** (amber) and **RMS** (green)
meters beside the primary controls. The Audio source tab has the same meters.
Start **preview** to check levels without transmitting, or **Go Live** to monitor
the exact normalized float32 PCM feeding the encoder (after channel mapping
and resampling, before MP3 compression). Levels refresh at 25 Hz, in dBFS;
zero audio is displayed at the -90 dBFS meter floor. Numeric readings can
exceed 0 dBFS even though the bars stop at 0.

**CLIPPING** appears per channel for samples at or above -0.1 dBFS, with a
two-second hold refreshed by further clipping. **SILENCE** appears when every
channel stays below the configured RMS threshold for the configured duration.
Defaults are -50 dBFS and five seconds; expand **Silence warning settings**
to choose -90 to -10 dBFS and one to 60 seconds. Changes restart the silence
countdown. A silent loopback endpoint that delivers no packets is also detected.
Signal recovery clears the silence warning; stopping, device loss or a new
capture clears the readings and clipping hold.

The optional **in-app notification** is off by default and fires once per
silence episode, not on every meter tick. Warnings work in preview and live
mode and never stop the stream. These session-only meter preferences reset
when the app restarts; they are independent of saved profile encoder settings.

## Go Live / Stop

The first **Broadcast** tab provides the three-step station workflow:
select a **saved connection profile**, select **one input or system-output
source**, then press **Go Live**. Create/save credentials in the Connection
profiles tab first. The current encoder requires an **audio/mpeg** profile and
the bundled FFmpeg described above. Starting a broadcast replaces any active
local preview with capture in the selected encoder format.
The Broadcast tab shows the selected saved profile's bitrate, rate, channels
and stream name. Unsaved editor changes and the source picker's **preview**
rate/channels do not override saved broadcast settings.

The status badge and text expose **Idle → Connecting → Live → Stopping → Idle**,
plus **Error** on capture/connection/encoder failure. **Live** begins when the
encoder sends MP3 bytes, not just when authentication succeeds. The elapsed
counter accumulates actual Live time (excluding reconnect downtime) and retains
the last session duration after stopping.
An idle system-output endpoint may not emit audio yet and remains Connecting.
Transient publisher failures enter **Reconnecting** automatically. There is no
silent source/profile fallback.

While Connecting/Live/Reconnecting/Stopping, profile editing/testing, source selection,
encoder-format controls and local preview commands are locked. **Stop broadcast**
asks for confirmation when live or reconnecting; **Keep broadcasting** (or Escape) cancels the
dialog. Stop while Connecting cancels startup without a live-stream warning.
Closing the window or using tray **Quit** also asks before ending a live stream;
confirming stops capture and flushes/reaps FFmpeg before closing.
SIGTERM/OS/process termination still performs host cleanup where possible,
but cannot always present an interactive confirmation.

The tray/menu-bar uses the Tropicast logo, with a red live dot for Live and
Reconnecting, state/elapsed/countdown tooltip and menu, **Show**, **Stop broadcast**
and **Quit** actions. Minimize keeps broadcasting; close means quit after
confirmation, not hide-to-tray. A supported system tray is optional: the same
controls remain available in the window. Buttons/selectors have screen-reader
names; normal Tab navigation and Enter/Space activate controls. The prominent
Go Live/Stop controls remain visible while source settings scroll.

Headless tests cover profile/source prerequisites, keyboard start/stop, state
text, selector locks, tray status, denied Stop/close, accepted close, capture
removal and encoder errors. The opt-in Icecast suite additionally runs the
whole controller with synthetic capture → converter → FFmpeg → real listener
and verifies decodable MP3 and stop. This is not a physical microphone or
manual platform accessibility qualification.

### Automatic reconnect

A dropped Icecast connection, network timeout, HTTP 5xx response or unexpected
publisher process exit triggers a fresh encoder/source session on the **same
saved target**. Initial unreachable connections also retry. Backoff begins at
one second and doubles to a 30-second maximum, with ±20% jitter (still capped
at 30 seconds). A successful return to Live resets the backoff.

Capture and PCM meters continue during reconnect; audio produced while the
publisher is absent is **not buffered or replayed**. The UI shows the upcoming
attempt number and countdown, then connecting/waiting-for-audio status.
**Retry now** skips the wait, without starting a parallel handshake; it is
disabled while a new publisher is connecting or waiting for its first audio.
**Stop broadcast** and confirmed Quit cancel both waits and in-flight
handshakes, stop capture and release the encoder.

Authentication failures, TLS certificate/configuration failures, occupied
mounts, other HTTP 4xx responses, invalid/missing encoder configuration,
queue overruns and capture/device failures are **terminal**. They show an
actionable Error and stop capture, rather than retrying forever. Correct the
profile/source/bundle and use Go Live again. Error classification uses typed
source/encoder failures, never arbitrary server message text. Credentials
stay managed and do not enter native process arguments or diagnostic logs.

The UI and controller snapshot expose the number of **successful reconnects**
and cumulative **downtime after first Live**, including reconnect handshakes
and waiting for audio. Counters retain the last session values after Stop and
reset at the next Go Live. Initial connection waiting is not live downtime.

## Connection profiles

Create a profile with a name, hostname/IP (no scheme or port), port, mount path
(for example `/live.mp3`), source username (`source` by default), password,
TLS selection and content type. Each profile also saves **MP3 bitrate**
(64/96/128/192/320 kbps), **sample rate** (44100/48000 Hz), and **channels**
(1 mono / 2 stereo). Defaults are 128 kbps, 44100 Hz, stereo. Existing profiles
without these fields load with those defaults; explicitly invalid values are
reported as corrupt/invalid settings, not silently replaced.

Optional **stream name**, **description**, **genre** and **website URL** identify
the stream to listeners. The profile name labels the local saved connection;
it is distinct from the public stream name. These values are sent with the
authenticated source handshake as `Ice-Name`, `Ice-Description`, `Ice-Genre`,
`Ice-URL`, `Ice-Bitrate` and `Ice-Audio-Info`. Metadata is sent again on every
reconnect. Icecast's `/status-json.xsl` exposes `server_name`,
`server_description`, `genre`, `server_url` and `bitrate` while the mount is live.
Blank optional metadata is omitted, allowing server defaults.

Names/genres allow up to 128 characters, descriptions/URLs up to 512.
Control characters are rejected; website URLs must be absolute HTTP/HTTPS
addresses without credentials. Metadata is public: never enter passwords or
other secrets there. The encoder remains MP3-only despite profiles supporting
other content types for connection testing.

**Save profile** writes non-secret settings
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

Broadcasting resolves a saved profile through
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

The independent restart test launches its own loopback-only Icecast, stops
and starts that exact server, verifies automatic recovery with decodable
listener audio, and checks that wrong credentials are terminal. It does not
restart the shared `TC_TEST_ICECAST_PORT` instance. Ubuntu CI runs it using
`TC_TEST_ICECAST_EXECUTABLE=/usr/bin/icecast2`. Locally, opt in with either a
native executable or an existing Docker image:

```bash
TC_TEST_ICECAST_DOCKER_IMAGE=tropicast-icecast dotnet test \
  tests/Tropicast.Station.Encoding.Tests -c Release \
  --filter FullyQualifiedName~ReconnectIntegrationTests
# Native Linux alternative:
TC_TEST_ICECAST_EXECUTABLE=/usr/bin/icecast2 dotnet test \
  tests/Tropicast.Station.Encoding.Tests -c Release \
  --filter FullyQualifiedName~ReconnectIntegrationTests
```

The test uses known test-only passwords, synthetic tones, a unique private
container/process and an ephemeral host port; it removes its own resources
afterward and never records microphones or restarts a production server.

The same opt-in server harness verifies stream metadata against
`status-json.xsl`, then decodes listener MP3 to check the saved sample rate and
channel count. Use `--filter FullyQualifiedName~Real_Icecast_status` with either
server environment variable above to run that acceptance check alone.
