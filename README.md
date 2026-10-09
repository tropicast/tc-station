# Tropicast Station

Cross-platform desktop broadcaster for Tropicast radio stations, built with [Avalonia](https://avaloniaui.net/) on .NET 10. Captures station audio (mic, mixer or app output), encodes, streams live to Icecast mount.

> Status: desktop scaffold, manual connection profiles, shared audio capture/device-picker pipeline, Windows WASAPI, Linux PulseAudio/PipeWire, macOS Core Audio/ScreenCaptureKit adapters, supervised FFmpeg/Icecast publishing backend, Go Live workflow, audio meters, auto reconnect, per-profile stream quality/metadata, safe diagnostics (issues #2–#13). Remaining features tracked in MVP epic, #1.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned by `global.json`)
- Windows 10+, Linux with X11 or Wayland (XWayland), or macOS 13+
- Linux credentials need **libsecret's `secret-tool`** (`libsecret-tools` on Debian/Ubuntu, `libsecret` on Arch) + unlocked Secret Service keyring (e.g. GNOME Keyring). No plaintext fallback.
- Linux audio needs **`pactl` and `parec`** (`pulseaudio-utils` on Debian/Ubuntu, `libpulse` on Arch) + running PulseAudio server or PipeWire with **`pipewire-pulse`**. Use recent `pactl` with JSON output (PulseAudio 15+). No root needed for capture.
- Native macOS capture build needs **Xcode Command Line Tools** (`xcode-select --install`). No third-party audio lib. Run `.app` bundle (below) for privacy permission requests.

## Build, test and run

```bash
dotnet build
dotnet test
dotnet run --project src/Tropicast.Station.App
```

Optimized build: `-c Release`. Debug builds enable Avalonia Developer Tools (F12).

For encoding, build pinned native bundle first (needs C compiler, make, Perl, pkg-config, curl, tar, xz-capable tar):

```bash
bash scripts/build-ffmpeg.sh linux-x64  # or linux-arm64, osx-arm64, osx-x64
dotnet build -c Release
```

Windows: run `bash scripts/build-ffmpeg.sh win-x64` from MSYS2 **MINGW64** shell with MinGW GCC/pkgconf, make, Perl, curl, tar. Script builds only FFmpeg/LAME/Opus + Linux OpenSSL, not .NET app. Native Linux builds use build host's libc baseline; build release bundles on oldest supported distro. Linux arm64 = native build, not x64 cross-build. macOS builds target either CPU arch, need Xcode tools. Sources checksum-pinned; intermediate outputs under ignored `artifacts/`.

## Solution layout

| Project | Responsibility |
|---|---|
| `src/Tropicast.Station.App` | Avalonia UI (views, view models), composition root (`AppHost`) |
| `src/Tropicast.Station.Core` | Domain model + shared services; no UI/platform deps |
| `src/Tropicast.Station.Audio` | Audio capture abstractions + platform adapters |
| `src/Tropicast.Station.Audio.Windows` | WASAPI shared-mode input + render-endpoint loopback via NAudio |
| `src/Tropicast.Station.Audio.Linux` | PulseAudio/pipewire-pulse sources + sink monitors via libpulse clients |
| `src/Tropicast.Station.Audio.MacOS` | Core Audio input + ScreenCaptureKit system audio via native Apple-framework bridge |
| `src/Tropicast.Station.Encoding` | Bounded float32→MP3/Opus FFmpeg children + credential-safe Icecast publishers |
| `src/Tropicast.Station.Infrastructure` | JSON profiles, OS credential stores, Icecast connection testing |
| `tests/Tropicast.Station.Core.Tests` | Unit tests for class libs |
| `tests/Tropicast.Station.App.Tests` | Headless Avalonia UI tests |
| `tests/Tropicast.Station.Audio.Tests` | PCM conversion, synthetic capture, hot-plug lifecycle tests |
| `tests/Tropicast.Station.Audio.Windows.Tests` | Windows adapter queue/lifecycle tests + opt-in hardware qualification |
| `tests/Tropicast.Station.Audio.Linux.Tests` | Linux parser/session tests + opt-in synthetic native capture integration |
| `tests/Tropicast.Station.Audio.MacOS.Tests` | macOS adapter lifecycle/permission tests + native enumeration smoke test |
| `tests/Tropicast.Station.Encoding.Tests` | Encoder settings, real bundled codec/lifecycle tests, opt-in Icecast listener POC |
| `tests/Tropicast.Station.Infrastructure.Tests` | Persistence, source handshake, native credential-store tests |

Deps point inward: `App` → `Audio` / `Encoding` / `Infrastructure` → `Core`.

## Audio sources and preview

**Audio source** tab groups devices into **Microphones / inputs** and **Application / system output**. Pick one device, encoder sample rate (44.1 or 48 kHz), mono/stereo, then **Start preview**. Consumes captured PCM through shared conversion pipeline; no playback, encoding, or Icecast publish. Levels + broadcast controls = separate MVP issues.

Windows: lists active WASAPI inputs + playback devices (loopback sources). Linux: PulseAudio/PipeWire inputs + sink monitor sources. macOS: Core Audio inputs (incl. virtual) + ScreenCaptureKit **System audio** source.
Run synthetic adapter on any OS for demos:

```bash
dotnet run --project src/Tropicast.Station.App -- --demo-audio
```

Demo mode supplies 440 Hz, 44.1 kHz mono signed-16 input + 660 Hz, 48 kHz stereo float32 loopback source, both labelled **Demo**. Generated tones, not mic/app recordings.

### Adapter contract and pipeline

`IAudioCaptureProvider` exposes enumeration, `DevicesChanged` notifications, `StartAsync`. `AudioDevice` carries stable ID, display name, kind, default flag, native `AudioFormat`. `IAudioCaptureSession` delivers interleaved little-endian signed16 or float32 PCM via single-consumer async stream. Each `PcmFrame` owns its buffer; adapters must not reuse memory. Stop, dispose, reader cancel must unblock capture, safe concurrently/repeatedly. Bounded native queues must report overruns, not silently drop audio. Device loss throws `IOException`.

`AudioCaptureService` serializes lifecycle/device refreshes, cancels + disposes capture on removal or native-format change, emits visible error without switching device. Idle hot-plug/default-device changes auto-refresh picker. Selected device preserved by ID on rename. Windows WASAPI + Linux PulseAudio adapters registered before shared audio services; macOS Core Audio adapter same pattern. Use `TryAdd` registration so explicitly registered adapter not overwritten.

`PcmConverter` produces float32 mono/stereo PCM for future encoder via streaming windowed-sinc low-pass resampler, 32 input-frame lookahead (~0.73 ms at 44.1 kHz). Mono duplicated to stereo; stereo averaged to mono. More channels: mono averages all; stereo averages alternating channel indices. Deliberately **not** speaker-layout-aware surround downmix. No gain/limiting. Conversion state carries across chunks; `Flush()` drains finite source, device-loss/stop discards tail. Invalid PCM alignment, non-finite float samples, midstream format changes → explicit errors.

`AudioCaptureService.FrameAvailable` = future encoder integration point: frames already in requested format. Subscribers run on capture thread, must not block or throw. Marshal UI work to UI dispatcher; keep encoder handoff bounded, report overloads. `ToneAudioCaptureProvider` also exposes `SetDevices` for deterministic hot-plug tests/demos.

### Windows WASAPI capture

App auto-selects `WindowsAudioCaptureProvider` on Windows unless `--demo-audio` passed. Active capture endpoints (incl. USB mixer/interface inputs) and render endpoints listed separately; Windows **Multimedia** default labelled. Capture targets explicit endpoint ID, never silently follows different default. Add/remove/state/default/property notifications trigger refresh via `IMMNotificationClient`. Removing active endpoint stops capture + shows guidance; replug refreshes picker.

Render-endpoint loopback captures **all audio played through that endpoint**, not single app/process. Pick endpoint your player uses. WASAPI may deliver no packets while render endpoint idle; adapter doesn't invent silence or play keepalive tone. Continuous loopback needs actively playing source.

Capture uses WASAPI **shared mode**, 100 ms native buffers, worker-thread init (no captured UI sync context). PCM16 + float32 mix formats preserved. PCM24/PCM32 mix formats request float32 via WASAPI shared-mode conversion, keeping endpoint rate/channel count. Picker format therefore = PCM supplied by adapter, not necessarily physical bit depth. Common `PcmConverter` converts to chosen encoder format. Non-PCM formats + rates/channel counts outside shared contract rejected explicitly.

NAudio callback buffers copied before reuse. Bounded queue (256 packets, max 2 s PCM by byte budget) fails explicitly on overrun, no silent drop. Capture errors + device invalidation close stream; cancel/stop unblocks readers; dispose joins native capture thread on worker before releasing endpoint.

If Windows denies mic access, app shows settings path: **Settings → Privacy & security → Microphone** (Windows 10: **Settings → Privacy → Microphone**). Enable **Microphone access** and **Let desktop apps access your microphone**. Adapter checks explicit Windows mic-consent denials as preflight hint, then treats WASAPI access-denied as authoritative. Loopback not blocked by mic-only denial. Exclusive-device use, stopped Windows Audio service, unsupported format, invalidation have separate guidance.

### Windows hardware qualification (not exercised by hosted CI)

Hosted CI runs adapter with injected fake native backend: can't verify USB interfaces or audible glitches. On Windows station, run app without `--demo-audio` to check real mic/interface + playback endpoint. For sustained qualification, feed continuous audio into input and play continuous audio through loopback endpoint for whole test.

Set endpoint IDs (shown in test output; also exposed by `GetDevicesAsync`), run opt-in test in PowerShell:

```powershell
$env:TC_TEST_WASAPI = "1"
$env:TC_WASAPI_INPUT_ID = "<USB interface capture endpoint ID>"
$env:TC_WASAPI_LOOPBACK_ID = "<player render endpoint ID>"
$env:TC_WASAPI_DURATION_SECONDS = "1800"
dotnet test tests/Tropicast.Station.Audio.Windows.Tests -c Release `
  --filter FullyQualifiedName~WindowsHardwareTests --logger "console;verbosity=detailed"
```

Test captures each endpoint 30 min (1 h total), requires ≥98% expected PCM frame count, fails on malformed packets, capture errors, queue overruns. Continuity check, **not proof that the result is glitch-free**: listen to/analyze encoded recording during station qualification once encoder integrated (#8). Broadcast publishing not available yet.

NAudio.Wasapi/NAudio.Core 2.4.0 MIT-licensed; package license metadata + upstream attribution at <https://github.com/naudio/NAudio>.

### Linux PulseAudio / PipeWire capture

App auto-selects `LinuxAudioCaptureProvider` on Linux unless `--demo-audio` passed. Supervises `pactl` (JSON enumeration + topology subscription) and `parec` (raw PCM capture), both libpulse clients. Same PulseAudio protocol on PulseAudio and PipeWire's `pipewire-pulse` compat server; no shell commands or device-name interpolation.

Mics + mixer/interface sources under inputs. Sink **monitor** sources under system output: pick monitor of output your player uses. Monitor captures whole sink, not one app. Route player to dedicated sink for app-only capture. Suspended/idle sources stay selectable; recording starts only on **Start preview**.

Default source/sink changes auto-refresh default labels. Selection pinned to explicit source name, not silently following new default. `stream.dont-move=true` prevents auto fallback when selected endpoint vanishes. Device removal or native format change stops preview with error; replug updates picker. Audio-server/subscription failures visible; notifications reconnect every 2 s. Missing packages, server access failures, capture failures report guidance, not empty success.

`parec` supplies float32 LE PCM at source's advertised rate + channel count (libpulse converts original encoding). Shared converter handles encoder target format. Capture requests 40 ms server latency + 20 ms processing; actual latency server-dependent. Owned 20 ms packets enter bounded 100-packet queue (~2 s); overload fails explicitly. Stop/cancel terminates owned child, dispose reaps it; shutdown also terminates topology subscription.

Run synthetic native integration test on running audio server:

```bash
TC_TEST_PULSE=1 dotnet test tests/Tropicast.Station.Audio.Linux.Tests -c Release
```

Test also needs `pacat` (same utils/libpulse package). Creates uniquely named null sink + remapped input, plays only generated 600 Hz tone into sink, checks input/monitor signal + stop/restart, then removes sources and checks auto capture shutdown. Doesn't record personal mics or change desktop defaults; removes its modules/child processes after. CI runs against isolated PulseAudio server; `TC_TEST_PULSE_DEFAULTS=1` also checks default changes — enable only on isolated server. PipeWire capture also exercised locally. Real USB/mixer hardware + sustained audible-glitch qualification remain station-side; synthetic tests don't prove those.

Audio preview stays local-only. Use **Go Live** in Broadcast tab to publish selected source via encoding backend.

### macOS Core Audio / system audio capture

Build app bundle **on a Mac**, launch it instead of `dotnet run` for native capture. `osx-arm64` on Apple Silicon, `osx-x64` on Intel:

```bash
bash scripts/build-macos.sh osx-arm64
open "artifacts/osx-arm64/Tropicast Station.app"
```

Script publishes self-contained .NET app + ad-hoc signs bundle. Native Objective-C bridge universal (arm64/x86_64); CI compiles it + builds both app archs. Bundle includes mic + screen/system-audio usage descriptions. Unbundled execution refuses permission requests with guidance rather than letting macOS kill process missing privacy declarations. Ad-hoc builds = dev, not distribution; stable dev signing/notarization part of packaging (#14); rebuild may need reauthorizing privacy permissions.

Core Audio enumerates live input devices by persistent device UID, native rate, input channel count. Includes built-in mics, USB interfaces, virtual inputs. Audio Queue records interleaved float32 at that rate/channel count; Core Audio converts hardware encoding. Device/default/format changes polled every 2 s. Default changes update labels, never change selected source. Device removal or format change stops capture with error instead of switching mic.

**Start preview** requests mic access only for input capture. If denied, enable **Tropicast Station** under **System Settings → Privacy & Security → Microphone**, restart app. No audio recorded during enumeration. OS permission dialog must be answered before pending start completes.

**System audio (ScreenCaptureKit)** source captures whole-system playback at 48 kHz stereo on macOS 13+, excluding this app's own playback. Needs Screen Recording permission on macOS 13; newer macOS labels it **Screen & System Audio Recording**. macOS may prompt; if capture fails, enable Tropicast Station there + restart. Display must be available. No screen output callback registered; no screen images retained, displayed, encoded or broadcast. Whole-system capture, not per-app selection or separate loopback per output. Idle output may give no samples.

For headless use, unavailable/denied system capture, or app-only routing, use virtual input like [BlackHole](https://github.com/ExistentialAudio/BlackHole). After installing driver, route player output to BlackHole, select **BlackHole input** in Tropicast. To also hear locally, create Multi-Output Device in Audio MIDI Setup combining BlackHole + speakers; match sample rates, configure drift correction there. Tropicast doesn't install drivers, alter output routing, or auto-mix mic with system audio. Virtual inputs still need mic permission.

Native callbacks copied into owned PCM packets. Bounded queue (256 packets, max 2 s by byte budget) reports overload instead of dropping samples. System audio's planar float buffers interleaved before shared converter. Stop/cancel closes reader immediately, releases native capture on worker; dispose drains callbacks before managed delegates released. Failed native start + shutdown errors surfaced explicitly.

CI exercises managed lifecycle, permission/failure mapping, hot-plug/default refresh, native permission-free enumeration; can't grant interactive privacy permissions or verify physical mics/interfaces. **Real capture and hardware qualification are pending on a Mac.** Check signed dev bundle with built-in mic + external interface, permission grant/denial, system playback, USB unplug/replug, default changes during capture, stop/restart, sustained preview on both hardware archs. PCM preview not broadcasting; use separate **Go Live** control to publish.

## FFmpeg / Icecast publishing backend

`IBroadcastEncoder.StartAsync` accepts resolved `BroadcastTarget` + optional `EncoderOptions` (float32, mono/stereo, 44.1/48 kHz, 64/96/128/192/320 kbps). Without override uses saved profile's settings; new profiles default **44.1 kHz stereo/128 kbps**. Profile must use **audio/mpeg**. Returns `IEncoderSession`; hand normalized `AudioCaptureService.FrameAvailable` frames to `Submit`, observe `Completion` and `Snapshot`, then stop/dispose session. `Submit` copies PCM, never blocks capture callback. Invalid format, non-finite samples, 256-packet capacity or 2 s byte-budget overrun explicitly fail stream. One publisher per encoder service. Go Live controller connects these services to UI; starting preview does **not** broadcast.

FFmpeg reads raw float32 on stdin, writes MP3 on stdout. Managed publisher shares authenticated PUT/`Expect: 100-continue` handshake with **Test connection**, then sends encoded audio directly to Icecast. Passwords never in FFmpeg args, env, URLs, temp files or stderr. Auth failure, occupied mount, denied publishing have specific statuses. Icecast 2.5's 409 and Icecast 2.4's 403 with fixed `Mountpoint in use` plaintext reason both recognized without logging response text; 2.4's `100 Continue` then `200 OK` keeps stream active. Disconnect, invalid responses, TLS cert failures, pipe errors, 10 s network-write stalls = explicit failures. No redirects or cert bypasses. FFmpeg stderr drained/classified without retaining native text. State becomes **Streaming** only after encoded bytes sent (not proof listener received them).

Stop completes PCM queue + closes stdin, letting FFmpeg flush MP3. Shutdown >5 s → owned process tree killed + reaped. DI host dispose stops active session even if caller forgot. Child owns no server connection: if parent exits abruptly, redirected stdin/stdout/stderr pipes close; EOF/broken pipes make FFmpeg exit, no lingering connected source. Publisher disconnects with parent's socket. Graceful exit + failures observe all pipe tasks before disposing native streams.

App loads only `ffmpeg/ffmpeg` (`ffmpeg.exe` on Windows) relative to app dir, never arbitrary PATH executable. Build/publish copies RID-specific bundle, **including corresponding sources and full licenses**. Minimal build includes LAME, Opus + TLS (OpenSSL on Linux, Schannel on Windows, Secure Transport on macOS), no GPL-only/nonfree features. See [`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES) for versions, hashes, licensing, redistribution obligations. Users can replace/rebuild this separate executable; commercial distribution still needs packaging/license review.

After building bundle, `dotnet test tests/Tropicast.Station.Encoding.Tests` exercises real encoding, decode, auth/mount status mapping, queue errors, network disconnect, graceful/forced process cleanup, owner dispose. Without bundle, native codec tests explicitly skip; CI requires one. For real listener POC, run local Icecast with source password `tc-test-source` and set its port:

```bash
TC_TEST_ICECAST_PORT=18000 dotnet test tests/Tropicast.Station.Encoding.Tests -c Release
```

POC sends generated 600 Hz tone to unique mount, checks listener HTTP success + `audio/mpeg`, captures >4 s MP3, decodes with bundled FFmpeg, verifies stop. No mic recorded. Linux CI runs this against isolated localhost Icecast plus PulseAudio/keyring tests; all three OS jobs build + exercise native FFmpeg.

## Audio levels and warnings

Broadcast tab keeps per-channel **peak** (amber) + **RMS** (green) meters beside primary controls. Audio source tab has same meters. **Preview** checks levels without transmitting; **Go Live** monitors exact normalized float32 PCM feeding encoder (after channel mapping + resampling, before MP3 compression). Levels refresh 25 Hz, in dBFS; zero audio shown at -90 dBFS meter floor. Numeric readings can exceed 0 dBFS though bars stop at 0.

**CLIPPING** shows per channel for samples ≥ -0.1 dBFS, 2 s hold refreshed by further clipping. **SILENCE** shows when every channel stays below configured RMS threshold for configured duration. Defaults -50 dBFS, 5 s; expand **Silence warning settings** to pick -90 to -10 dBFS and 1–60 s. Changes restart silence countdown. Silent loopback endpoint delivering no packets also detected. Signal recovery clears silence warning; stop, device loss, or new capture clears readings + clipping hold.

Optional **in-app notification** off by default, fires once per silence episode, not every meter tick. Warnings work in preview + live, never stop stream. Session-only meter prefs reset on app restart; independent of saved profile encoder settings.

## Go Live / Stop

First **Broadcast** tab = three-step workflow: pick **saved connection profile**, pick **one input or system-output source**, press **Go Live**. Create/save credentials in Connection profiles tab first. Current encoder needs **audio/mpeg** profile + bundled FFmpeg above. Starting broadcast replaces any active local preview with capture in selected encoder format.
Broadcast tab shows selected saved profile's bitrate, rate, channels, stream name. Unsaved editor changes and source picker's **preview** rate/channels don't override saved broadcast settings.

Status badge/text expose **Idle → Connecting → Live → Stopping → Idle**, plus **Error** on capture/connection/encoder failure. **Live** begins when encoder sends MP3 bytes, not just on auth success. Elapsed counter accumulates actual Live time (excl. reconnect downtime), retains last session duration after stop.
Idle system-output endpoint may not emit audio yet → stays Connecting.
Transient publisher failures enter **Reconnecting** automatically. No silent source/profile fallback.

While Connecting/Live/Reconnecting/Stopping: profile editing/testing, source selection, encoder-format controls, local preview commands locked. **Stop broadcast** asks confirmation when live or reconnecting; **Keep broadcasting** (or Escape) cancels dialog. Stop while Connecting cancels startup without live-stream warning.
Closing window or tray **Quit** also asks before ending live stream; confirming stops capture + flushes/reaps FFmpeg before closing.
SIGTERM/OS/process termination still does host cleanup where possible, but can't always show interactive confirmation.

Tray/menu-bar uses Tropicast logo, red live dot for Live + Reconnecting, state/elapsed/countdown tooltip + menu, **Show**, **Stop broadcast**, **Quit** actions. Minimize keeps broadcasting; close = quit after confirmation, not hide-to-tray. System tray optional: same controls in window. Buttons/selectors have screen-reader names; normal Tab nav + Enter/Space activate controls. Prominent Go Live/Stop controls stay visible while source settings scroll.

Headless tests cover profile/source prerequisites, keyboard start/stop, state text, selector locks, tray status, denied Stop/close, accepted close, capture removal, encoder errors. Opt-in Icecast suite also runs whole controller with synthetic capture → converter → FFmpeg → real listener, verifies decodable MP3 + stop. Not physical mic or manual platform accessibility qualification.

### Automatic reconnect

Dropped Icecast connection, network timeout, HTTP 5xx or unexpected publisher process exit triggers fresh encoder/source session on **same saved target**. Initial unreachable connections also retry. Backoff starts 1 s, doubles to 30 s max, ±20% jitter (still capped 30 s). Successful return to Live resets backoff.

Capture + PCM meters continue during reconnect; audio produced while publisher absent **not buffered or replayed**. UI shows upcoming attempt number + countdown, then connecting/waiting-for-audio status.
**Retry now** skips wait, no parallel handshake; disabled while new publisher connecting or waiting for first audio.
**Stop broadcast** and confirmed Quit cancel waits + in-flight handshakes, stop capture, release encoder.

Auth failures, TLS cert/config failures, occupied mounts, other HTTP 4xx, invalid/missing encoder config, queue overruns, capture/device failures = **terminal**. Show actionable Error + stop capture, no endless retry. Fix profile/source/bundle, Go Live again. Error classification uses typed source/encoder failures, never arbitrary server message text. Credentials stay managed, never in native process args or diagnostic logs.

UI + controller snapshot expose count of **successful reconnects** and cumulative **downtime after first Live**, incl. reconnect handshakes + waiting for audio. Counters keep last session values after Stop, reset on next Go Live. Initial connection wait ≠ live downtime.

## Connection profiles

Profile = name, hostname/IP (no scheme/port), port, mount path (e.g. `/live.mp3`), source username (`source` default), password, TLS choice, content type. Each profile also saves **MP3 bitrate** (64/96/128/192/320 kbps), **sample rate** (44100/48000 Hz), **channels** (1 mono / 2 stereo). Defaults 128 kbps, 44100 Hz, stereo. Existing profiles lacking these fields load with defaults; explicitly invalid values reported as corrupt/invalid, not silently replaced.

Optional **stream name**, **description**, **genre**, **website URL** identify stream to listeners. Profile name labels local saved connection; distinct from public stream name. Values sent with authenticated source handshake as `Ice-Name`, `Ice-Description`, `Ice-Genre`, `Ice-URL`, `Ice-Bitrate` and `Ice-Audio-Info`. Metadata resent every reconnect. Icecast's `/status-json.xsl` exposes `server_name`, `server_description`, `genre`, `server_url` and `bitrate` while mount live. Blank optional metadata omitted → server defaults.

Names/genres ≤128 chars, descriptions/URLs ≤512. Control chars rejected; website URLs must be absolute HTTP/HTTPS without credentials. Metadata public: never put passwords/secrets there. Encoder publishes MP3 (`audio/mpeg`) or Ogg Opus (`audio/ogg`, 48/64/96 kbps, always 48 kHz); `audio/aac` profiles are for connection testing only.

**Also publish Ogg Opus** (MP3 profiles whose mount ends in `.mp3`) adds a second stream from the same capture on the matching `.opus` mount (`/stations/42/live.mp3` → `/stations/42/live.opus`) at the profile's **Opus bitrate** (default 64 kbps; 48 kbps on the free plan). Each stream has its own FFmpeg child, `PUT` with its own `Content-Type`, `Ice-Bitrate` and `Ice-Audio-Info`, and its own reconnect backoff: losing or being refused one stream (e.g. the station's plan allows only MP3, so Tropicast answers 401 on `.opus`) does not stop the other. The broadcast stays **Live** while any stream is live, the Broadcast tab lists each stream's state, and the broadcast ends in **Error** only when every stream has failed permanently.

