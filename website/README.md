# Touch Deck website

A static brochure site: plain HTML, CSS and JavaScript, no build step.

| Path | What |
|---|---|
| `index.html` | The home page |
| `license.html` | The licence in plain words (adapted from `docs/license.md`) |
| `assets/css/site.css` | All styles. Colours come from the Windows app (`App.xaml`) |
| `assets/js/site.js` | Scroll behaviour: screen switching, the console demo, GSAP scroll effects |
| `assets/screens/` | The boards' screens, rendered from the firmware's own page code |
| `assets/img/` | Logo, Open Graph image, Windows and macOS app screenshots |
| `tools/render_screens.py` | Re-renders `assets/screens/` |

External resources: Barlow and JetBrains Mono from Google Fonts, and GSAP 3.12.5 + ScrollTrigger from cdnjs (pinned with Subresource Integrity hashes). Without GSAP the page still works: screens switch and the console types, only the scrubbed tilt, zoom and parallax are missing. With `prefers-reduced-motion: reduce` those effects are off.

## Preview

Any static file server works. From the repo root:

```bash
python -m http.server 8000 -d website
# open http://localhost:8000
```

Opening `index.html` straight from disk mostly works too, but a server is closer to the real thing.

## Deploy

Upload the contents of `website/` to any static host: GitHub Pages, Cloudflare Pages, Netlify, S3, or a plain web server. There's nothing to build. Leave out `README.md` and `tools/` if you like; the page doesn't use them.

- **GitHub Pages:** publish from a branch with the site at the root, or use an Actions workflow that uploads `website/` as the Pages artifact.
- **Cloudflare Pages / Netlify:** set the build command to none and the output directory to `website`.

After deploying, set `og:image` in `index.html` to the absolute URL of `assets/img/og.png` (for example `https://touchdeck.example/assets/img/og.png`). Link previews in chat apps need an absolute URL.

## Rectangular board mockups

Every 1.69" mockup's glass uses `--rect-glass` (`18.333% / 15.714%`, the firmware's 44 px corner on 240x280) through `.board.rect .screen` or `.rect-glass`; the bezel uses the concentric `--rect-bezel`. Change them there only.

## Updating the screens

The pictures in `assets/screens/` are the real firmware pages, drawn on the PC by the same renderer the Windows app's live mirror uses (`hostui/`). After a UI change in the firmware:

```bash
python website/tools/render_screens.py
```

It needs a C compiler (Visual Studio's C++ tools on Windows, `cc` on macOS/Linux) and Pillow (`python -m pip install pillow`). It writes:

- `rect-*.png` (RP2040-Touch-LCD-1.69) and `round-*.png` (RP2350-Touch-LCD-1.28): watch, clipboard, clipboard while pasting, jiggler, the jiggler menu;
- `*-watch-anim.webp` (a minute of ticks) and `*-jiggler-anim.webp` (the dot driving O and W);
- `esp32-*.png`: the ESP32-C3 board in Bluetooth mode.

## Links to keep current

- Downloads: https://github.com/SynexyConsulting/TouchDeckUpdates/releases/latest and /releases
- Source: https://github.com/SynexyConsulting/TouchDeck
- When the macOS .pkg is published, update its line in the Download section of `index.html`.
- When the ESP32-C3 firmware is published, drop the PlatformIO note on its board card.
