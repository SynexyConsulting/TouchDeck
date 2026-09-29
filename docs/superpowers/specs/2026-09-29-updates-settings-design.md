# App updates, release pipeline, Settings dialog: design

Status: approved in conversation on 2026-09-29. Settings style A was chosen from the brainstorm mockups.
Scope: the Windows app (1.2.0) plus the repo's release tooling. Firmware stays at 1.6.0.

## Goal

Once a change is built, pushing a tag publishes it, and the installed app finds it, checks it and installs it. App and firmware update independently. A Settings dialog holds the versions, startup options and updates. The Python helper is retired, because the app replaces it.

## 1. Retire the Python helper

- Delete `tools/clip_helper.py`, `tools/inject.py`, `tools/tests/test_helper.py` and `tools/tests/test_inject.py`.
- Keep `touchdeck.py`, `flash.py`, `perf_rp2040.py`, `fontgen.py`, `jigpaths.py` and the board tests.
- Messages and docs that said "stop clip_helper.py" now say "quit the Touch Deck app".
- The app's comments that cite the Python original keep the reference as history ("ported from the former …").

## 2. Update feed (`updates.json`)

It lives in the public repo `SynexyConsulting/TouchDeckUpdates`. The app reads it from `https://github.com/SynexyConsulting/TouchDeckUpdates/releases/latest/download/updates.json`, a fixed URL that GitHub redirects to the newest release.

```json
{
  "schema": 1,
  "published": "2026-09-29T08:00:00Z",
  "app": {
    "windows": { "version": "1.2.0", "url": "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/app-v1.2.0/TouchDeck-1.2.0.msi",
                 "sha256": "<64 hex>", "size": 56000000 }
  },
  "firmware": [
    { "board": "rp2040-169", "version": "1.6.0", "url": "https://github.com/.../releases/download/app-v1.2.0/rp2040-169-1.6.0.uf2",
      "sha256": "<64 hex>", "size": 350000 }
  ]
}
```

- Every release carries a **complete** feed: the newest app per OS and the newest firmware per board. That's why "latest" is always enough.
- `app.macos` is reserved for the Mac app. Unknown keys are ignored.

## 3. Security rules (the app enforces these; tests pin each one)

- **URLs:**
  - Feed and asset URLs must be `https://github.com/SynexyConsulting/TouchDeckUpdates/releases/…`.
  - Redirects are followed only to `https` hosts under `github.com` / `githubusercontent.com`.
  - Anything else is rejected before any download.
  - Test mode: a loopback `http://127.0.0.1` / `localhost` feed is allowed only when passed with `--update-feed`. The same loopback base then applies to its assets.
- **Checksums:** `sha256` is required, 64 hex characters. Downloads are hashed while streaming, and a mismatch deletes the file and fails the update.
- **Size:** `size` must be positive and at most 200 MB for the app and 4 MB for firmware. The download stops at the declared size.
- **Where downloads go:** a per-user folder, `%LOCALAPPDATA%\TouchDeck\Updates`.
  - File names are fixed by the app (`TouchDeck-update.msi`, `<board>-update.uf2`) and never taken from the feed.
  - The folder is cleared before each download.
- **Firmware:** a downloaded UF2 must pass the existing RP2040 UF2 validation before it's flashed. The feed's `board` must match the connected board's `VER` board ID.
- **Install:** the MSI runs as `msiexec /i "<path>" /passive /norestart`.
  - Arguments are built by the app, never from feed text, and the process is started with an argument list rather than a shell string.
  - The per-user MSI needs no elevation.
  - After it finishes, a detached launcher starts the installed `TouchDeck.exe` again.
- **Versions:** only strictly newer versions are offered. Downgrades never happen, and the MSI itself also refuses them.
- **Network:** HTTP timeout 20 s; failures show as text and never throw into the UI; the check runs off the UI thread. `User-Agent: TouchDeck/<version>`.
- **Signed feed (added after the security review):**
  - `updates.json.sig` holds an ECDSA P-256 signature over the feed.
  - The app pins the public key and refuses unsigned or wrongly signed feeds.
  - The private key stays off the repo: on the maintainer's PC and in the protected `release` environment.
  - Publishing needs a reviewer's approval.
  - Test switches exist only in `-UpdateTestHooks` builds.
  - Downloads have idle timeouts, and the feed has an overall timeout.