**Save profile** writes non-secret settings to user's app-data dir:

- Windows: `%APPDATA%\Tropicast\Station\profiles.json`
- Linux: `$XDG_CONFIG_HOME/Tropicast/Station/profiles.json` (normally `~/.config`)
- macOS: `~/Library/Application Support/Tropicast/Station/profiles.json`

Passwords stored separately in Windows Credential Manager, macOS Keychain, or Linux Secret Service (libsecret). Saved passwords never loaded into editor; leave password blank to keep existing. Delete removes selected profile + its credential. Failed settings write tries to restore previous credential.

Use TLS for non-local connections: without TLS, HTTP Basic source credentials + audio travel unencrypted. TLS certs must be trusted + match host; app doesn't bypass cert checks.

**Test connection** uses current editor values (unsaved). Does authenticated Icecast `PUT` with `Expect: 100-continue`, sends no audio, closes immediately. Briefly reserves free mount; success = point-in-time check, not reservation for later broadcast. Auth failure (401), mount in use (409), publishing denied (403), network failure, TLS errors reported separately. Don't use Icecast admin or shared production source password.

Broadcasting resolves saved profile via `IBroadcastTargetProvider`. `ManualBroadcastTargetProvider` rejects invalid profiles + missing credentials before returning ephemeral target; future API-backed provider can replace it without changing capture pipeline.

