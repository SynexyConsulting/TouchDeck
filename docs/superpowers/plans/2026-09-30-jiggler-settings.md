# Jiggler Settings Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each board gets a "Jiggler settings" page. Context menu ON/OFF, key ESC/F15, menu-open time 0-60 s and pause before the next letter 0-60 s are saved on the board, mirrored and editable in the app, and drive the jiggler's menu event.

**Architecture:**
- The menu event becomes a pure, shared C state machine, `jig_menu.c` (identical in `src/` and `esp32c3/src/`, host-tested through ctypes like `jig_motion.c`). `jiggler.c`/`jiggler.cpp` only send the action it returns.
- Settings live in the board: RP flash record v4, ESP Preferences.
- They travel as `JIG CFG` (app → board) and new STATE/`ui_state_t` fields (board → app).
- The page is drawn by the shared page code, so the app's mirror gets it for free.

**Tech Stack:** Pico SDK 2.1.1 C, Arduino-ESP32/NimBLE C++, C#/.NET 8 WPF, pytest + MSVC host tests, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-jiggler-settings-design.md`

## Global Constraints

- **Defaults:** Context menu ON, key ESC, open 2 s, pause 0 s. Ranges 0-60 s, 1 s steps.
- **Event order:** stop → [right-click, hold `open`] → key down/up → wait `pause` (+300 ms settle) → switch letter → move.
- **Phase numbers:** `JIG_*` values in `ui_state.h` keep their numbers, so the STATE `phase=` meaning is stable. `JIG_ESC_DOWN` now means "key down" (ESC or F15).
- **Shared files** (`jig_menu.*`, `ui_state.h`, `ui_sync.*`, and `src/round/*` vs `esp32c3/src/*`) stay byte-identical, and pytest enforces it.
- **No regressions:** the RP2040 1.69 and the RP2350 must pass their existing board tests, and the existing jiggler behaviour must match the defaults (right-click, hold, ESC).
- **Versions:** firmware 1.8.0 in both `version.h`.
- **Hardware tests that fire the menu** must use the hook that swallows the right-click and the key, or they interrupt the focused window (lesson from 2026-09-30).
- **Backslashes** in source go through Edit/Write or a script file, never a heredoc.

## Review Focus

