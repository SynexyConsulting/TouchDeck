import Foundation

/// One low-level input action, as handed to the event sink.
public enum InputEvent: Equatable {
    /// A macOS virtual key code (kVK_*) pressed or released.
    case key(code: UInt16, up: Bool)
    /// Relative pointer motion.
    case move(dx: Int, dy: Int)
    case button(MouseAction)
}

public enum MouseAction: Equatable, Hashable {
    case leftDown, leftUp, rightDown, rightUp, middleDown, middleUp
}

/// Where injected input goes: the real desktop, or a recorder (tests, dry run).
public protocol InputSink: AnyObject {
    func send(_ events: [InputEvent])
}

/// The Mac's Caps Lock state (reported to the board as LEDS so typed case stays right).
public protocol KeyboardState {
    var capsLock: Bool { get }
}

/// Turns the board's HID-style reports (ESP32-C3 PC output mode) into macOS key codes.
/// Same ordering rules as the Windows Injector; only the key table differs (kVK codes, not scancodes).
public final class Injector {
    /// HID keyboard usage -> macOS virtual key code (Carbon kVK_*).
    static let keyCodes: [Int: UInt16] = {
        // a..z (HID 0x04..0x1D)
        let letters: [UInt16] = [0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
                                 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06]
        // 1..9, 0 (HID 0x1E..0x27)
        let digits: [UInt16] = [0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19, 0x1D]
        var map: [Int: UInt16] = [:]
        for (i, k) in letters.enumerated() { map[0x04 + i] = k }
        for (i, k) in digits.enumerated() { map[0x1E + i] = k }
        map[0x28] = 0x24   // Return
        map[0x29] = 0x35   // Escape
        map[0x2A] = 0x33   // Delete (backspace)
        map[0x2B] = 0x30   // Tab
        map[0x2C] = 0x31   // Space
        map[0x6A] = 0x71   // F15: the jiggler's harmless alternative to Esc
        let punct: [(Int, UInt16)] = [
            (0x2D, 0x1B), (0x2E, 0x18), (0x2F, 0x21), (0x30, 0x1E), (0x31, 0x2A),   // - = [ ] \
            (0x33, 0x29), (0x34, 0x27), (0x35, 0x32), (0x36, 0x2B), (0x37, 0x2F), (0x38, 0x2C),   // ; ' ` , . /
        ]
        for (usage, code) in punct { map[usage] = code }
        return map
    }()

    /// HID modifier bit -> key code: LCtrl LShift LAlt LGUI RCtrl RShift RAlt RGUI.
    /// A PC keyboard's GUI key is the Mac's Command key.
    static let modifiers: [(bit: Int, code: UInt16)] = [
        (0x01, 0x3B), (0x02, 0x38), (0x04, 0x3A), (0x08, 0x37),
        (0x10, 0x3E), (0x20, 0x3C), (0x40, 0x3D), (0x80, 0x36),
    ]

    static let buttons: [(bit: Int, down: MouseAction, up: MouseAction)] = [
        (0x01, .leftDown, .leftUp), (0x02, .rightDown, .rightUp), (0x04, .middleDown, .middleUp),
    ]

    private let sink: InputSink
    public private(set) var heldKey = 0
    public private(set) var heldMods = 0
    public private(set) var heldButtons = 0

    public init(sink: InputSink) { self.sink = sink }

    /// A keyboard report: modifier byte + one usage (0 = release all).
    public func key(mods: Int, usage: Int) {
        if usage != 0 && Self.keyCodes[usage] == nil { return }   // unknown key: ignore the whole report
        var events: [InputEvent] = []
        if heldKey != 0 && heldKey != usage {                     // 1. old key up
            events.append(.key(code: Self.keyCodes[heldKey]!, up: true))
            heldKey = 0
        }
        for m in Self.modifiers where heldMods & m.bit != 0 && mods & m.bit == 0 {   // 2. dropped modifiers up
            events.append(.key(code: m.code, up: true))
        }
        for m in Self.modifiers where mods & m.bit != 0 && heldMods & m.bit == 0 {   // 3. new modifiers down
            events.append(.key(code: m.code, up: false))
        }
        heldMods = mods
        if usage != 0 && usage != heldKey {                       // 4. new key down
            events.append(.key(code: Self.keyCodes[usage]!, up: false))
            heldKey = usage
        }
        send(events)
    }

