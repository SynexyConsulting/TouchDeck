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


def merge(previous, app_windows, firmware, published):
    """previous: the last feed (or None). app_windows: new Windows app entry or None.
    firmware: new entries, each with a "board". Returns the complete new feed."""
    prev = previous or {}
    apps = dict(prev.get("app") or {})
    if app_windows:
        old = apps.get("windows")
        if old and version_tuple(app_windows["version"]) < version_tuple(old["version"]):
            raise ValueError(f"app {app_windows['version']} is older than the published {old['version']}")
        apps["windows"] = app_windows
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
