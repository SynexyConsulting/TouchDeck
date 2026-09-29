# App Updates, Release Pipeline, Settings Dialog: Implementation Plan

> **For agentic workers:** executed inline (superpowers:executing-plans), TDD per task.

**Goal:** App 1.2.0 checks the public update feed, verifies what it downloads, installs app and firmware updates, and has a Settings dialog. A tag builds and publishes releases. The Python helper is gone.

**Spec:** `docs/superpowers/specs/2026-09-29-updates-settings-design.md`

## Global Constraints

- Feed URL: `https://github.com/SynexyConsulting/TouchDeckUpdates/releases/latest/download/updates.json`. Asset prefix: `https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/`.
- SHA-256 is required. Size limits are 200 MB (app) and 4 MB (firmware). Downloads go to `%LOCALAPPDATA%\TouchDeck\Updates`, under fixed names.
- `msiexec /i <path> /passive /norestart` is started through `ProcessStartInfo.ArgumentList`.
- App version 1.2.0. Firmware 1.6.0 (unchanged).
- Tags are `app-vX.Y.Z` / `fw-vX.Y.Z`. Secret: `TOUCHDECK_UPDATES_TOKEN`.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.

## Review Focus

- A feed pointing at another host, or at `http`, must be refused before any byte is fetched.
- A redirect from GitHub to a non-GitHub host must be refused.
- A download that is longer than declared, or whose hash is wrong, must leave no file behind.
- A firmware entry for another board must never be flashed.
- A network failure or malformed JSON must show text and never crash the app.

### Task 1: Retire the Python helper
- Delete the four files, fix `flash.py` and `perf_rp2040.py` messages, update the docs.
- Test: `python -m pytest tools/tests -q` is green.

### Task 2: Core update feed + checker + downloader
- Files: `TouchDeck.Core/Updates/UpdateFeed.cs` (records, parse, validate), `UpdateChecker.cs` (HTTP, selection), `UpdateDownloader.cs` (stream, hash, size, redirect policy).
- Tests (`UpdateTests.cs`): every Review Focus line, plus parsing and selection, against an `HttpListener` loopback server.

### Task 3: Settings model + autostart minimized
- `AppSettings` gains `CheckForUpdates`, `StartMinimized` and `LastUpdateCheck`. `Autostart.Set(enabled, exe, minimized)`.
- Tests: round-trip, the old settings file still loads, both command forms.

### Task 4: App updater service + installer launch
- `TouchDeck.App/Updater.cs`: auto/daily checks, install app (download → msiexec → relaunch → quit), install firmware (download → existing flash).
- `--update-feed <url>` (loopback only when set), and `--smoke-update <dir>` for the end-to-end run.

### Task 5: Settings dialog + main window cleanup
- `SettingsWindow.xaml` (style A), a cog in the header, a scrim. Remove the Settings card, the header version and the footer version.
- Check: smoke snapshots of the main window and the dialog.

### Task 6: Release tooling + CI
- `tools/make_updates.py` (feed build and merge, with tests), `tools/publish_release.py` (GitHub REST, token from env), `.github/workflows/release.yml`.
- Tests: pytest for `make_updates`. The YAML parses. `publish_release.py --dry-run`.

### Task 7: Version 1.2.0, end-to-end update test, docs, security review, PRs, publish
- `build.ps1` 1.2.0 and 1.2.1. Loopback end-to-end update 1.2.0 → 1.2.1, then reinstall 1.2.0. Install smoke.
- Security review subagent, then fix pass.
- Commit, push, PRs, then publish release `app-v1.2.0` to `TouchDeckUpdates`, and check the live feed with the installed app.
