"""touchdeck helpers: pick a board by the model it reports, find either chip's bootloader drive."""
import os
import sys
from types import SimpleNamespace

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import touchdeck  # noqa: E402


class FakeSerial:
    def __init__(self, reply):
        self.reply, self.sent = reply, b""

    def write(self, data):
        self.sent += data

    def reset_input_buffer(self):
        pass

    def read_all(self):
        return self.reply.encode() if b"VER" in self.sent else b""

    def close(self):
        pass


@pytest.fixture
def two_rp_boards(monkeypatch):
    """COM11 is the round RP2350, COM6 the RP2040 1.69, COM9 is busy; both RP boards are CAFE:4011."""
    ports = [SimpleNamespace(device=d, vid=0xCAFE, pid=0x4011) for d in ("COM11", "COM9", "COM6")]
    replies = {"COM11": "PONG\nVERSION rp2350-128 1.7.0 Sep 30 2026\n",
               "COM6": "VERSION rp2040-169 1.7.0 Sep 30 2026\n"}

    def open_serial(port, timeout=0.1):
        if port == "COM9":
            raise OSError("Access is denied")
        return FakeSerial(replies[port])

    monkeypatch.setattr(touchdeck.list_ports, "comports", lambda: ports)
    monkeypatch.setattr(touchdeck, "open_serial", open_serial)
    monkeypatch.setattr(touchdeck.time, "sleep", lambda s: None)


def test_find_board_picks_the_port_reporting_that_model(two_rp_boards):
    assert touchdeck.find_board("rp2040-169") == "COM6"
    assert touchdeck.find_board("rp2350-128") == "COM11"


def test_find_board_is_none_when_that_model_is_absent(two_rp_boards):
    assert touchdeck.find_board("esp32c3-128") is None


@pytest.mark.parametrize("text, chip, found", [
    ("UF2 Bootloader v3.0\nModel: Raspberry Pi RP2\nBoard-ID: RPI-RP2\n", None, True),
    ("UF2 Bootloader v1.0\nModel: Raspberry Pi RP2350\nBoard-ID: RP2350\n", None, True),
    ("UF2 Bootloader v1.0\nModel: Raspberry Pi RP2350\nBoard-ID: RP2350\n", "rp2350", True),
    ("UF2 Bootloader v1.0\nModel: Raspberry Pi RP2350\nBoard-ID: RP2350\n", "rp2040", False),
    ("UF2 Bootloader v3.0\nModel: Raspberry Pi RP2\nBoard-ID: RPI-RP2\n", "rp2350", False),
    ("Some other UF2 board\nBoard-ID: SAMD21\n", None, False),
])
def test_uf2_drive_by_chip(tmp_path, monkeypatch, text, chip, found):
    (tmp_path / "INFO_UF2.TXT").write_text(text)
    monkeypatch.setattr(touchdeck, "_drive_roots", lambda: [str(tmp_path)])
    assert (touchdeck.find_uf2_drive(chip) == str(tmp_path)) is found
