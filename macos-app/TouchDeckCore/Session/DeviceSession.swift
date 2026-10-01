import Foundation

public protocol Clock {
    var now: Date { get }
}

public struct SystemClock: Clock {
    public init() {}
    public var now: Date { Date() }
}

/// Board identity from VER. Firmware older than 1.5.0 has no VER and is `unknown`.
public struct FirmwareInfo: Equatable {
    public var board: String
    public var version: String
    public var build: String
    public init(board: String, version: String, build: String) { self.board = board; self.version = version; self.build = build }

    public static let unknown = FirmwareInfo(board: "?", version: "unknown", build: "")
    public var known: Bool { self != .unknown }
    public var semVer: SemVer? { SemVer(version) }
}

/// major.minor.patch, compared numerically ("1.10.0" > "1.9.0").
public struct SemVer: Comparable, CustomStringConvertible {
    public var major: Int, minor: Int, patch: Int

    public init(_ major: Int, _ minor: Int, _ patch: Int) { self.major = major; self.minor = minor; self.patch = patch }

    public init?(_ s: String) {
        let parts = s.split(separator: ".").map { Int($0) }
        guard (2...4).contains(parts.count), parts.allSatisfy({ $0 != nil && $0! >= 0 }) else { return nil }
        major = parts[0]!; minor = parts[1]!; patch = parts.count > 2 ? parts[2]! : 0
    }

    public static func < (a: SemVer, b: SemVer) -> Bool { (a.major, a.minor, a.patch) < (b.major, b.minor, b.patch) }
    public var description: String { "\(major).\(minor).\(patch)" }
}

/// One connected board: the port of the Windows DeviceSession. Single-threaded by design:
/// `run` owns the transport on its own thread; other threads only queue requests.
public final class DeviceSession: @unchecked Sendable {
    private static let heartbeatEvery: TimeInterval = 2
    private static let capsPollEvery: TimeInterval = 0.25
    private static let timeSyncEvery: TimeInterval = 3600
    private static let readSlice: TimeInterval = 0.02
    private static let mirrorSince = SemVer(1, 6, 0)

    private let transport: SerialTransport
    public let injector: Injector
    private let keyboard: KeyboardState
    private let selection: SelectionProvider
    private let clock: Clock

    private let requestLock = NSLock()
    private var requests: [() throws -> Void] = []
    private var held: [String] = []           // lines read while waiting for a reply
    private var lastHeartbeat = Date.distantPast, lastCapsPoll = Date.distantPast, lastTimeSync = Date.distantPast
    private var caps: Bool?                    // nil forces the first LEDS report
    private var started = false
    private var handshaken = false
    private var watchSentAt: Date?
    private let stateLock = NSLock()
    private var mirrorSupportedValue: Bool?

    /// Which board this is (set by `DeviceManager` before the session starts).
    public var kind: BoardKind = .rp2040
    public private(set) var firmware: FirmwareInfo?
    /// Poll DBG instead of PING, feeding `onDiagnostics`.
    public var diagnosticsEnabled = false
    public private(set) var lastState: StateReport?

    // Events, raised on the session thread.
    public var onLog: ((String) -> Void)?
    /// Text sent to the board: (ascii text, source, characters that became '?').
    public var onClipSent: ((String, String, Int) -> Void)?
    public var onDiagnostics: (([String: String]) -> Void)?
    public var onState: ((StateReport) -> Void)?
    public var onText: ((String, String) -> Void)?
    public var onClipText: (([UInt8]) -> Void)?

    public init(transport: SerialTransport, injector: Injector, keyboard: KeyboardState,
                selection: SelectionProvider, clock: Clock = SystemClock()) {
        self.transport = transport
        self.injector = injector
        self.keyboard = keyboard
        self.selection = selection
        self.clock = clock
    }

