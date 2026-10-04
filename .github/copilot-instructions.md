# Tropicast Station

Cross-platform Avalonia desktop broadcaster on .NET 10. Target `net10.0`; nullable and warnings-as-errors are enabled.

## Work efficiently

- Read only files needed for the task. Do not scan `artifacts/`, generated `bin/` or `obj/`, vendored native sources, or large README sections unless relevant.
- Start with the owning project and its matching test project. Search narrowly by symbol or path before broad repository searches.
- Keep replies concise: state result, changed files, and validation. Do not restate the request or narrate routine tool calls.
- Make the smallest complete change. Do not refactor unrelated code, alter package versions, or regenerate native artifacts without an explicit request.
- Preserve existing worktree changes. Never discard or overwrite unrelated edits.

## Architecture

Dependencies point inward: `App` → `Audio` / `Encoding` / `Infrastructure` → `Core`.

- `App`: Avalonia views, view models, and DI composition root (`AppHost`).
- `Core`: domain models and shared services; no UI or platform dependencies.
- `Audio`: platform-neutral capture contracts, lifecycle service, PCM conversion, and demo provider.
- `Audio.Windows`, `Audio.Linux`, `Audio.MacOS`: OS-specific adapters only.
- `Encoding`: FFmpeg lifecycle and Icecast publishing.
- `Infrastructure`: profile persistence, OS secrets, and connection testing.

Never make a lower-level project depend on `App` or a platform adapter. Register platform adapters before shared audio services and use `TryAdd` so explicit registrations win.

## Safety and behavior

- Audio capture is single-consumer. Keep frame ownership, bounded queues, cancellation, disposal, and device-loss behavior explicit.
- Never silently switch devices, discard overruns, expose credentials, or add plaintext credential fallback.
- Keep UI work off capture threads; marshal UI updates through Avalonia's dispatcher.
- Preserve platform boundaries. Test platform-specific behavior in its matching test project.

## Style and validation

- Follow `.editorconfig`: file-scoped namespaces, underscore-prefixed private fields, 4-space C# indentation, and `var` for apparent types.
- Add or update focused xUnit tests for behavior changes. Match existing test naming and structure.
- Use central package management in `Directory.Packages.props`; do not put package versions in individual projects.
- Default checks: `dotnet build Tropicast.Station.slnx` and targeted `dotnet test <project>`. Use `-c Release --no-build` only after a matching Release build.
- Native FFmpeg bundles are needed when `TC_REQUIRE_FFMPEG=1`. Do not run full native packaging or end-to-end suites unless the change requires them.