## Logging and safe diagnostics

**Diagnostics** tab shows user log dir; **Export diagnostics** opens ZIP save dialog. Nothing uploaded automatically. Bundle has `diagnostics.json` (app/OS/runtime versions, arch, anonymous device kinds/default flags/native formats, broadcast state, reconnect/downtime counters) and `logs/recent.jsonl` (up to 5 recent log files).

Logs use .NET `ILogger` structured events + small rolling JSON-lines provider; no new logging framework. Files roll at **1 MiB**, **10 files retained**, in:

- Windows: `%LOCALAPPDATA%\Tropicast\Station\logs`
- Linux: `$XDG_STATE_HOME/Tropicast/Station/logs`, normally `~/.local/state/Tropicast/Station/logs`
- macOS: `~/Library/Logs/Tropicast/Station`

Files created owner read/write; log dir owner-only on Unix. Each process gets unique filenames. Recent records flushed synchronously; writes/rotation serialized. Logging = Information+; broadcast state changes recorded, not every meter tick. Disk/permission failures set explicit failure flag + emit fixed credential-safe stderr warning; export reports logging failure.

**Every configured sink**, incl. stderr, uses same fail-closed projection. Keeps only timestamp, severity, approved category, numeric event ID, approved error type, approved numeric counters. Raw formatted messages, templates, scopes, arbitrary structured fields, exception messages, stack traces, native stderr, URLs, FFmpeg command lines omitted—not regex-redacted after logging. Also protects credentials not yet loaded into app, encoded credentials, malicious diagnostic text. Avalonia's unfiltered trace sink disabled. App-owned source classes + runtime messages use **Tropicast** naming; external Icecast commands/schema/headers + technical refs keep actual names.

