import csv
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import github_traffic as gt


def day(date, count, uniques):
    return {"timestamp": f"{date}T00:00:00Z", "count": count, "uniques": uniques}


def rows(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


# Shaped like GET /repos/{repo}/traffic/... (API version 2022-11-28).
VIEWS = {"count": 14, "uniques": 6, "views": [day("2026-10-04", 9, 4), day("2026-10-05", 5, 3)]}
CLONES = {"count": 2, "uniques": 1, "clones": [day("2026-10-05", 2, 1)]}
REFERRERS = [{"referrer": "touchdeck.synexyconsulting.com", "count": 8, "uniques": 3},
             {"referrer": "Google", "count": 2, "uniques": 2}]
PATHS = [{"path": "/SynexyConsulting/TouchDeck", "title": "TouchDeck", "count": 10, "uniques": 5}]


def test_first_run_writes_every_file(tmp_path):
    gt.save(tmp_path, VIEWS, CLONES, REFERRERS, PATHS, "2026-10-05")
    assert rows(tmp_path / "views.csv") == [
        {"date": "2026-10-04", "count": "9", "uniques": "4"},
        {"date": "2026-10-05", "count": "5", "uniques": "3"}]
    assert rows(tmp_path / "clones.csv") == [{"date": "2026-10-05", "count": "2", "uniques": "1"}]
    assert [r["referrer"] for r in rows(tmp_path / "referrers.csv")] == ["touchdeck.synexyconsulting.com", "Google"]
    assert rows(tmp_path / "paths.csv")[0]["title"] == "TouchDeck"


def test_later_run_keeps_old_days_and_updates_refetched_ones(tmp_path):
    gt.save(tmp_path, VIEWS, CLONES, REFERRERS, PATHS, "2026-10-05")
    # Next day the response has 10-05 (now complete) and the new 10-06, but not 10-04,
    # which must stay in the file.
    later = {"views": [day("2026-10-05", 7, 4), day("2026-10-06", 1, 1)]}
    gt.save(tmp_path, later, {"clones": []}, REFERRERS[:1], PATHS, "2026-10-06")
    assert rows(tmp_path / "views.csv") == [
        {"date": "2026-10-04", "count": "9", "uniques": "4"},
        {"date": "2026-10-05", "count": "7", "uniques": "4"},
        {"date": "2026-10-06", "count": "1", "uniques": "1"}]
    assert len(rows(tmp_path / "clones.csv")) == 1
    snaps = [r["snapshot"] for r in rows(tmp_path / "referrers.csv")]
    assert snaps == ["2026-10-05", "2026-10-05", "2026-10-06"]


def test_same_day_rerun_replaces_that_snapshot(tmp_path):
    gt.save(tmp_path, VIEWS, CLONES, REFERRERS, PATHS, "2026-10-05")
    gt.save(tmp_path, VIEWS, CLONES, REFERRERS[:1], PATHS, "2026-10-05")
    assert len(rows(tmp_path / "referrers.csv")) == 1
    assert len(rows(tmp_path / "views.csv")) == 2
