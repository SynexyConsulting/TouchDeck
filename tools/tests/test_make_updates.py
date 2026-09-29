import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest
import make_updates as mu

BASE = "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/"


def blob(tmp_path, name, data):
    p = tmp_path / name
    p.write_bytes(data)
    return str(p)


def test_entry_hashes_and_sizes_the_file(tmp_path):
    p = blob(tmp_path, "TouchDeck-1.2.0.msi", b"x" * 1234)
    e = mu.entry(p, "1.2.0", "app-v1.2.0")
    assert e == {"version": "1.2.0", "url": BASE + "app-v1.2.0/TouchDeck-1.2.0.msi",
                 "sha256": hashlib.sha256(b"x" * 1234).hexdigest(), "size": 1234}


def test_new_feed_from_nothing(tmp_path):
    app = mu.entry(blob(tmp_path, "a.msi", b"a"), "1.2.0", "app-v1.2.0")
    fw = dict(mu.entry(blob(tmp_path, "f.uf2", b"f"), "1.6.0", "app-v1.2.0"), board="rp2040-169")
    feed = mu.merge(None, app_windows=app, firmware=[fw], published="2026-09-29T08:00:00Z")
    assert feed["schema"] == 1 and feed["published"] == "2026-09-29T08:00:00Z"
    assert feed["app"]["windows"]["version"] == "1.2.0"
    assert feed["firmware"] == [fw]


def test_firmware_release_keeps_the_app_and_other_boards(tmp_path):
    prev = {"schema": 1, "published": "x",
            "app": {"windows": {"version": "1.2.0", "url": BASE + "a", "sha256": "0" * 64, "size": 1}},
            "firmware": [{"board": "rp2040-169", "version": "1.6.0", "url": BASE + "f", "sha256": "0" * 64, "size": 1},
                         {"board": "esp32c3-128", "version": "1.6.0", "url": BASE + "e", "sha256": "0" * 64, "size": 1}]}
    new_rp = dict(mu.entry(blob(tmp_path, "rp.uf2", b"r"), "1.7.0", "fw-v1.7.0"), board="rp2040-169")
    feed = mu.merge(prev, app_windows=None, firmware=[new_rp], published="p")
    assert feed["app"] == prev["app"]
    boards = {f["board"]: f["version"] for f in feed["firmware"]}
    assert boards == {"rp2040-169": "1.7.0", "esp32c3-128": "1.6.0"}


def test_app_release_replaces_only_the_app(tmp_path):
    prev = {"schema": 1, "app": {"windows": {"version": "1.2.0"}, "macos": {"version": "0.1.0"}}, "firmware": []}
    app = mu.entry(blob(tmp_path, "a.msi", b"a"), "1.3.0", "app-v1.3.0")
    feed = mu.merge(prev, app_windows=app, firmware=[], published="p")
    assert feed["app"]["windows"]["version"] == "1.3.0"
    assert feed["app"]["macos"] == {"version": "0.1.0"}


def test_never_goes_backwards(tmp_path):
    prev = {"schema": 1, "app": {"windows": {"version": "1.3.0"}}, "firmware": []}
    app = mu.entry(blob(tmp_path, "a.msi", b"a"), "1.2.0", "app-v1.2.0")
    with pytest.raises(ValueError):
        mu.merge(prev, app_windows=app, firmware=[], published="p")


@pytest.mark.parametrize("tag", ["v1.2.0", "app-v1.2", "app-v1.2.0;rm", "../x"])
def test_tags_are_validated(tag):
    with pytest.raises(ValueError):
        mu.check_tag(tag)


def test_output_is_valid_json_for_the_app(tmp_path):
    app = mu.entry(blob(tmp_path, "TouchDeck-1.2.0.msi", b"a"), "1.2.0", "app-v1.2.0")
    text = mu.dumps(mu.merge(None, app_windows=app, firmware=[], published="p"))
    assert json.loads(text)["app"]["windows"]["url"].startswith(BASE)


# ---------- feed signatures (the app pins the public key and refuses anything else) ----------

@pytest.fixture(scope="module")
def keypair():
    pytest.importorskip("cryptography")
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives import serialization
    key = ec.generate_private_key(ec.SECP256R1())
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    return pem, mu.public_key_b64(pem)


def test_signature_is_base64_raw_r_s(keypair):
    import base64
    pem, _ = keypair
    sig = mu.sign(b"feed bytes", pem)
    assert len(base64.b64decode(sig)) == 64          # r||s, as the app expects (IeeeP1363)


def test_sign_then_verify(keypair):
    pem, pub = keypair
    assert mu.verify(b"feed bytes", mu.sign(b"feed bytes", pem), pub)


def test_tampered_feed_or_wrong_key_fails(keypair):
    pem, pub = keypair
    sig = mu.sign(b"feed bytes", pem)
    assert not mu.verify(b"feed bytez", sig, pub)
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives import serialization
    other = ec.generate_private_key(ec.SECP256R1()).private_bytes(
        serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    assert not mu.verify(b"feed bytes", sig, mu.public_key_b64(other))
    assert not mu.verify(b"feed bytes", b"garbage", pub)
