# Several boards at once: design

Status: approved in conversation (2026-10-03, sections 1 and 2; the owner asked for it to be built overnight). Questions that came up while building are in `docs/QUESTIONS-multi-board.md`.

## What the owner asked for

- One tab or selector button per attached device. With one device the window looks as it does today, with no tabs.
- A board running Touch Deck firmware shows today's mirror and controls. A board without it shows today's install UI.
- The same on macOS. The owner compiles that side on the Mac.

Decisions made in conversation:
- **Every board is live at once.** Each keeps its own session; the tabs only choose what the window shows.
- **Tab label:** the model plus the port, e.g. `RP2040 1.69 · COM6`, with a status dot.
- **One activity log.** Board lines are tagged with their port. An **Only selected board** checkbox filters the log, and it appears only when two or more boards are attached.

## Core: one slot per port (`DeviceManager`)

- **`Tick()` scans every 2 s and reconciles one slot per port:**
  - A port without a running session gets a connection attempt (open, handshake), as today. The result is that slot's `LinkState`: Connected, PortBusy or NotResponding.
  - A slot whose session ended (unplugged or rebooted) publishes Searching, and the next scan reconnects it or removes it.
  - A port that's gone and has no running session is removed (`SlotRemoved`).
- **Events:** `SlotChanged(port, LinkState)`, `SlotRemoved(port)`, `SessionStarted(port, DeviceSession)`. All are raised on background threads.
- **Queries:** `Sessions` (live sessions) and `SessionFor(port)`.
- **`RequiredBoard` and `PreferredPort` go away.** Every board connects. An install recognises its board coming back as a *new* session reporting the model being installed (see Installs), so another board of either model can never be mistaken for it.

## New boards (no Touch Deck firmware)

- `NewBoards.Scan()` runs every 2 s, no longer only while nothing is connected. Its results become tabs:
  - a stock program is keyed by its COM port;
  - a board in its bootloader is keyed `boot:<chip>`, since a UF2 drive has no port.
- `FromEntities` now keeps one entry per (chip, state, port), so two stock boards make two tabs. Bootloader entries of the same chip still merge, because the drive can't say which board it belongs to.
- **While an install runs, the new-board scan is paused,** because the board being flashed passes through its bootloader.

## App: `BoardController` per tab, `AppController` app-wide

**`BoardController` (new)** holds one tab's state and actions, moved from `AppController`:
- the link state, name, port, firmware, health and status texts;
- the mirror (`MirrorState`, frame, renderer error, fallback text) and the legacy jiggler card fields;
- the Remote actions: Page, BOOT, Tap, Send, Clear clip and Bootloader;
- the jiggler settings and the board diagnostics;
- the bundled-firmware offer, the feed-firmware offer, and new-board onboarding (model picker, Install Touch Deck).

It owns its once-a-second mirror refresh, and it raises `PropertyChanged` and `MirrorFrameChanged` itself.

**`AppController` keeps:**
- settings, Dry run, Diagnostics, autostart;
- app updates, recent clips, notifications and the activity log;
- `Boards` (an `ObservableCollection<BoardController>`) and `Selected`.

**Selection rules:**
- A new board is selected only if it's the only one, or if its port is the last-used port (`settings.PreferredPort`, now only used for this).
- Removing the selected board selects the first remaining one.
- After an install, the board's new tab is selected.
- `Selected` is never null: with no boards, it's a placeholder controller (not in `Boards`) showing "No board" and "Connect a Touch Deck".

**Updates:**
- The update check keeps the last verified feed. Each board's feed-firmware offer is `UpdateSelector.Select(feed, app, its firmware)`, recomputed when the board connects or its firmware changes.
- That makes the per-connect re-check (`UpdateRecheck`, added the day before) unnecessary: a board plugged in after the daily check gets its offer at once from the cached feed, with no network call.
- Settings shows the Firmware line for the selected board.

**Installs:**
- One install at a time across the app (`AppController.Installing`). The other boards keep working.
- The board being installed keeps its tab while its port disappears and comes back.
- Success means a session that wasn't there when the install started now reports the target model.

**Logging:**
- `LogLines` holds `LogEntry(Time, Port?, Text)`; `ToString()` shows `HH:mm:ss  COM6: text`.
- The window shows a filtered view: everything, or the selected board's lines plus untagged app lines. **Copy log** copies what's shown.

**Tray:**
- **Icon:** green if any board is connected, red if any is busy or not responding, otherwise grey.
- **Tooltip:** one line per board.
- **Notifications:** they name the board when more than one is attached.

**Window:**
- **Tab strip:** an `ItemsControl` of pill buttons over `Boards`, visible at 2 or more. Each pill shows a status dot and the label, and the selected pill uses the amber Primary style.
- **The device column and the Remote card take `Selected` as their DataContext.**
- **Code-behind** that read `app.X` for board fields now reads `app.Selected.X`. It re-subscribes to the selected controller's events when the selection changes.

**Smoke:**
- `smoke.txt` reports the selected board plus `boards=N` and `board_ports=`.
- `--smoke-board PORT` picks the board for `--smoke-steps`.

**Not done (deliberately):** serializing pastes from two boards.
- An RP2040 types through its own USB keyboard, so the app can't hold it back.
- Only ESP32-C3 PC-mode keystrokes pass through the app.
- Two boards pasting at the same moment will interleave. That's a known limit, listed in the questions file.

## Errors

- **One board's failure is that tab's state** (PortBusy, NotResponding). It never affects the other sessions.
- **A slot's session ending always releases its held input** (`Injector.ReleaseAll` in `Run`'s finally, unchanged).
- **Late events from a removed board are ignored:** a `BoardController` is detached when its slot goes away, and `Post` checks that.
- **WMI failures** in either scan skip that round, as today.

## Testing

- **Core (`ManagerTests`):** two boards connect at once; unplugging one leaves the other connected; busy and silent ports become their own slots; a reappearing port reconnects; slot events come in order; `Sessions` and `SessionFor` are right.
- **Core (`OnboardingTests`):** two stock boards on different ports give two new-board entries.
- **App logic** that can be tested without WPF (selection rules, log filter, offer from a cached feed) goes in Core helpers with unit tests (`BoardSelection`, `LogEntry`).
- **Board tests:** the existing hardware tests keep passing with whatever boards are attached.
- **Smoke:** `--smoke` with the boards plugged in: the snapshot shows the tab strip (2 or more), and `smoke.txt` lists them.

## macOS

The same split, file for file:
- `DeviceManager.swift` gets slots;
- `BoardController.swift` is a new `@MainActor ObservableObject`;
- `AppController.swift` keeps the app-wide parts;
- `MainView.swift` gets the pill row.

The selected board's view observes its `BoardController` directly (`@ObservedObject`), because `@Published` on an array doesn't pass on changes inside its elements.

Mac port names are long (`/dev/cu.usbmodem1101`), so tabs and log tags show `usbmodem1101`.

It's written on Windows without a compiler, so expect compile fixes on the Mac (see the questions file).