Events `1301` (bundle assembled), `1302` (broadcast state/counters), `1303` (unhandled error), `1304` (export failure) support diagnosis. Broadcast state numbers: Idle=0, Connecting=1, Live=2, Reconnecting=3, Stopping=4, Error=5. Existing capture/profile/encoder warnings keep category + approved error type.

Export excludes profiles/passwords, endpoints, stream metadata, device names/stable IDs, usernames, machine names, user paths, raw errors, audio. Recent logs parsed + re-projected before export, so manually added free text/fields can't leak. Corrupt/oversized logs → visible export failure, not incomplete success. Device identities intentionally anonymous; users can describe hardware separately.

Unhandled dispatcher errors + unobserved task failures stop capture/publishing where possible, show generic restart-required dialog, quit. Handlers detached on exit. Fatal CLR/process errors can't reliably show/await dialog: handler synchronously records safe error type, emits fixed restart message, exits code 1 before runtime can print raw managed exception details. Native fatal errors outside this managed handler's control. Startup errors also return exit code 1 with safe logs/stderr; no secret-bearing exception text shown. Handlers don't claim to recover corrupted state.

Automated tests inject credential-bearing messages, scopes, command lines, URLs, exceptions, edited log records; verify file/console sinks, rolling bounds/private permissions, ZIP contents, real FFmpeg live-session password absence, actual dispatcher error dialog/cleanup.