    /// nil until known; false when the board sent no STATE within 1.5 s of WATCH 1 (older firmware).
    public var mirrorSupported: Bool? {
        get { stateLock.withLock { mirrorSupportedValue } }
        set { stateLock.withLock { mirrorSupportedValue = newValue } }
    }

    /// HELLO must be answered by PONG, else the port isn't a Touch Deck. Then VER, TIME and WATCH 1.
    public func handshake(timeout: TimeInterval = 1.5) throws -> Bool {
        try transport.writeLine("")               // flush any half line the board holds
        try transport.writeLine("HELLO")
        guard try waitFor(timeout, { if case .pong = $0 { return true }; return false }) != nil else { return false }
        try transport.writeLine("VER")
        if case .version(let board, let version, let build)? = try waitFor(0.5, { if case .version = $0 { return true }; return false }) {
            firmware = FirmwareInfo(board: board, version: version, build: build)
        } else {
            firmware = .unknown
        }
        try sendTime()
        try transport.writeLine("WATCH 1")        // the app mirror; older firmware ignores it
        watchSentAt = clock.now
        // STATE arrived in firmware 1.6.0: an older (or unknown) version has no mirror.
        if let v = firmware?.semVer, v >= Self.mirrorSince {} else { mirrorSupported = false }
        handshaken = true
        return true
    }

    /// Runs until `shouldStop` says so or the transport fails; never leaves input held on the Mac.
    public func run(shouldStop: () -> Bool) throws {
        defer { injector.releaseAll() }
        if !handshaken {
            guard try handshake() else { throw SerialError.io(ETIMEDOUT) }   // no PONG: not a Touch Deck
        }
        while !shouldStop() { try step(wait: Self.readSlice) }
    }

    /// One pass: queued requests, at most one board line, then timers.
    public func step(wait: TimeInterval = 0) throws {
        var now = clock.now
        if !started {
            started = true
            lastHeartbeat = now
            lastTimeSync = now
            lastCapsPoll = now.addingTimeInterval(-Self.capsPollEvery)
        }
        let queued: [() throws -> Void] = requestLock.withLock { defer { requests.removeAll() }; return requests }
        for r in queued { try r() }

        let line = try held.isEmpty ? transport.readLine(timeout: wait) : held.removeFirst()
        if let line { try handle(BoardLine.parse(Self.trimEol(line))) }

        now = clock.now
        if now.timeIntervalSince(lastCapsPoll) >= Self.capsPollEvery {
            lastCapsPoll = now
            let c = keyboard.capsLock
            if c != caps {
                caps = c
                try transport.writeLine(c ? "LEDS 02" : "LEDS 00")
            }
        }
        if now.timeIntervalSince(lastHeartbeat) >= Self.heartbeatEvery {
            lastHeartbeat = now
            try transport.writeLine(diagnosticsEnabled ? "DBG" : "PING")
        }
        if mirrorSupported == nil, let w = watchSentAt, now.timeIntervalSince(w) >= 1.5 {
            mirrorSupported = false
        }
        if now.timeIntervalSince(lastTimeSync) >= Self.timeSyncEvery {
            lastTimeSync = now
            try sendTime()
        }
    }

    // MARK: requests (any thread)

    private func enqueue(_ r: @escaping () throws -> Void) { requestLock.withLock { requests.append(r) } }
    private func enqueueLine(_ line: String) { enqueue { [transport] in try transport.writeLine(line) } }

