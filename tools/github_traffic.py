"""Saves the repository's GitHub traffic before GitHub drops it.

GitHub keeps views, clones, referrers and popular pages for 14 days only. Run daily
(.github/workflows/traffic.yml) to build up the whole history as CSV files:

  views.csv, clones.csv   date, count, uniques
                          One row per day. A day that is fetched again is overwritten,
                          so today's partial count becomes the full one on the next run.
  referrers.csv           snapshot, referrer, count, uniques
  paths.csv               snapshot, path, title, count, uniques
                          GitHub only gives these as 14-day totals, so each run adds one
                          dated snapshot; a second run on the same day replaces it.

Reading traffic needs a token with "Administration: Read-only" on the repo (a
fine-grained personal access token); the Actions GITHUB_TOKEN can't.

    TRAFFIC_TOKEN=... python tools/github_traffic.py SynexyConsulting/TouchDeck OUT_DIR
"""
import csv
import json
import os
import sys
import urllib.request
from datetime import datetime, timezone

API = "https://api.github.com/repos/"


def fetch(repo, endpoint, token):
    req = urllib.request.Request(API + repo + "/traffic/" + endpoint, headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "touchdeck-traffic",
    })
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def write_csv(path, fields, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


def merge_daily(path, items):
    """items: GitHub's [{"timestamp": "2026-10-01T00:00:00Z", "count": n, "uniques": n}, ...]"""
    days = {r["date"]: r for r in read_csv(path)}
    for it in items:
        date = it["timestamp"][:10]
        days[date] = {"date": date, "count": it["count"], "uniques": it["uniques"]}
    write_csv(path, ["date", "count", "uniques"], [days[d] for d in sorted(days)])


def merge_snapshot(path, fields, snapshot, rows):
    """Replace `snapshot`'s rows (if any) with `rows`, keeping every other snapshot."""
    kept = [r for r in read_csv(path) if r["snapshot"] != snapshot]
    new = [{"snapshot": snapshot, **{k: r[k] for k in fields[1:]}} for r in rows]
    write_csv(path, fields, kept + new)


def save(out, views, clones, referrers, paths, snapshot):
    os.makedirs(out, exist_ok=True)
    merge_daily(os.path.join(out, "views.csv"), views["views"])
    merge_daily(os.path.join(out, "clones.csv"), clones["clones"])
    merge_snapshot(os.path.join(out, "referrers.csv"), ["snapshot", "referrer", "count", "uniques"],
                   snapshot, referrers)
    merge_snapshot(os.path.join(out, "paths.csv"), ["snapshot", "path", "title", "count", "uniques"],
                   snapshot, paths)


def main(argv):
    if len(argv) != 3:
        sys.exit(__doc__)
    repo, out = argv[1], argv[2]
    token = os.environ.get("TRAFFIC_TOKEN")
    if not token:
        sys.exit("TRAFFIC_TOKEN is not set (a token with Administration: Read-only on the repo)")
    snapshot = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    save(out,
         fetch(repo, "views?per=day", token),
         fetch(repo, "clones?per=day", token),
         fetch(repo, "popular/referrers", token),
         fetch(repo, "popular/paths", token),
         snapshot)
    print(f"saved {repo} traffic to {out} (snapshot {snapshot})")


if __name__ == "__main__":
    main(sys.argv)