## Packaging and releases

Self-contained installers (Windows MSIX, Linux AppImage, macOS DMG for Apple Silicon + Intel) built by `scripts/package-*.{ps1,sh}`, verified in CI, published as draft GitHub Release on `v*` tag push. See [`docs/packaging.md`](docs/packaging.md) for formats, Linux prereqs, signing secrets, release procedure.

## Conventions

- **MVVM** with [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/).
  Bindings compiled by default, so views declare `x:DataType`.
- **Dependency injection, configuration and logging** via `Microsoft.Extensions.Hosting`. Each lib exposes `AddStation<Layer>()` registration extension; app wires them in `AppHost`.
- **Central package management**: versions in `Directory.Packages.props`.
- **Strict builds**: nullable reference types, recommended .NET analyzers, code style enforced at build (`.editorconfig`), warnings as errors.

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`) restores, builds, tests solution on `windows-latest`, `ubuntu-latest` and `macos-latest` for every push to `main` + every PR. UI tests use Avalonia Headless, no display server needed.

CI also exercises real OS credential stores with unique test entries deleted after (isolated keyring/keychain on Linux/macOS). Locally, those integration tests opt-in:

```bash
TC_TEST_OS_SECRETS=1 dotnet test
```

For optional real Icecast handshake test, start local Icecast with source username `source` + password `tc-test-source`, set `TC_TEST_ICECAST_PORT` to its port when running `dotnet test`. Uses unique temp mount; tests accepted creds, wrong creds, occupied mount, release after disconnect.

Independent restart test launches own loopback-only Icecast, stops/starts that exact server, verifies auto recovery with decodable listener audio, checks wrong creds terminal. Doesn't restart shared `TC_TEST_ICECAST_PORT` instance. Ubuntu CI runs it with `TC_TEST_ICECAST_EXECUTABLE=/usr/bin/icecast2`. Locally, opt in with native executable or existing Docker image:

```bash
TC_TEST_ICECAST_DOCKER_IMAGE=tropicast-icecast dotnet test \
  tests/Tropicast.Station.Encoding.Tests -c Release \
  --filter FullyQualifiedName~ReconnectIntegrationTests