1. **Settings changed mid-event:** `JIG CFG` or a tap while a menu is open must not leave the right button or a key held, and must not skip the key. The new values apply from the next event.
2. **`open=0` with the menu ON:** right-click down/up, then the key immediately. That's legal and the timing test covers it.
3. **Turning the jiggler off mid-event** still releases everything: the existing release logic must cover the new phases.
4. **Old firmware or old app:** an app 1.2.x reading 1.8.0 STATE ignores the extra fields. The new app with 1.7.0 firmware dims the section. A v3 settings record loads as defaults.
5. **ESP32 Bluetooth F15:** report-map change plus a possibly cached map on the bonded host. Document the re-pair, and fall back to ESC behaviour if F15 is selected while BT is up but not delivering (can't detect, so documentation only).

---

### Task 1: Shared menu-event engine `jig_menu.c` (host-tested)

**Files:**
- Create `src/jig_menu.h` and `src/jig_menu.c`, copied byte-identical to `esp32c3/src/`.
- Modify `tools/tests/jig_host.py` (compile `jig_menu.c` into the host DLL) and `tools/tests/test_jigpaths.py` (identical-in-both-trees list).
- Create `tools/tests/test_jig_menu.py`.

**Interfaces (produces):**
```c
typedef struct { uint8_t menu_on, key_f15, open_s, pause_s; } jig_cfg_t;   // clamped by jmenu_clamp
enum { JM_NONE, JM_RIGHT_DOWN, JM_RIGHT_UP, JM_KEY_DOWN, JM_KEY_UP, JM_SWITCH };
typedef struct { uint8_t action; uint8_t next_phase; uint32_t wait_ms; } jm_step_t;
#define JM_KEY_ESC 0x29
#define JM_KEY_F15 0x6A
void jmenu_clamp(jig_cfg_t *c);              // open/pause to 0..60, flags to 0/1
jm_step_t jmenu_step(int phase, const jig_cfg_t *c, uint32_t rnd);  // phase is JIG_STOP..JIG_RESUME
uint8_t jmenu_key(const jig_cfg_t *c);       // JM_KEY_ESC or JM_KEY_F15
```

**Behaviour:**

| Phase | Menu ON | Menu OFF |
|---|---|---|
| `JIG_STOP` | `JM_RIGHT_DOWN`, next `JIG_CLICK_DOWN`, 60-120 ms | `JM_KEY_DOWN`, next `JIG_ESC_DOWN`, 50-90 ms |
| `JIG_CLICK_DOWN` | `JM_RIGHT_UP`, next `JIG_MENU_OPEN`, `open_s*1000` ms | — |
| `JIG_MENU_OPEN` | `JM_KEY_DOWN`, next `JIG_ESC_DOWN`, 50-90 ms | — |
| `JIG_ESC_DOWN` | `JM_KEY_UP`, next `JIG_RESUME`, `pause_s*1000 + 300` ms | same |
| `JIG_RESUME` | `JM_SWITCH`, next `JIG_MOVING`, 0 | same |

- [ ] **Step 1:** Write `test_jig_menu.py` (ctypes over the host DLL). It walks the event from `JIG_STOP` and collects `(action, wait)` for:
  - defaults: RD, RU, wait 2000, KD, KU, wait 300, SWITCH;
  - menu OFF: KD, KU, SWITCH, no right actions;
  - pause=5: 5300 before SWITCH;
  - open=0: KD straight after RU;
  - clamp: 99→60, menu=7→1.
- [ ] **Step 2:** `python -m pytest tools/tests/test_jig_menu.py -q`. Expected: FAIL (no `jig_menu.c`).
- [ ] **Step 3:** Implement `jig_menu.c/.h`, copy it to `esp32c3/src`, add both to the jig_host build and the identical-files test.
- [ ] **Step 4:** pytest. Expected: PASS.
- [ ] **Step 5:** Commit "Jiggler: shared, host-tested menu-event engine".

### Task 2: Firmware uses the engine; settings persist

**Files:**
- `src/app.h` and `esp32c3/src/app.h`: `jig_cfg_t jig_cfg`.
- `src/jiggler.c`, `esp32c3/src/jiggler.cpp`: the phases call `jmenu_step` and send the action (advance only when the send succeeds). The release-on-off logic covers right-button and key held. `JM_SWITCH` does today's RESUME work.
- `src/settings.c`: `MAGIC_V4 "TDK4"` record `{magic, muted, jig_on, jig_scale, menu_on, key_f15, open_s, pause_s, check}`, with v3/v2/v1 readers giving defaults for the new fields. Saves are debounced: `settings_save_soon()` writes about 1 s after the last change, from core0's loop.
- `esp32c3/src/main.cpp`: Preferences `jmenu`, `jkey`, `jopen`, `jpause`, loaded at boot with defaults.
- `CMakeLists.txt` (both RP targets) and PlatformIO (automatic) get `jig_menu.c`.

- [ ] **Step 1:** Build both RP targets and the ESP32. Flash the RP2350 and the 1.69. With the defaults, a hook-logged `JIG MENU` run (the swallowing hook) shows RD, RU, about 2 s, ESC down/up, then movement about 0.3 s later. Run `test_board_rp2040.py` and `test_board_rp2350.py`. Expected: all pass.
- [ ] **Step 2:** Commit "Jiggler: menu event from settings (defaults keep today's sequence, fixed 2 s open)".

### Task 3: Protocol `JIG CFG`, STATE/DBG fields, `ui_state_t` +16 bytes

**Files:**
- `src/ui_state.h` (both trees): append `int32_t jig_menu_on, jig_key, jig_open_s, jig_pause_s;`.
- `src/ui_sync.c` (both): STATE appends `jmenu= jkey= jopen= jpause=`.
- `src/ui.c` `ui_state_fill` and `esp32c3` ui fill: copy the cfg in.
- `src/usb_io.c`, `esp32c3/src/link.cpp`:
  - `JIG CFG m k o p`: parse four ints, reject out of range, set, clamp, save soon, redraw;
  - DBG gains `jmenu= jkey= jopen= jpause=`;
  - the protocol header comment documents both.
- **App:**
  - `Mirror/UiState.cs` gets the 4 fields, size 1264;
  - `MirrorState` key table, `BoardLine` and `StateReport` fields;
  - `DeviceSession.SetJigConfig(menu, key, open, pause)` queues `JIG CFG`;
  - `tools/tests/tdui_host.py` UiState gets the 4 fields.
- **Tests:**
  - Size tests 1248 → 1264 (C#, Python).
  - The state-line round trip includes the new fields.
  - A board test: `JIG CFG 0 1 7 3` → DBG `jmenu=0 jkey=1 jopen=7 jpause=3` and STATE has them; `JIG CFG 1 0 99 0` ignored; restore the defaults.

- [ ] Steps: tests first (size and round-trip RED), implement, host suites GREEN, board test GREEN on both RP boards, then commit "Protocol: JIG CFG and jiggler settings in STATE/ui_state".

### Task 4: Device pages (rect and round), touch, page counts

**Files:**
- `src/ui_pages.c`/`ui_pages.h` (rect): `SCR_JIGSET` after `SCR_JIG`, `draw_jigset`.
- `esp32c3/src/ui_pages.c`/`.h` (round): `SCR_JIGSET` after `SCR_JIG`, before `SCR_SETTINGS`; `UI_PAGE_COUNT` is 3 under `UI_USB_ONLY`. Copied to `src/round/`.
- `src/ui.h` (rect) and `esp32c3/src/ui.h` (round): `JS_*` geometry (rows, pills, −/+ hit boxes) shared by drawing and touch.
- `src/main.c`, `esp32c3/src/main.cpp`: `on_touch` for `SCR_JIGSET` (toggle, segment, −/+ with dimmed open when menu OFF), feedback click on the 1.69.
- `tools/tests/test_fontgen.py`: new labels ("Jiggler settings", "Context menu", "Key", "Menu open", "Pause", "ESC", "F15", "ON", "OFF", "60 s", "−", "+").
- `tools/tests/test_ui_host.py`: PAGES rp2040 4, esp32c3 4, rp2350 3. The page renders differently from the others, the dimmed row differs from the enabled one, and it stays inside the panel shape.
- ESP32 board tests: the Settings page moves from index 2 to 3 (update `goto(2)` → `goto(3)` where it means Settings).

- [ ] Steps:
  1. Host render tests first (RED).
  2. Implement the pages and geometry; render PNGs of both layouts.
  3. **Show the owner the PNG mockups before flashing.**
  4. Touch handling; board tests that `TAP` each control and read DBG.
  5. FBCRC mirror tests on both RP boards.
  6. Commit "Device: Jiggler settings page on every board".

### Task 5: F15 on every output path

**Files:**
- `esp32c3/src/ble_hid.cpp`: keyboard report map key range 0x65 → 0x73 (logical and usage maximum).
- `windows-app/src/TouchDeck.Core/Input/Injector.cs`: usage 0x6A → scancode 0x66.
- **Tests:**
  - xUnit `Injector` F15 → KeyStroke(0x66).
  - Hardware: with key=F15, a hook-logged `JIG MENU` on the RP2350 over USB shows vk 0x7E (VK_F15) down/up and no ESC.
  - Optional host check: SendInput scancode 0x66 → vk 0x7E.
  - The ESP32 BT check waits until it's connected; ledger it as pending.

- [ ] Steps: RED xUnit, implement, GREEN; hardware hook run; commit "F15 key on USB, Bluetooth and PC mode".

### Task 6: App Settings dialog: Jiggler section

**Files:** `SettingsWindow.xaml(.cs)`, `AppController.cs` (`JigCfg` properties from StateReport, `SetJigConfig`), `UiModel.PageCount` (4/4/3), and the `--smoke` `settings.png` showing the section.
- [ ] Steps:
  1. Core test: StateReport with jmenu parses into `JigConfig`; a report without it gives null, which means dimmed.
  2. Implement the UI: toggle, ESC/F15 pills, two −/+ steppers with the value, matching the existing style.
  3. Smoke PNG review.
  4. Commit.

### Task 7: Version 1.8.0 and docs

- [ ] `FW_VERSION "1.8.0"` in both `version.h`. Update the CLAUDE.md files (root: page lists, `JIG CFG`, settings v4, `jig_menu`; app: Jiggler section) and the README (the new page and settings). Commit.

### Task 8: Acceptance on hardware, final review

- [ ] **Step 1:** Flash 1.8.0 to the 1.69 and the RP2350. All their board and mirror tests pass. Settings survive a UF2 install on both (set non-defaults, reinstall, check DBG).
- [ ] **Step 2:** Hook-logged event runs (swallowing hook), each showing the configured order and gaps:
  - defaults;
  - menu OFF + F15;
  - open=5, pause=3.
- [ ] **Step 3:** The owner checks the page on both boards and in the app (mirror and Settings section).
- [ ] **Step 4:** Full suites: pytest, `dotnet test`, both RP builds and the ESP32 build, `build.ps1 -Smoke`.
- [ ] **Step 5:** Final review by a fresh reviewer, then the fix pass; restore the owner's settings; ledger.
- [ ] **Step 6:** ESP32-C3 hardware checks (page, BT F15 and re-pair note) when the board is connected, or ledger them as pending.
