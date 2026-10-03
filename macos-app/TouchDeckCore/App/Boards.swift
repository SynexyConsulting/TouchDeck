import Foundation

/// One activity-log line. Board lines carry their port, so the log can show one board's lines.
/// Port of Boards.cs.
public struct LogEntry: Equatable, Identifiable {
    public let id = UUID()
    public var time: Date
    public var port: String?
    public var text: String

    public init(time: Date = Date(), port: String?, text: String) {
        self.time = time; self.port = port; self.text = text
    }

    private static let clock: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    public var line: String {
        let t = Self.clock.string(from: time)
        return port.map { "\(t)  \($0): \(text)" } ?? "\(t)  \(text)"
    }

    /// With "only the selected board", app lines (no port) stay; other boards' lines go.
    public func shows(selected: String?, onlySelected: Bool) -> Bool { !onlySelected || port == nil || port == selected }

    public static func == (a: LogEntry, b: LogEntry) -> Bool { a.id == b.id }
}

/// Which board's tab is selected when boards come and go.
public enum BoardSelection {
    /// - Parameters:
    ///   - keys: the boards now attached, in tab order.
    ///   - current: the selected board before the change.
    ///   - added: the board that just appeared, if that was the change.
    ///   - preferred: the board used last time (its port), selected when it comes back.
    public static func next(_ keys: [String], current: String?, added: String?, preferred: String?) -> String? {
        guard let first = keys.first else { return nil }
        // A board plugged in doesn't take the tab you're looking at, unless it's the only one or the last used.
        if let added, keys.contains(added), keys.count == 1 || added == preferred { return added }
        if let current, keys.contains(current) { return current }
        return first
    }
}

/// Short names for the tab strip and the log: the model, then where it is.
public enum BoardLabels {
    public static func model(_ kind: BoardKind, board: String?) -> String {
        switch board {
        case "rp2040-169": return "RP2040 1.69"
        case "rp2350-128": return "RP2350 1.28"
        case "esp32c3-128": return "ESP32-C3 1.28"
        default: return kind == .esp32c3 ? "ESP32-C3" : "RP2040"
        }
    }

    public static func model(_ b: NewBoard) -> String {
        (b.chip == .rp2350 ? "RP2350" : "RP2040") + (b.state == .bootloader ? " bootloader" : " (not Touch Deck)")
    }

    /// The tab text: "RP2350 1.28 · usbmodem1101"; a board with no port (a bootloader volume) has none.
    public static func tab(_ model: String, port: String?) -> String {
        guard let port else { return model }
        return "\(model) · \(shortPort(port))"
    }

    /// macOS ports are long (/dev/cu.usbmodem1101): the last part is enough.
    public static func shortPort(_ port: String) -> String {
        port.hasPrefix("/dev/cu.") ? String(port.dropFirst(8)) : port
    }
}