# Native Linux alternative:
TC_TEST_ICECAST_EXECUTABLE=/usr/bin/icecast2 dotnet test \
  tests/Tropicast.Station.Encoding.Tests -c Release \
  --filter FullyQualifiedName~ReconnectIntegrationTests
```

Test uses known test-only passwords, synthetic tones, unique private container/process, ephemeral host port; removes own resources after, never records mics or restarts production server.

Same opt-in server harness verifies stream metadata against `status-json.xsl`, then decodes listener MP3 to check saved sample rate + channel count. Use `--filter FullyQualifiedName~Real_Tropicast_status` with either server env var above to run that acceptance check alone.

### End-to-end suite (Tropicast container)

`tests/Tropicast.Station.E2E.Tests` proves whole pipeline against real Tropicast Icecast image (built from `tests/Tropicast.Station.E2E.Tests/docker`): synthetic tone capture, bundled FFmpeg encoder, source handshake, listener whose MP3 decoded + checked for non-silent audio. Each test starts own container on ephemeral loopback port with fresh random creds, removes it after. Scenarios: go-live + stop, MP3 and Opus from one capture (both listeners decoded), unattended recovery after server restart, wrong password, mount already in use (first source keeps playing), unreachable host.

```bash
bash scripts/build-ffmpeg.sh linux-x64   # once
bash scripts/e2e.sh                      # builds the image, then runs the suite
```

Needs Docker. Without `TC_E2E_IMAGE` suite skips, so plain `dotnet test` stays fast; with `TC_E2E_REQUIRED=1` (set by `scripts/e2e.sh` and CI) missing image or FFmpeg bundle fails run. CI runs it in separate Ubuntu "End-to-end" job; failures name expected condition + broadcast's last state and message.

## License

Copyright 2026 Tropicast. Licensed under the [Apache License, Version 2.0](LICENSE);
see [NOTICE](NOTICE). The license does not grant use of the Tropicast name or
logo (section 6).

## Responsible use

- The software is provided "AS IS", without warranties or conditions of any
  kind. The authors and Tropicast are not liable for any damages or claims
  arising from its use (Apache License 2.0, sections 7 and 8).
- You are responsible for what you broadcast or host with it: rights and
  royalties for music and other content, and the broadcasting, privacy and
  other laws that apply to you and your listeners.
- Do not use it to distribute content you have no right to distribute, or for
  any unlawful purpose.
