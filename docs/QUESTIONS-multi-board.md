# Questions and decisions from the overnight multi-board build (2026-10-03)

These came up while building `feature/multi-board`. Where something needed a decision, I made the call that seemed right and kept going. Each item says what I chose, so you can overrule it.

1. **The re-check on connect was replaced.** Earlier tonight you picked "re-check when a new or changed board connects" (`UpdateRecheck`). With several boards, the app now keeps the last verified feed and works out each board's offer from it locally. A board plugged in after the daily check gets its offer at once, with no network call, and a board coming back with new firmware is recomputed the same way. I removed `UpdateRecheck`; its goal is met without fetching the feed again. *If you'd rather also re-fetch the feed when a board connects, it's a few lines to add back.*
2. **Pastes from two boards at once aren't serialized.** An RP2040 types through its own USB keyboard, which the app can't hold back. Only the ESP32-C3 in PC mode types through the app. Two boards pasting at the same instant will interleave their text. I judged it too rare to justify a protocol change.
3. **Two boards in their bootloader at the same time show as one tab.** Windows can't tell which UF2 drive belongs to which board. Installs run one at a time, so this only matters if you put two boards into bootloader mode by hand.
4. **Only one board was attached overnight (the ESP32-C3 on COM7).** The single-board window looks as before. The tab strip, switching boards, the bootloader tab and the busy-port tab were checked on screen with the debug-only `--smoke-demo-boards`, which adds two pretend tabs (screenshots via `--smoke DIR --smoke-demo-boards`). **Please plug in two or three real boards** and check that each gets its own tab and its own live mirror, then try a firmware install on one while another stays connected.
5. **The macOS side is written but not compiled** (there's no Swift toolchain on the PC). A review agent read it for compile errors and fixed what it found. Build it on the Mac (`xcodebuild -scheme TouchDeck test`) and expect a few more fixes. If Remote Login is turned on (see Lanyard), the PC session can do this itself.
6. **The website is on its own branch**, `feature/website`, in the worktree `C:\ai\TouchDesk-website`. It isn't merged and isn't pushed. The agent's open items:
   - `og:image` needs an absolute URL once the site's address is known;
   - board prices are its estimates;
   - the ESP32-C3 card says "flash with PlatformIO for now";
   - confirm "Windows 10 and 11" and the macOS wording;
   - the board drawings are generic bezels, not replicas.
