# Jiggler settings page: design

Status: approved in chat 2026-09-30 ("yes, go ahead with the spec and plan").

## Why

The jiggler's periodic "menu event" does: right-click, wait a random 0.8-2 s, ESC, then switch letter. It works on every board; Windows receives it, as confirmed by a low-level hook on the RP2040 1.6.0/1.7.0 and on the ESP32 over BT and PC mode. Two problems remain:
- When the right-click lands where no context menu opens, the **ESC goes to whatever window has focus**, which can cancel or close the user's work.
- The menu is open too briefly to notice.

The owner wants the behaviour configurable on the device and in the app.

## Behaviour

Each menu event (still every 45-150 s while the jiggler runs):
1. The mouse stops (as today).
2. If **Context menu = ON**: right-click, then hold for **Menu open** seconds (fixed, no longer random).
3. Press and release the **key**: ESC or F15. With the context menu OFF the key is still sent: it is the activity "tap".
4. Wait **Pause** seconds, still stopped.
5. Switch to a new letter and resume.

| Setting | Values | Default |
|---|---|---|
| Context menu | ON / OFF | ON |
| Key | ESC / F15 | ESC |
| Menu open | 0-60 s, 1 s steps (dimmed and ignored when Context menu is OFF) | 2 s |
| Pause before the next letter | 0-60 s, 1 s steps | 0 s |

`JIG MENU` (tests) runs the event now, with these settings. A paste waiting for the jiggler still waits until it is back to moving.

## Device panel: "Jiggler menu"

Changed after the first on-device check, at the owner's request: the settings are a **panel**, not a swipe page.
- The page lists are unchanged:
  - **1.69:** Watch, Clipboard, Jiggler.
  - **RP2350:** Clipboard, Jiggler.
  - **ESP32:** Clipboard, Jiggler, Settings.
- **Open:** a **cog** at the Jiggler page's lower left opens the "Jiggler menu" panel over it.
- **Close:** an **X** left of the title closes it, and so does a right swipe, like Settings > Bluetooth.
- The board reports the open panel as `ui_state_t.sub = UI_SUB_JIGSET` (2), so the app mirror shows it; DBG says `jset=`.
- The jiggler's dot animation doesn't draw over the panel.
- **Rows:**
  - Context menu ON/OFF pill;
  - Key segmented ESC | F15;
  - Menu open: − value +;
  - Pause: − value +.
- A hint line underneath summarises the sequence.
- When Context menu is OFF, the Menu open row is dimmed and its −/+ do nothing.
- Each tap changes the value at once. RP saves are debounced (~1 s after the last tap); the 1.69 clicks on each tap.
- **The 1.69's touch panel squeezes its top band:** a finger on the X, drawn at y 42, reports y ≈ 2 (measured). So targets near the top (the X, the trash, the jiggler's pills) take taps up to the top edge.

## Persistence

- **RP boards:** the `settings.c` record grows to v4 (`TDK4`): `jig_menu_on`, `jig_key`, `jig_open_s`, `jig_pause_s`. The v3/v2/v1 readers stay, so older records load with the defaults.
- **ESP32-C3:** `Preferences` keys `jmenu`, `jkey`, `jopen`, `jpause`.
- Values are clamped on load and on every change.

## Protocol (`usb_io.c`, `link.cpp`, app `Protocol/`)

- **New command:** `JIG CFG <menu 0|1> <key 0=ESC|1=F15> <open 0-60> <pause 0-60>`. It sets all four, validates (out of range → ignored), saves, and redraws.
- **STATE:** appends `jmenu= jkey= jopen= jpause=`.
- **DBG:** gains the same fields, for tests.
- **`ui_state_t`:** appends `jig_menu_on, jig_key, jig_open_s, jig_pause_s` (int32). The size goes 1248 → 1264. The C#, Python and size tests are updated.

## F15 on every output path

- **RP boards:** TinyUSB's keyboard report descriptor covers usages 0-255, so `HID_KEY_F15` (0x6A) works.
- **ESP32-C3 Bluetooth:** its report map limits keys to 0x65, so it widens to 0x73 (F24).
  - A bonded Windows host may have cached the old report map. If F15 doesn't arrive after the update, remove and re-pair the board.
  - This will be verified on hardware when the ESP32 is connected; until then it's build- and host-tested only.
- **App PC mode (ESP32 → app):** the `Injector` maps usage 0x6A to set-1 scancode 0x66. Windows should report it as VK_F15 (0x7E); to be verified with a low-level hook.

## App

- **The mirror:** shows and taps the new page automatically (`hostui` builds the boards' page code). `UiModel.PageCount` becomes 4 / 4 / 3 (RP2040 rect / ESP32 round / RP2350 round).
- **Settings dialog:** gains a **Jiggler** section with the same four controls.
  - It's enabled while a board with firmware 1.8.0+ is connected (STATE has `jmenu`). Otherwise it's dimmed, with "Connect a board with firmware 1.8.0 or later".
  - Changes send `JIG CFG`. The displayed values always come from the board's STATE, so the board is the source of truth.
- **Activity log:** "Jiggler: menu ON, ESC, open 2 s, pause 0 s" when changed from the app.

## Versions

Firmware 1.8.0 on all three boards. This branch is stacked on `feature/rp2350-round`.

## Tests

- **Host:**
  - the jiggler event timeline per setting, in a host-compiled state machine test like `test_jigpaths.py`: ON/ESC/open=2/pause=0; OFF/F15; pause=5; open=0;
  - settings v3 → v4 migration;
  - `JIG CFG` validation;
  - page renders: dimmed row, 4/3 page dots, pixel-identical host vs board;
  - label fit;
  - injector F15.
- **Board:**
  - `JIG CFG` round-trip through DBG/STATE on each connected board;
  - `TAP` on each control changes the value;
  - a hook-logged event run (with the hook swallowing the right-click and key) showing the configured gaps;
  - settings survive a UF2 install.
- **App:** the Settings section sends `JIG CFG`, follows STATE, and is dimmed for old firmware.
