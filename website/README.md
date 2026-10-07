# Touch Deck website

A static brochure site: plain HTML, CSS and JavaScript, no build step.

| Path | What |
|---|---|
| `index.html` | The home page |
| `license.html` | The licence in plain words (adapted from `docs/license.md`) |
| `assets/css/site.css` | All styles. Colours come from the Windows app (`App.xaml`) |
| `assets/js/site.js` | Scroll behaviour: screen switching, the console demo, GSAP scroll effects |
| `assets/js/analytics.js` | GA4 events: downloads, outbound and in-page links, FAQ, sections seen (see Analytics) |
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

The live site is https://touchdeck.synexyconsulting.com. A push to `main` that touches `website/` runs `.github/workflows/website.yml`, which builds `Dockerfile` (unprivileged nginx with `nginx.conf`, port 8080) and pushes `ghcr.io/synexyconsulting/touchdeck-website` tagged `latest` and `sha-<commit>`. Going live is a manual redeploy of the stack (Re-pull image); the stack itself is kept outside this repo.

To check the image locally:

```bash
docker build -t touchdeck-website website
docker run --rm -p 8080:8080 touchdeck-website   # open http://localhost:8080
```

The image holds only `index.html`, `license.html` and `assets/`. It is still a plain static site, so any static host works too.

`og:image` and `og:url` in `index.html` are absolute URLs on the live domain (link previews in chat apps need that). Change them if the domain changes.

## Analytics

Both pages load the Google tag (GA4, `G-07TPQJ6MLL`) at the top of `<head>`. Only the live hostname reports, so local previews and the Docker check don't add test visits. To watch hits arrive in GA4's DebugView, add `?ga_debug=1` to a live URL.

`assets/js/analytics.js` adds these events to the page views. It sorts links by where they point, so new links need no extra markup:

| Event | Sent when | Parameters |
|---|---|---|
| `download_click` | a link to the TouchDeckUpdates releases is clicked | `link_text`, `link_url`, `section` |
| `affiliate_click` | a shop link (`rel="sponsored"`) is clicked | `board` (its row in Where to buy), `shop`, `link_domain`, `section` |
| `donate_click` | a Ko-fi link is clicked | `link_text`, `section` |
| `outbound_click` | any other link off the site is clicked | `link_domain`, `link_url`, `link_text`, `section` |
| `nav_click` | an in-page anchor or another page of the site is clicked | `link_text`, `target`, `section` |
| `faq_open` | an FAQ answer is opened | `question` |
| `demo_replay` | the console demo is replayed | |
| `section_view` | a section reaches the middle of the window (once per page load) | `section` |

`section` is the id of the `<section>` the element is in, or `header`, `main` or `footer`. A download card's `link_text` is its title (Windows, macOS, All versions). To use `link_text`, `section`, `question` and the other parameters as report dimensions, register each as a custom dimension (event scope) in GA4 under Admin > Custom definitions. Until then, GA4 still counts the events.

The Google tag has no Subresource Integrity hash, unlike GSAP: Google serves a different, frequently updated `gtag.js` for each measurement ID.

The GitHub repo's own traffic (views, clones, referrers) is saved separately, by `.github/workflows/traffic.yml`, to the `traffic` branch.

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
- When the ESP32-C3 firmware is published, drop the PlatformIO note on its board card and set its Firmware cell in Where to buy to "Ready-made".
- Donations: https://ko-fi.com/synexyconsulting (Download section, footer of both pages, "Donations are welcome" on the licence page).

## Where to buy (affiliate links)

The table under the board cards (`#buy` in `index.html`) has one row per board. A compatible board can have a row without having a card.

- **Add a board:** copy a `<tr>`. The board name goes in the row's `<th>`, because analytics reports it as `board`.
- **Add a shop:** put another link in the last cell, with the shop's name as its text, for example `<a href="…" rel="sponsored noopener">AliExpress</a>`.
- **Keep `rel="sponsored"` on every affiliate link.** It tells search engines the link is paid, and analytics uses it to send `affiliate_click` instead of `outbound_click`.
- **Amazon links:** keep them short, as `https://www.amazon.com/dp/<ASIN>?linkCode=ll2&tag=thecronjob-20&linkId=…`. Leave out the search-session parameters (`crid`, `dib`, `qid`, `keywords`), and write `&` as `&amp;` in HTML.
- **Disclosure:** the fine print under the table carries the Amazon Associates statement that the programme requires. Keep it, and name any new affiliate programme there too.
