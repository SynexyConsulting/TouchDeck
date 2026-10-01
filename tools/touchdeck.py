"""Shared helpers for talking to the Touch Deck firmware over its CDC serial port."""
import time

from serial.tools import list_ports

VID, PID = 0xCAFE, 0x4011          # RP2040 firmware (src/usb_descriptors.c)
ESP_VID, ESP_PID = 0x303A, 0x1001  # ESP32-C3 built-in USB-Serial-JTAG (esp32c3/)
SDK_VID, SDK_PID = 0x2E8A, 0x000A  # stock Pico SDK stdio-USB firmware (RP2040)
SDK_PID_RP2350 = 0x0009            # the same on an RP2350 (e.g. the 1.28 factory demo)

BOARDS = [(VID, PID), (ESP_VID, ESP_PID)]


def find_port(vid=None, pid=None):
    """Port of the given VID:PID, or of any Touch Deck board if none given."""
    wanted = [(vid, pid)] if vid is not None else BOARDS
    for v, p in wanted:
        for port in list_ports.comports():
            if port.vid == v and port.pid == p:
                return port.device
    return None


def open_serial(port, timeout=0.1):
    """Open a Touch Deck port with the right control-line setup.

    The RP2040 firmware (TinyUSB) only sends when DTR is asserted. The
    ESP32-C3's USB-Serial-JTAG treats DTR/RTS as its reset and boot-mode
    lines, so both must stay low there or opening the port reboots the chip.
    """
    import serial
    info = next((p for p in list_ports.comports() if p.device == port), None)
    esp = info is not None and info.vid == ESP_VID
    ser = serial.Serial()
    ser.port = port
    ser.baudrate = 115200
    ser.timeout = timeout
    ser.dtr = not esp
    ser.rts = False
    ser.open()
    return ser


def find_board(model):
    """Port of the Touch Deck board whose VER reports `model` (e.g. "rp2040-169",
    "rp2350-128"). Every RP board enumerates as CAFE:4011, so the USB ID alone
    can't tell a 1.69 from a round RP2350. Busy ports are skipped."""
    for port in list_ports.comports():
        if (port.vid, port.pid) not in BOARDS:
            continue
        try:
            s = open_serial(port.device)
        except OSError:
            continue
        try:
            time.sleep(0.3)
            s.reset_input_buffer()
            s.write(b"VER\n")
            time.sleep(0.4)
            for line in s.read_all().decode(errors="replace").splitlines():
                parts = line.split()
                if len(parts) >= 2 and parts[0] == "VERSION" and parts[1] == model:
                    return port.device
        finally:
            s.close()
    return None


# Board-ID in a bootloader drive's INFO_UF2.TXT, per chip.
UF2_BOARD_IDS = {"rp2040": "RPI-RP2", "rp2350": "RP2350"}


def _drive_roots():
    import string
    return [f"{letter}:\\" for letter in string.ascii_uppercase[2:]]


def find_uf2_drive(chip=None):
    """Drive root of an RP board in its UF2 bootloader (RP2040 or RP2350, or just
    `chip`), if any."""
    import os
    ids = [UF2_BOARD_IDS[chip]] if chip else list(UF2_BOARD_IDS.values())
    for root in _drive_roots():
        info = os.path.join(root, "INFO_UF2.TXT")
        try:
            if not os.path.exists(info):
                continue
            with open(info) as f:
                text = f.read()
        except OSError:
            continue
        board_id = next((l.split(":", 1)[1].strip() for l in text.splitlines() if l.startswith("Board-ID:")), "")
        if board_id in ids:
            return root
    return None


def wait_for(fn, timeout=10.0, interval=0.2):
    end = time.time() + timeout
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(interval)
    return None
