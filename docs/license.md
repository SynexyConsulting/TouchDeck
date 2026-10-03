# Touch Deck licence

Copyright (C) 2026 Synexy Consulting (https://github.com/SynexyConsulting)

Touch Deck (the firmware, the Windows and macOS apps, the host renderers and the tools in this repository) is free software, licensed under the **GNU General Public License, version 3 or (at your option) any later version** (GPL-3.0-or-later). The full licence text is in the [`LICENSE`](https://github.com/SynexyConsulting/TouchDeck/blob/main/LICENSE) file and at https://www.gnu.org/licenses/gpl-3.0.html. Third-party components keep their own licences (see [Third-party components](#third-party-components)).

## In plain words (not part of the licence)

- **You may** use Touch Deck for anything, for free: at home, at work, in a school or a business.
- **You may** study it, change it and share it, changed or not.
- **If you share it** (the firmware, an app, or a board with Touch Deck on it), you must also share the source code of that version, including your changes, under this same licence, and keep the copyright notices. Nobody may turn it into a closed, secret product.
- **You may charge** for copies, boards or support, but the people who get it from you get the same rights: they can see the source, change it and share it too.
- **Donations are welcome** and don't change any of this.
- It comes **as is**, with no warranty.

## Notice

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with this program. If not, see https://www.gnu.org/licenses/.

## Contributions

Contributions (for example pull requests) are accepted under the same licence, GPL-3.0-or-later. By submitting one, you confirm that you have the right to license it this way.

## Where the source is

The complete source for every release is in the public repository https://github.com/SynexyConsulting/TouchDeck. Each release is tagged there (`app-vX.Y.Z` for the apps, `fw-vX.Y.Z` for firmware), and the release files in https://github.com/SynexyConsulting/TouchDeckUpdates are built from those tags.

## Third-party components

These components are included in, or used to build, Touch Deck. Each stays under its own licence, and all of them can be combined with GPL-3.0 software. Their licence texts and notices must be kept with any copy.

| Component | Used in | Licence |
|---|---|---|
| Raspberry Pi Pico SDK | RP2040 / RP2350 firmware | BSD 3-Clause |
| TinyUSB (via the Pico SDK) | RP2040 / RP2350 firmware | MIT |
| Arduino-ESP32 core 2.0.17 (Espressif) | ESP32-C3 firmware | LGPL 2.1 or later (parts Apache 2.0) |
| ESP-IDF components (via Arduino-ESP32) | ESP32-C3 firmware | Apache 2.0 |
| LovyanGFX 1.2.30 | ESP32-C3 firmware | FreeBSD (BSD 2-Clause); bundled fonts under their own licences |
| NimBLE-Arduino 1.4.3 | ESP32-C3 firmware | Apache 2.0 |
| Panel init register values from Waveshare's demo code (`LCD_1in28.c`, `LCD_1in69.c`) | `src/lcd.c` | Those two files carry no licence header (Waveshare's other demo files use the MIT licence); only the panels' register settings were taken, not the code |
| Barlow (Jeremy Tribby) | apps, firmware fonts | SIL Open Font License 1.1 (`tools/fonts/OFL-Barlow.txt`) |
| JetBrains Mono (JetBrains) | apps, firmware fonts | SIL Open Font License 1.1 (`tools/fonts/OFL-JetBrainsMono.txt`) |
| DSEG (keshikan) | firmware fonts | SIL Open Font License 1.1 (`tools/fonts/DSEG-LICENSE.txt`) |
| System.IO.Ports, System.Management (Microsoft) | Windows app | MIT |
| pyserial, Pillow, cryptography, pytest, PlatformIO | development tools (not shipped) | BSD 3-Clause, MIT-CMU (HPND), Apache 2.0 / BSD, MIT, Apache 2.0 |

**Fonts.** The font files, and the bitmap fonts generated from them (`src/aa_fonts.c`, `esp32c3/src/aa_fonts.c`, made by `tools/fontgen.py`), stay under the SIL Open Font License 1.1, not the GPL. The OFL allows them to be bundled and distributed with any software, including this one. Their reserved font names apply to changed versions of the fonts.
