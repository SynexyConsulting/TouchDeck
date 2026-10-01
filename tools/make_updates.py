"""Builds the public update feed (updates.json) for the Touch Deck app.

Every release in SynexyConsulting/TouchDeckUpdates carries a complete feed: the
newest app per OS and the newest firmware per board. A new release starts from
the previous latest feed and replaces only what it ships, so "latest" is always
enough for the app. Versions never go backwards.

Used by tools/publish_release.py (CI and by hand); the format is documented in
docs/superpowers/specs/2026-09-29-updates-settings-design.md.
"""
import hashlib
import json
import os
import re

REPO = "SynexyConsulting/TouchDeckUpdates"
BASE = f"https://github.com/{REPO}/releases/download/"
TAG = re.compile(r"^(app|fw)-v\d+\.\d+\.\d+$")
BOARD = re.compile(r"^[a-z0-9][a-z0-9-]{0,31}$")


def check_tag(tag):
    if not TAG.match(tag):
        raise ValueError(f"bad release tag {tag!r} (want app-vX.Y.Z or fw-vX.Y.Z)")
    return tag


def version_tuple(v):
    parts = v.split(".")
    if len(parts) != 3 or not all(p.isdigit() for p in parts):
        raise ValueError(f"bad version {v!r}")
    return tuple(int(p) for p in parts)


def entry(path, version, tag):
    """A feed entry for a file uploaded to release `tag` under its own name."""
    check_tag(tag)
    version_tuple(version)
    h = hashlib.sha256()
    size = 0
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
            size += len(chunk)
    return {"version": version, "url": BASE + tag + "/" + os.path.basename(path), "sha256": h.hexdigest(), "size": size}


def merge(previous, app_windows, firmware, published, app_macos=None):
    """previous: the last feed (or None). app_windows / app_macos: new app entry per OS, or None
    (the two apps have their own version numbers). firmware: new entries, each with a "board".
    Returns the complete new feed."""
    prev = previous or {}
    apps = dict(prev.get("app") or {})
    for os_name, new in (("windows", app_windows), ("macos", app_macos)):
        if not new:
            continue
        old = apps.get(os_name)
        if old and version_tuple(new["version"]) < version_tuple(old["version"]):
            raise ValueError(f"{os_name} app {new['version']} is older than the published {old['version']}")
        apps[os_name] = new
    boards = {f["board"]: f for f in (prev.get("firmware") or [])}
    for f in firmware:
        if not BOARD.match(f["board"]):
            raise ValueError(f"bad board id {f['board']!r}")
        old = boards.get(f["board"])
        if old and version_tuple(f["version"]) < version_tuple(old["version"]):
            raise ValueError(f"{f['board']} {f['version']} is older than the published {old['version']}")
        boards[f["board"]] = f
    return {"schema": 1, "published": published, "app": apps, "firmware": [boards[b] for b in sorted(boards)]}


def dumps(feed):
    return json.dumps(feed, indent=2) + "\n"


# ---------- feed signature (ECDSA P-256 / SHA-256, raw r||s, base64) ----------
# The app pins the public key (UpdateSource.OfficialPublicKey) and refuses a feed whose
# updates.json.sig doesn't verify. The private key never lives in the repo.

def _crypto():
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec, utils
    return hashes, serialization, ec, utils


def public_key_b64(private_pem):
    import base64
    _, serialization, _, _ = _crypto()
    key = serialization.load_pem_private_key(private_pem, None)
    spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return base64.b64encode(spki).decode()


def sign(data, private_pem):
    """Signature file content for `data` (bytes): base64 of r||s (64 bytes)."""
    import base64
    hashes, serialization, ec, utils = _crypto()
    key = serialization.load_pem_private_key(private_pem, None)
    if not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != "secp256r1":
        raise ValueError("the feed key must be an ECDSA P-256 private key")
    r, s = utils.decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    return base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode()


def verify(data, signature, public_b64):
    import base64
    hashes, serialization, ec, utils = _crypto()
    from cryptography.exceptions import InvalidSignature
    try:
        raw = base64.b64decode(signature.strip() if isinstance(signature, (str, bytes)) else signature, validate=True)
        if len(raw) != 64:
            return False
        pub = serialization.load_der_public_key(base64.b64decode(public_b64))
        pub.verify(utils.encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big")),
                   data, ec.ECDSA(hashes.SHA256()))
        return True
    except (InvalidSignature, ValueError, TypeError):
        return False
