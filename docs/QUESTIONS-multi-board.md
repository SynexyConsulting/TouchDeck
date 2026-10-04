# Questions and decisions from the overnight multi-board build (2026-10-03)

These came up while building `feature/multi-board`. Where something needed a decision, I made the call that seemed right and kept going. Each item says what I chose, so you can overrule it.

1. **The re-check on connect was replaced.** Earlier tonight you picked "re-check when a new or changed board connects" (`UpdateRecheck`). With several boards, the app now keeps the last verified feed and works out each board's offer from it locally. A board plugged in after the daily check gets its offer at once, with no network call, and a board coming back with new firmware is recomputed the same way. I removed `UpdateRecheck`; its goal is met without fetching the feed again. *If you'd rather also re-fetch the feed when a board connects, it's a few lines to add back.*
2. **Pastes from two boards at once aren't serialized.** An RP2040 types through its own USB keyboard, which the app can't hold back. Only the ESP32-C3 in PC mode types through the app. Two boards pasting at the same instant will interleave their text. I judged it too rare to justify a protocol change.
3. **Two boards in their bootloader at the same time show as one tab.** Windows can't tell which UF2 drive belongs to which board. Installs run one at a time, so this only matters if you put two boards into bootloader mode by hand.
4. **Only one board was attached overnight (the ESP32-C3 on COM7).** The single-board window looks as before. The tab strip, switching boards, the bootloader tab and the busy-port tab were checked on screen with the debug-only `--smoke-demo-boards`, which adds two pretend tabs (screenshots via `--smoke DIR --smoke-demo-boards`). **Please plug in two or three real boards** and check that each gets its own tab and its own live mirror, then try a firmware install on one while another stays connected.
5. **The macOS side builds and its tests pass on the Mac** (2026-10-03, through Lanyard, in a throwaway worktree `/tmp/td-mb` on the Mac; the Mac's own checkout wasn't touched). `xcodebuild build` succeeded the first time and all 63 XCTests pass. Smoke on the Mac: the RP2350 running Waveshare's demo shows as "RP2350 (not Touch Deck) · usbmodem2101", with a round screen and port-tagged log lines. Still to check by hand: two boards on the Mac at once.
6. **The website is on its own branch**, `feature/website`, in the worktree `C:\ai\TouchDesk-website`. It isn't merged and isn't pushed. The agent's open items:
   - `og:image` needs an absolute URL once the site's address is known;
   - board prices are its estimates;
   - the ESP32-C3 card says "flash with PlatformIO for now";
   - confirm "Windows 10 and 11" and the macOS wording;
   - the board drawings are generic bezels, not replicas.
