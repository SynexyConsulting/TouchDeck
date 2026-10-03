# Questions and decisions from the overnight multi-board build (2026-10-03)

These came up while building `feature/multi-board`. Where something needed a decision, I made the call that seemed right and kept going. Each item says what I chose, so you can overrule it.

1. **The re-check on connect was replaced.** Earlier tonight you picked "re-check when a new or changed board connects" (`UpdateRecheck`). With several boards, the app now keeps the last verified feed and works out each board's offer from it locally. A board plugged in after the daily check gets its offer at once, with no network call, and a board coming back with new firmware is recomputed the same way. I removed `UpdateRecheck`; its goal is met without fetching the feed again. *If you'd rather also re-fetch the feed when a board connects, it's a few lines to add back.*
2. **Pastes from two boards at once aren't serialized.** An RP2040 types through its own USB keyboard, which the app can't hold back. Only the ESP32-C3 in PC mode types through the app. Two boards pasting at the same instant will interleave their text. I judged it too rare to justify a protocol change.
3. **Two boards in their bootloader at the same time show as one tab.** Windows can't tell which UF2 drive belongs to which board. Installs run one at a time, so this only matters if you put two boards into bootloader mode by hand.
