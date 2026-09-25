"""Shared helpers for talking to the Touch Deck firmware over its CDC serial port."""
import time

from serial.tools import list_ports

VID, PID = 0xCAFE, 0x4011          # RP2040 firmware (src/usb_descriptors.c)
ESP_VID, ESP_PID = 0x303A, 0x1001  # ESP32-C3 built-in USB-Serial-JTAG (esp32c3/)
SDK_VID, SDK_PID = 0x2E8A, 0x000A  # stock Pico SDK stdio-USB firmware

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


def find_uf2_drive():
    """Return the drive root of a board sitting in the UF2 bootloader, if any."""
    import string, os
    for letter in string.ascii_uppercase[2:]:
        info = f"{letter}:\\INFO_UF2.TXT"
        try:
            if os.path.exists(info) and "RPI-RP2" in open(info).read():
                return f"{letter}:\\"
        except OSError:
            pass
    return None


def wait_for(fn, timeout=10.0, interval=0.2):
    end = time.time() + timeout
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(interval)
    return None
