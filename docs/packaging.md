# Packaging and releases

Tropicast Station ships as self-contained builds (no .NET install needed) that bundle the
source-built LGPL FFmpeg, its corresponding sources and full license texts, and
[`THIRD_PARTY_NOTICES`](../THIRD_PARTY_NOTICES).

| Platform | Package | Script | Runtime ID |
|----------|---------|--------|------------|
| Windows 10 1809+ | `Tropicast-Station-<v>-win-x64.msix` | `scripts/package-windows.ps1` | `win-x64` |
| Linux x86-64 | `Tropicast-Station-<v>-x86_64.AppImage` | `scripts/package-linux.sh` | `linux-x64` |
| macOS 13+ Apple Silicon | `Tropicast-Station-<v>-osx-arm64.dmg` | `scripts/package-macos.sh osx-arm64` | `osx-arm64` |
| macOS 13+ Intel | `Tropicast-Station-<v>-osx-x64.dmg` | `scripts/package-macos.sh osx-x64` | `osx-x64` |

`win-arm64` and `linux-arm64` are not built yet: they need their own FFmpeg bundles and CI runners.
Flatpak is not provided.

Every package needs the FFmpeg bundle first (`scripts/build-ffmpeg.sh <rid>`, output in
`artifacts/ffmpeg/<rid>`). Packages are written to `artifacts/package/`.

## Runtime prerequisites for users

- **Windows / macOS:** none. Grant microphone / screen-recording (system audio) permission when asked.
- **Linux:** the AppImage bundles .NET and FFmpeg but not the system audio and credential tools:
  `pactl` and `parec` (`pulseaudio-utils`, or `libpulse` on Arch) with a PulseAudio or PipeWire-Pulse
  server, and `secret-tool` (`libsecret-tools`) with an unlocked Secret Service keyring.
  Make the file executable (`chmod +x`) and run it. If FUSE is missing, run it with
  `--appimage-extract-and-run`.

## Building locally

```bash
bash scripts/build-ffmpeg.sh linux-x64 && TC_VERSION=0.1.0 bash scripts/package-linux.sh
bash scripts/build-ffmpeg.sh osx-arm64 && TC_VERSION=0.1.0 bash scripts/package-macos.sh osx-arm64   # on macOS
```

```powershell
bash scripts/build-ffmpeg.sh win-x64   # from an MSYS2 MINGW64 shell
./scripts/package-windows.ps1 -Version 0.1.0 -TestSign   # throw-away certificate for local install tests
```

Without signing configuration the packages are still produced, but:

- **MSIX cannot be installed unsigned.** `-TestSign` creates a self-signed certificate and exports
  `tropicast-test-signing.cer`; import it into `LocalMachine\TrustedPeople` (administrator) before
  `Add-AppxPackage`. Do not distribute test-signed packages.
- **macOS images are ad-hoc signed and not notarized.** Gatekeeper requires manual approval.
- **AppImage** needs no signing.

## Continuous integration

`ci.yml` packages on every platform for each PR and push: it installs and launches the MSIX
(test-signed) on Windows, launches the AppImage under Xvfb on Linux, and mounts and verifies both
DMGs on macOS. Packages are uploaded as 7-day workflow artifacts. This proves the packages build,
install and start on hosted runners; it does **not** replace manual verification on clean physical
machines or of audio capture hardware.

## Release workflow

`release.yml` runs when a tag matching `v<major>.<minor>.<patch>[-prerelease]` is pushed. The tag's
commit must be on `main`. It builds all packages, signs them if secrets exist, writes
`SHA256SUMS.txt`, and creates a **draft** GitHub Release (marked pre-release for `-` tags). Release
notes are the matching `## [x.y.z]` section of [`CHANGELOG.md`](../CHANGELOG.md) (pre-release tags use
their base version; a warning is printed if it is missing) followed by GitHub's generated list of
merged pull requests, grouped by label (`.github/release.yml`; label a PR `skip-changelog` to omit
it). Before tagging, rename `Unreleased` in `CHANGELOG.md` to the new version. Review the draft and
confirm the signing state before publishing.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

MSIX versions are `major.minor.patch.0`; the pre-release suffix only appears in the release title.

## Code-signing secrets

Configure these repository (or environment) secrets. Missing secrets never fail the build; the
affected package is unsigned and the workflow prints a warning.

| Secret | Purpose |
|--------|---------|
| `WINDOWS_PFX_BASE64` | Base64 of the code-signing `.pfx` (`base64 -w0 cert.pfx`) |
| `WINDOWS_PFX_PASSWORD` | Password of that `.pfx` |
| `WINDOWS_MSIX_PUBLISHER` | Certificate subject exactly as in the manifest, e.g. `CN=Tropicast, O=Tropicast, C=FR`. MSIX signing fails if it differs. |
| `MACOS_CERTIFICATE_BASE64` | Base64 of the **Developer ID Application** certificate exported as `.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | Password of the `.p12` |
| `MACOS_SIGNING_IDENTITY` | e.g. `Developer ID Application: Tropicast (TEAMID)` |
| `APPLE_ID` | Apple ID used for notarization |
| `APPLE_TEAM_ID` | Apple Developer Team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific password for that Apple ID (never the account password) |

Notes:

- Certificates are imported into a temporary runner keychain or file that is deleted or discarded
  with the runner. Secrets are never echoed.
- macOS signing uses the hardened runtime with `packaging/macos/entitlements.plist` (JIT, unsigned
  executable memory and library validation exceptions required by .NET, plus audio input). The app
  and then the DMG are notarized and stapled only when all notarization secrets exist.
- For a trusted Windows install without a commercial certificate, publish the `.cer` and have users
  trust it, or use a certificate chain trusted on the target machines.

## License obligations

FFmpeg/LAME/OpenSSL are redistributed under the licenses in `THIRD_PARTY_NOTICES`. Each package
contains the notices, the corresponding source archives, full license texts and the build script
(in `ffmpeg/` next to the app; Windows and Linux packages also carry the root notices file, and the
DMG carries it beside the app). The application's own license is not yet decided; a distribution
review is required before shipping publicly.

## Not verified yet

- Installing and launching on **clean** Windows, Linux and macOS machines.
- Real Developer ID signing, notarization, and a trusted Windows certificate (need your credentials).
- Physical audio capture in packaged builds (MSIX file-system virtualization of the log folder and
  macOS permission prompts should be checked by hand).
