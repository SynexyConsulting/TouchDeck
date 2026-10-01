import Foundation
import IOKit
import IOKit.serial

public enum BoardKind: Int, Comparable {
    /// RP2040 / RP2350 Touch Deck firmware (USB CAFE:4011): native USB keyboard/mouse.
    case rp2040 = 0
    /// ESP32-C3 Touch Deck (USB 303A:1001): Bluetooth or PC-mode output.
    case esp32c3 = 1

    public static func < (a: BoardKind, b: BoardKind) -> Bool { a.rawValue < b.rawValue }
}

public enum BoardKinds {
    public static func fromUsb(vid: Int, pid: Int) -> BoardKind? {
        switch (vid, pid) {
        case (0xCAFE, 0x4011): return .rp2040
        case (0x303A, 0x1001): return .esp32c3
        default: return nil
        }
    }

    /// The RP boards (TinyUSB) only transmit while DTR is asserted. On the ESP32-C3's
    /// USB-Serial-JTAG, DTR/RTS are the reset and boot-mode lines: both stay low, or
    /// opening the port reboots the chip.
    public static func dtrHigh(_ kind: BoardKind) -> Bool { kind == .rp2040 }

    public static func displayName(_ kind: BoardKind, board: String? = nil) -> String {
        if board == "rp2350-128" { return "RP2350 Touch Deck (round)" }
        switch kind {
        case .rp2040: return "RP2040 Touch Deck"
        case .esp32c3: return "ESP32-C3 Touch Deck"
        }
    }
}

public struct UsbId: Equatable, Hashable {
    public var vid: Int
    public var pid: Int
    public init(vid: Int, pid: Int) { self.vid = vid; self.pid = pid }
}

/// A serial port that may be a Touch Deck. `port` is the call-out device, e.g. /dev/cu.usbmodem1101.
public struct DeviceCandidate: Equatable, Hashable {
    public var port: String
    public var kind: BoardKind
    public var usb: UsbId
    public init(port: String, kind: BoardKind, usb: UsbId) { self.port = port; self.kind = kind; self.usb = usb }
}

/// A USB serial port as IOKit reports it.
public struct SerialPortInfo: Equatable {
    public var path: String
    public var usb: UsbId?
    public init(path: String, usb: UsbId?) { self.path = path; self.usb = usb }
}

public enum DeviceScanner {
    /// Touch Deck serial ports currently present, RP boards first.
    public static func scan() -> [DeviceCandidate] { fromPorts(UsbRegistry.serialPorts()) }

    /// The pure part of `scan`: classify ports by their USB ids.
    public static func fromPorts(_ ports: [SerialPortInfo]) -> [DeviceCandidate] {
        ports.compactMap { p in
            guard let usb = p.usb, let kind = BoardKinds.fromUsb(vid: usb.vid, pid: usb.pid) else { return nil }
            return DeviceCandidate(port: p.path, kind: kind, usb: usb)
        }
        .sorted { ($0.kind, $0.port) < ($1.kind, $1.port) }
    }
}

/// IOKit queries: serial ports with the USB ids of the device they belong to, and USB devices.
public enum UsbRegistry {
    public static func serialPorts() -> [SerialPortInfo] {
        var result: [SerialPortInfo] = []
        guard let matching = IOServiceMatching(kIOSerialBSDServiceValue) else { return result }
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator) == KERN_SUCCESS else { return result }
        defer { IOObjectRelease(iterator) }
        while case let service = IOIteratorNext(iterator), service != 0 {
            defer { IOObjectRelease(service) }
            guard let path = IORegistryEntryCreateCFProperty(service, kIOCalloutDeviceKey as CFString, kCFAllocatorDefault, 0)?
                .takeRetainedValue() as? String else { continue }
            result.append(SerialPortInfo(path: path, usb: usbId(of: service)))
        }
        return result
    }

    /// USB devices (vid, pid) present, including ones without a serial port (a UF2 bootloader).
    public static func usbDevices() -> [UsbId] {
        var result: [UsbId] = []
        guard let matching = IOServiceMatching("IOUSBHostDevice") else { return result }
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator) == KERN_SUCCESS else { return result }
        defer { IOObjectRelease(iterator) }
        while case let service = IOIteratorNext(iterator), service != 0 {
            defer { IOObjectRelease(service) }
            if let id = usbId(of: service) { result.append(id) }
        }
        return result
    }

    /// idVendor/idProduct of the entry or the nearest USB device above it in the registry.
    private static func usbId(of service: io_object_t) -> UsbId? {
        let options = IOOptionBits(kIORegistryIterateRecursively | kIORegistryIterateParents)
        guard let vid = IORegistryEntrySearchCFProperty(service, kIOServicePlane, "idVendor" as CFString, kCFAllocatorDefault, options) as? Int,
              let pid = IORegistryEntrySearchCFProperty(service, kIOServicePlane, "idProduct" as CFString, kCFAllocatorDefault, options) as? Int
        else { return nil }
        return UsbId(vid: vid, pid: pid)
    }
}