    /// Push text into the board's clip (queued; sent on the session thread).
    public func sendText(_ text: String, source: String = "app") { enqueue { [weak self] in try self?.sendClip(text, source: source) } }
    /// Reboot the board into its bootloader (RP boards: the UF2 drive).
    public func requestBootloader() { enqueueLine("BOOT") }
    /// Change the board's page, as a finger swipe would.
    public func swipe(left: Bool) { enqueueLine(left ? "SWIPE L" : "SWIPE R") }
    /// Press the board's BOOT button (stopwatch on the watch, scale on the jiggler).
    public func pressButton(long: Bool) { enqueueLine(long ? "BTN LONG" : "BTN") }
    /// A tap on the board's touch panel at (x, y), device pixels (the device mirror).
    public func tap(x: Int, y: Int) { enqueueLine("TAP \(x) \(y)") }
    /// ANIM 1|0: the jiggler page animates its dot without sending HID (demos, tests).
    public func animate(_ on: Bool) { enqueueLine(on ? "ANIM 1" : "ANIM 0") }
    /// Turn the board's jiggler on or off (saved on the board).
    public func setJiggler(_ on: Bool) { enqueueLine(on ? "JIG ON" : "JIG OFF") }
    /// Jiggler scale index 0..2 (1x, 1.5x, 2x).
    public func setScale(_ index: Int) { if (0...2).contains(index) { enqueueLine("JIG SCALE \(index)") } }
    /// The board's Jiggler settings (firmware 1.8.0+): context menu, F15 instead of Esc, seconds
    /// the menu stays open, seconds to pause before the next letter (each clamped to 0-60).
    public func setJigConfig(menuOn: Bool, f15: Bool, openS: Int, pauseS: Int) {
        let open = min(max(openS, 0), 60), pause = min(max(pauseS, 0), 60)
        enqueueLine("JIG CFG \(menuOn ? 1 : 0) \(f15 ? 1 : 0) \(open) \(pause)")
    }
    /// Empty the board's clip (the trash can); the board ignores it while pasting.
    public func clearClip() { enqueueLine("CLIP CLEAR") }
    /// Any protocol line (tests).
    func sendRaw(_ line: String) { enqueueLine(line) }

    // MARK: session thread

    private func handle(_ message: BoardMessage) throws {
        switch message {
        case .state(let st):
            lastState = st
            mirrorSupported = true
            onState?(st)
        case .text(let key, let value):
            onText?(key, value)
        case .clipText(let bytes):
            onClipText?(bytes)
        case .copyRequest:
            let (text, source) = selection.grab()
            try sendClip(text, source: source)
        case .key(let mods, let usage):
            injector.key(mods: mods, usage: usage)
        case .mouse(let buttons, let dx, let dy):
            injector.mouse(buttons: buttons, dx: dx, dy: dy)
        case .log(let t) where t.hasPrefix("up="):
            onDiagnostics?(DbgFields.parse(t))        // the DBG reply always starts with uptime
        case .log(let t):
            onLog?(t)
        default:
            break
        }
    }

    private func sendClip(_ text: String, source: String) throws {
        var (ascii, lost) = AsciiText.transliterate(text)
        if ascii.utf8.count > ClipMessage.maxBytes { ascii = String(ascii.prefix(ClipMessage.maxBytes)) }
        try transport.write(ClipMessage.encode(ascii, source: source))
        onClipSent?(ascii, source, lost)
    }

    private func sendTime() throws {
        let c = Calendar.current.dateComponents([.hour, .minute, .second], from: clock.now)
        try transport.writeLine(String(format: "TIME %02d:%02d:%02d", c.hour ?? 0, c.minute ?? 0, c.second ?? 0))
    }

    private func waitFor(_ timeout: TimeInterval, _ match: (BoardMessage) -> Bool) throws -> BoardMessage? {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            guard let line = try transport.readLine(timeout: 0.05) else { continue }
            let msg = BoardLine.parse(Self.trimEol(line))
            if match(msg) { return msg }
            held.append(line)                      // e.g. a K report racing the handshake
        }
        return nil
    }

    /// Only CR/LF are trimmed, so TEXT values keep their spaces.
    static func trimEol(_ s: String) -> String {
        var scalars = Substring(s).unicodeScalars    // "\r\n" is one Character, so work on scalars
        while let last = scalars.last, last == "\r" || last == "\n" { scalars.removeLast() }
        return String(scalars)
    }
}
