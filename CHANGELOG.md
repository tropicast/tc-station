# Changelog

All notable user-facing changes. Format: [Keep a Changelog](https://keepachangelog.com/);
versions follow [Semantic Versioning](https://semver.org/).

When releasing, rename `Unreleased` to the version (for example `## [0.2.0] - 2026-11-01`) and add a
new empty `Unreleased` section. The release workflow publishes the matching section above the
auto-generated list of merged pull requests.

## [Unreleased]

### Added
- Packaging: Windows MSIX, Linux AppImage and macOS DMG (Apple Silicon and Intel) with a tag-driven release workflow.
- Credential-safe rolling diagnostics and a diagnostics export.
- Per-profile stream quality and public stream metadata.
- Automatic reconnect with exponential backoff.
- Audio level meters, clipping and silence warnings.
- Go Live / Stop broadcast workflow with tray status.
