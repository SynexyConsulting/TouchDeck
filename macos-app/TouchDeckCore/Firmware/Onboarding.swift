import Foundation

/// A Touch Deck model the app can install firmware on (a UF2 board).
public struct BoardModel: Equatable, Hashable, Identifiable {
    public var board: String
    public var name: String
    public var chip: Uf2Chip
    public var id: String { board }
}

public enum BoardModels {
    public static let all: [BoardModel] = [
        BoardModel(board: "rp2040-169", name: "Touch LCD 1.69 (rectangle)", chip: .rp2040),
        BoardModel(board: "rp2350-128", name: "Touch LCD 1.28 (round)", chip: .rp2350),
    ]

    /// The models built for a chip. The chip is known from USB; the screen isn't.
    public static func `for`(_ chip: Uf2Chip) -> [BoardModel] { all.filter { $0.chip == chip } }
    public static func find(_ board: String) -> BoardModel? { all.first { $0.board == board } }
}

public enum NewBoardState: Equatable {
    /// Running a stock Pico SDK program with USB serial (e.g. the factory demo).
    case stockFirmware
    /// Sitting in its UF2 bootloader (a volume named RPI-RP2 or RP2350).
    case bootloader
}

/// A Raspberry Pi board that isn't running Touch Deck yet.
public struct NewBoard: Equatable {
    public var chip: Uf2Chip
    public var state: NewBoardState
    public var port: String?

    public func describe() -> String {
        "\(chip == .rp2350 ? "RP2350" : "RP2040") board " +
            (state == .bootloader ? "in its bootloader" : "with other firmware (\(port ?? "no port"))")
    }
}

public enum NewBoards {
    /// Raspberry Pi USB IDs (VID 2E8A): stock SDK programs with USB serial, and the bootloaders.
    static func fromUsb(_ id: UsbId) -> (Uf2Chip, NewBoardState)? {
        switch (id.vid, id.pid) {
        case (0x2E8A, 0x000A): return (.rp2040, .stockFirmware)
        case (0x2E8A, 0x0003): return (.rp2040, .bootloader)
        case (0x2E8A, 0x0009): return (.rp2350, .stockFirmware)
        case (0x2E8A, 0x000F): return (.rp2350, .bootloader)
        default: return nil
        }
    }

    public static func scan() -> [NewBoard] {
        fromRegistry(devices: UsbRegistry.usbDevices(), ports: UsbRegistry.serialPorts())
    }

    /// The pure part of `scan`: one entry per board kind, with its serial port when it has one.
    public static func fromRegistry(devices: [UsbId], ports: [SerialPortInfo]) -> [NewBoard] {
        var found: [NewBoard] = []
        for id in Set(devices) {
            guard let (chip, state) = fromUsb(id) else { continue }
            if found.contains(where: { $0.chip == chip && $0.state == state }) { continue }
            let port = ports.filter { $0.usb == id }.map(\.path).sorted().first
            found.append(NewBoard(chip: chip, state: state, port: port))
        }
        return found.sorted {
            ($0.chip == .rp2350 ? 1 : 0, $0.state == .bootloader ? 1 : 0) < ($1.chip == .rp2350 ? 1 : 0, $1.state == .bootloader ? 1 : 0)
        }
    }

    /// Reboots a stock Pico SDK program into its bootloader: opening its USB serial port at 1200
    /// baud is the SDK's reset signal. False if the port couldn't be opened.
    public static func rebootToBootloader(_ port: String) -> Bool { PosixSerialTransport.touch1200(port) }
}