    /// A mouse report: button byte + relative motion.
    public func mouse(buttons: Int, dx: Int, dy: Int) {
        var events: [InputEvent] = []
        if dx != 0 || dy != 0 { events.append(.move(dx: dx, dy: dy)) }
        for b in Self.buttons {
            if buttons & b.bit != 0 && heldButtons & b.bit == 0 { events.append(.button(b.down)) }
            else if heldButtons & b.bit != 0 && buttons & b.bit == 0 { events.append(.button(b.up)) }
        }
        heldButtons = buttons
        send(events)
    }

    /// Let go of every key and button (link lost, app quitting, mode switch).
    public func releaseAll() {
        key(mods: 0, usage: 0)
        mouse(buttons: 0, dx: 0, dy: 0)
    }

    private func send(_ events: [InputEvent]) {
        if !events.isEmpty { sink.send(events) }
    }
}

/// Keeps what would have been injected. Used by tests and by dry-run mode.
public final class RecordingSink: InputSink {
    public private(set) var events: [InputEvent] = []
    public var sent: (([InputEvent]) -> Void)?
    public init() {}
    public func send(_ events: [InputEvent]) {
        self.events += events
        sent?(events)
    }
}

/// Real injection, or (dry run) a description of each event instead. Switchable live.
/// Tracks what is down on the real desktop, so turning dry run on mid-paste releases it there;
/// otherwise the matching key-up would only be logged and the key would stick.
public final class SwitchableSink: InputSink {
    private let real: InputSink
    private let lock = NSLock()
    private var keysDown = Set<UInt16>()
    private var buttonsDown = Set<MouseAction>()
    private var dryRunValue = false

    /// Dry-run descriptions, e.g. "key down" (never which key).
    public var dryRunEvent: ((String) -> Void)?

    public init(real: InputSink) { self.real = real }

    public var dryRun: Bool {
        get { lock.withLock { dryRunValue } }
        set {
            lock.withLock {
                if newValue && !dryRunValue { releaseReal() }
                dryRunValue = newValue
            }
        }
    }

    public func send(_ events: [InputEvent]) {
        let dry: Bool = lock.withLock {
            if !dryRunValue {
                for e in events { track(e) }
                real.send(events)
            }
            return dryRunValue
        }
        guard dry else { return }
        for e in events { dryRunEvent?(Self.describe(e)) }
    }

    static func describe(_ e: InputEvent) -> String {
        switch e {
        case .key(_, let up): return up ? "key up" : "key down"
        case .move(let dx, let dy): return "mouse move \(dx),\(dy)"
        case .button(let a):
            switch a {
            case .leftDown: return "left button down"
            case .leftUp: return "left button up"
            case .rightDown: return "right button down"
            case .rightUp: return "right button up"
            case .middleDown: return "middle button down"
            case .middleUp: return "middle button up"
            }
        }
    }

    private func track(_ e: InputEvent) {
        switch e {
        case .key(let code, let up): if up { keysDown.remove(code) } else { keysDown.insert(code) }
        case .button(let a):
            switch a {
            case .leftDown, .rightDown, .middleDown: buttonsDown.insert(a)
            case .leftUp: buttonsDown.remove(.leftDown)
            case .rightUp: buttonsDown.remove(.rightDown)
            case .middleUp: buttonsDown.remove(.middleDown)
            }
        case .move: break
        }
    }

    private func releaseReal() {
        var up: [InputEvent] = keysDown.map { .key(code: $0, up: true) }
        for b in buttonsDown {
            switch b {
            case .leftDown: up.append(.button(.leftUp))
            case .rightDown: up.append(.button(.rightUp))
            case .middleDown: up.append(.button(.middleUp))
            default: break
            }
        }
        keysDown.removeAll()
        buttonsDown.removeAll()
        if !up.isEmpty { real.send(up) }
    }
}
