"""Publishes a Touch Deck release to the public update repo (SynexyConsulting/TouchDeckUpdates).

    TOUCHDECK_UPDATES_TOKEN=... python tools/publish_release.py --tag app-v1.2.0 \\
        --app-windows windows-app/out/TouchDeck-1.2.0.msi --app-version 1.2.0 \\
        [--app-macos TouchDeck-0.2.0.pkg --app-macos-version 0.2.0] \\
        --firmware rp2040-169=1.6.0=windows-app/firmware/rp2040-169.uf2 \\
        --key-file ~/.touchdeck/feed-signing-key.pem [--dry-run]

Creates the release, uploads the files, a complete updates.json (merged with the
previous latest feed, see make_updates.py) and its signature updates.json.sig.

- The token needs Contents: read and write on the update repo only. It is read from
  TOUCHDECK_UPDATES_TOKEN and never printed.
- The signing key comes from TOUCHDECK_FEED_KEY (PEM text) or --key-file. It must match
  the public key pinned in the app, or nothing is published.
- The previous feed is merged only if its own signature verifies.

Used by .github/workflows/release.yml and by hand.
"""
import argparse
import base64
import datetime
import json
import os
import shutil
import sys
import tempfile
import re
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_updates as mu

API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
FEED = f"https://github.com/{mu.REPO}/releases/latest/download/updates.json"
APP_KEY_SOURCE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                              "windows-app", "src", "TouchDeck.Core", "Updates", "Updates.cs")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Authenticated API calls must not carry the token to wherever a redirect points."""
    def redirect_request(self, *args, **kwargs):
        return None


_AUTH_OPENER = urllib.request.build_opener(_NoRedirect)


def token():
    t = os.environ.get("TOUCHDECK_UPDATES_TOKEN")
    if not t:
        sys.exit("Set TOUCHDECK_UPDATES_TOKEN (fine-grained token: Contents read/write on the update repo).")
    return t


def pinned_public_key():
    """The public key compiled into the app (UpdateSource.OfficialPublicKey)."""
    m = re.search(r'OfficialPublicKey = "([A-Za-z0-9+/=]+)"', open(APP_KEY_SOURCE, encoding="utf-8").read())
    if not m:
        sys.exit("Can't find OfficialPublicKey in the app source.")
    return m.group(1)


def signing_key(path):
    pem = os.environ.get("TOUCHDECK_FEED_KEY", "").encode() or (open(path, "rb").read() if path else b"")
    if not pem:
        return None
    if mu.public_key_b64(pem) != pinned_public_key():
        sys.exit("The signing key does not match the public key pinned in the app; refusing to publish.")
    return pem


def call(method, url, tok, body=None, data=None, content_type="application/json"):
    headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28",
               "User-Agent": "touchdeck-publish"}
    if tok:
        headers["Authorization"] = f"Bearer {tok}"
    if body is not None:
        data = json.dumps(body).encode()
    if data is not None:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with _AUTH_OPENER.open(req, timeout=120) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw and r.headers.get_content_type() == "application/json" else raw)
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def _get_public(url):
    """Unauthenticated GET (public release assets); None on 404."""
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "touchdeck-publish"}), timeout=30) as r:
            return r.read()
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def previous_feed():
    """The latest published feed (signature verified), or None when nothing is published yet."""
    raw = _get_public(FEED)
    if raw is None:
        return None
    sig = _get_public(FEED + ".sig")
    if sig is None or not mu.verify(raw, sig, pinned_public_key()):
        sys.exit("The published feed's signature doesn't verify; refusing to build on it.")
    return json.loads(raw)


def ensure_initialized(tok):
    """A release needs a commit to tag: give an empty repo its README first."""
    status, _ = call("GET", f"{API}/repos/{mu.REPO}/commits?per_page=1", tok)
    if status == 200:
        return
    if status != 409:  # 409 = empty repository
        sys.exit(f"Can't read {mu.REPO} (HTTP {status}). Check the token's repository access.")
    readme = (
        "# Touch Deck updates\n\n"
        "Release packages for the Touch Deck Windows app and board firmware.\n"
        "The app reads `updates.json` from the latest release and verifies each\n"
        "download against its SHA-256 before installing.\n"
    )
    status, resp = call("PUT", f"{API}/repos/{mu.REPO}/contents/README.md", tok,
                        body={"message": "Initial README", "content": base64.b64encode(readme.encode()).decode()})
    if status not in (200, 201):
        sys.exit(f"Couldn't initialise {mu.REPO} (HTTP {status}).")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--title")
    ap.add_argument("--notes", default="")
    ap.add_argument("--app-windows", metavar="MSI")
    ap.add_argument("--app-version")
    ap.add_argument("--app-macos", metavar="PKG", help="signed, notarized macOS installer package")
    ap.add_argument("--app-macos-version", help="the Mac app's own version (CFBundleShortVersionString)")
    ap.add_argument("--firmware", action="append", default=[], metavar="BOARD=VERSION=PATH")
    ap.add_argument("--out", default=os.path.join(tempfile.gettempdir(), "touchdeck-release"))
    ap.add_argument("--key-file", help="PEM signing key (else TOUCHDECK_FEED_KEY)")
    ap.add_argument("--dry-run", action="store_true", help="build the feed and list the files; publish nothing")
    a = ap.parse_args()

    tag = mu.check_tag(a.tag)
    if os.path.isdir(a.out):
        shutil.rmtree(a.out)
    os.makedirs(a.out)

    files = []
    app = None
    if a.app_windows:
        if not a.app_version:
            sys.exit("--app-version is required with --app-windows")
        name = f"TouchDeck-{a.app_version}.msi"
        dst = os.path.join(a.out, name)
        shutil.copyfile(a.app_windows, dst)
        app = mu.entry(dst, a.app_version, tag)
        files.append(dst)
    app_mac = None
    if a.app_macos:
        if not a.app_macos_version:
            sys.exit("--app-macos-version is required with --app-macos")
        name = f"TouchDeck-{a.app_macos_version}.pkg"
        dst = os.path.join(a.out, name)
        shutil.copyfile(a.app_macos, dst)
        app_mac = mu.entry(dst, a.app_macos_version, tag)
        files.append(dst)
    firmware = []
    for spec in a.firmware:
        board, version, path = spec.split("=", 2)
        if not mu.BOARD.match(board):
            sys.exit(f"bad board id {board!r}")
        dst = os.path.join(a.out, f"{board}-{version}.uf2")
        shutil.copyfile(path, dst)
        firmware.append(dict(mu.entry(dst, version, tag), board=board))
        files.append(dst)
    if not files:
        sys.exit("Nothing to publish.")

    published = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    feed = mu.merge(previous_feed(), app_windows=app, firmware=firmware, published=published, app_macos=app_mac)
    feed_path = os.path.join(a.out, "updates.json")
    feed_bytes = mu.dumps(feed).encode("utf-8")
    with open(feed_path, "wb") as f:
        f.write(feed_bytes)
    files.append(feed_path)
    key = signing_key(a.key_file)
    if key is None and not a.dry_run:
        sys.exit("No signing key (TOUCHDECK_FEED_KEY or --key-file): the app refuses unsigned feeds.")
    if key is not None:
        sig_path = feed_path + ".sig"
        with open(sig_path, "w", encoding="ascii", newline="") as f:
            f.write(mu.sign(feed_bytes, key))
        if not mu.verify(feed_bytes, open(sig_path, "rb").read(), pinned_public_key()):
            sys.exit("Self-check failed: the new signature doesn't verify against the pinned key.")
        files.append(sig_path)

    print(f"Release {tag}:")
    for p in files:
        print(f"  {os.path.basename(p)}  {os.path.getsize(p)} bytes")
    if a.dry_run:
        print(mu.dumps(feed))
        return

    tok = token()
    ensure_initialized(tok)
    status, _ = call("GET", f"{API}/repos/{mu.REPO}/releases/tags/{tag}", tok)
    if status == 200:
        sys.exit(f"Release {tag} already exists; bump the version instead of overwriting it.")
    status, rel = call("POST", f"{API}/repos/{mu.REPO}/releases", tok, body={
        "tag_name": tag, "name": a.title or f"Touch Deck {tag}", "body": a.notes, "make_latest": "true"})
    if status != 201:
        sys.exit(f"Creating the release failed (HTTP {status}).")
    for p in files:
        with open(p, "rb") as f:
            data = f.read()
        ctype = "application/json" if p.endswith(".json") else "text/plain" if p.endswith(".sig") else "application/octet-stream"
        url = f"{UPLOADS}/repos/{mu.REPO}/releases/{rel['id']}/assets?name={os.path.basename(p)}"
        status, _ = call("POST", url, tok, data=data, content_type=ctype)
        if status != 201:
            sys.exit(f"Uploading {os.path.basename(p)} failed (HTTP {status}); the release is incomplete.")
        print(f"  uploaded {os.path.basename(p)}")
    print(f"Published: {rel['html_url']}")


if __name__ == "__main__":
    main()