- **Unsigned artefacts, for now:** Windows SmartScreen may warn when the MSI is run by hand. The in-app update goes through `msiexec`, which doesn't show SmartScreen. Code signing (Azure Trusted Signing) is a follow-up. Until then the SHA-256 in the HTTPS feed is the integrity check.

## 4. Release pipeline (`.github/workflows/release.yml`, private repo)

- **Triggers:**
  - tag `app-v*`: builds firmware and app and publishes the MSI plus the bundled RP2040 UF2;
  - tag `fw-v*`: builds and publishes the firmware only;
  - `workflow_dispatch` for a dry run that uploads artefacts only.
- **Jobs:**
  - `firmware` on `ubuntu-latest`: Pico SDK 2.1.1, `gcc-arm-none-eabi`, cmake/ninja → `watch.uf2`; PlatformIO → ESP32 `firmware.bin`, kept as an artefact but not published until it has been run on hardware.
  - `app` on `windows-latest`, needs `firmware`: puts the UF2 into `windows-app/firmware`, runs `dotnet test` and the host C tests (MSVC is on the runner; the board tests skip), `build.ps1` → MSI.
  - `publish`: `tools/publish_release.py` creates the release in the public repo, uploads the files, builds `updates.json` by merging the previous latest feed (`tools/make_updates.py`), and uploads it.
- **Version check:** the tag must equal `FW_VERSION` (`fw-v`) or `<Version>` (`app-v`), or the job fails.
- **Secret:** `TOUCHDECK_UPDATES_TOKEN`, a fine-grained PAT with **Contents: read and write** on `TouchDeckUpdates` only. The workflow never uses it for the private repo.
- **Publishing by hand:** the same `publish_release.py` can run locally with a token in the environment. This first 1.2.0 release is published that way.

## 5. Settings dialog (style A)

- **Opening it:** a cog button in the main window header opens a modal card while the window behind is dimmed. It closes with ✕, Esc, or a click outside.
- **Versions card:**
  - "App Version 1.2.0";
  - "Device Firmware 1.6.0 (rp2040-169)", or, with no board, the label dimmed and no version.
- **Startup:**
  - Launch at startup (HKCU Run);
  - Start minimized in tray (indented; disabled when Launch at startup is off). The Run command gets `--minimized` only when it's on.
- **Updates:**
  - Check for updates automatically (default on): once 15 s after start, then every 24 h.
  - **Check for updates now.**
  - Result lines for App and Firmware: "Up to date", "1.3.0 available [Install]", "No updates published yet", or an error.
  - App Install: download, verify, then the installer takes over and the app restarts on the new version.
  - Firmware Install: download, verify, then the existing flash flow, with progress in the result line.
- **Advanced:** Dry run, Board diagnostics, and the Ctrl+Alt+C hint.
- **Main window:**
  - loses the Settings card, the version next to the app name, and the footer version;
  - the device card keeps its firmware line and its Install/Reinstall button, which use the bundled firmware.
- **New settings:** `CheckForUpdates` (true), `StartMinimized` (true), `LastUpdateCheck` (UTC, nullable).

## 6. Testing

- **C# unit tests:**
  - feed parsing and validation (each security rule);
  - version selection (newer only, per OS, per board);
  - download against a loopback server: good hash, bad hash (file deleted), oversize, redirect to a foreign host rejected;
  - settings round-trip with the new fields;
  - the autostart command with and without `--minimized`.
- **pytest:** `make_updates.py` merging (the app replaces the app entry, firmware replaces per board, the rest is kept), `sha256`/`size` computed.
- **End to end on this PC:**
  - build and install 1.2.0, then build 1.2.1;
  - serve `updates.json` and the MSI from a loopback server;
  - run the installed app with `--update-feed <url> --smoke-update <dir>`: it checks, installs, and the installed version becomes 1.2.1;
  - then reinstall 1.2.0 (same-version reinstall) so the machine matches the published release.
- **Security review:** a fresh reviewer, focused on the updater and pipeline.
