import XCTest
@testable import TouchDeckCore

final class InjectorTests: XCTestCase {
    private let sink = RecordingSink()
    private lazy var injector = Injector(sink: sink)

    func testShiftedLetterPressesShiftFirstAndReleasesInOrder() {
        injector.key(mods: 0x02, usage: 0x04)     // Shift + a
        injector.key(mods: 0, usage: 0)
        XCTAssertEqual(sink.events, [
            .key(code: 0x38, up: false), .key(code: 0x00, up: false),   // shift down, a down
            .key(code: 0x00, up: true), .key(code: 0x38, up: true),     // a up, shift up
        ])
    }

    func testUnknownUsageIgnoresTheWholeReport() {
        injector.key(mods: 0x02, usage: 0x99)
        XCTAssertTrue(sink.events.isEmpty)
        XCTAssertEqual(injector.heldMods, 0)
    }

    func testF15AndEscMapToMacKeyCodes() {
        injector.key(mods: 0, usage: 0x6A)
        injector.key(mods: 0, usage: 0x29)
        XCTAssertEqual(sink.events, [.key(code: 0x71, up: false), .key(code: 0x71, up: true), .key(code: 0x35, up: false)])
    }

    func testMouseButtonsAndMotion() {
        injector.mouse(buttons: 0x02, dx: 3, dy: -1)
        injector.mouse(buttons: 0, dx: 0, dy: 0)
        XCTAssertEqual(sink.events, [.move(dx: 3, dy: -1), .button(.rightDown), .button(.rightUp)])
    }

    func testReleaseAllLetsGoOfEverything() {
        injector.key(mods: 0x01, usage: 0x05)
        injector.mouse(buttons: 0x01, dx: 0, dy: 0)
        injector.releaseAll()
        XCTAssertEqual(injector.heldKey, 0)
        XCTAssertEqual(injector.heldMods, 0)
        XCTAssertEqual(injector.heldButtons, 0)
        XCTAssertEqual(Array(sink.events.suffix(3)), [.key(code: 0x0B, up: true), .key(code: 0x3B, up: true), .button(.leftUp)])
    }

    func testDryRunMidPasteReleasesWhatIsDownOnTheRealDesktop() {
        let real = RecordingSink()
        let sw = SwitchableSink(real: real)
        var logged: [String] = []
        sw.dryRunEvent = { logged.append($0) }
        sw.send([.key(code: 0x00, up: false), .button(.leftDown)])
        sw.dryRun = true
        sw.send([.key(code: 0x00, up: true)])
        XCTAssertTrue(real.events.contains(.key(code: 0x00, up: true)))
        XCTAssertTrue(real.events.contains(.button(.leftUp)))
        XCTAssertEqual(logged, ["key up"])                       // never which key
    }
}

/// A scripted board: lines queued for reading, everything written recorded.
final class FakeTransport: SerialTransport {
    var incoming: [String] = []
    var written: [UInt8] = []
    var replies: [String: [String]] = [:]     // written line -> lines the board answers
    var openError: Error?

    var writtenLines: [String] { String(decoding: written, as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: false).map(String.init) }

    func open() throws { if let openError { throw openError } }
    func write(_ data: [UInt8]) throws {
        written += data
        let line = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .newlines)
        incoming += replies[line] ?? []
    }
    func readLine(timeout: TimeInterval) throws -> String? { incoming.isEmpty ? nil : incoming.removeFirst() }
    func close() {}
}

struct FakeKeyboard: KeyboardState { var capsLock = false }

struct FakeSelection: SelectionProvider {
    var text = "picked"
    func grab() -> (text: String, source: String) { (text, "select") }
}

final class FakeClock: Clock {
    var now = Date(timeIntervalSince1970: 1_700_000_000)
}

final class SessionTests: XCTestCase {
    private func board(version: String? = "1.8.0") -> FakeTransport {
        let t = FakeTransport()
        t.replies["HELLO"] = ["PONG"]
        if let version { t.replies["VER"] = ["VERSION rp2040-169 \(version) Oct 1 2026"] }
        return t
    }

    private func session(_ t: FakeTransport, sink: RecordingSink = RecordingSink(), clock: FakeClock = FakeClock()) -> DeviceSession {
        DeviceSession(transport: t, injector: Injector(sink: sink), keyboard: FakeKeyboard(), selection: FakeSelection(), clock: clock)
    }

    func testHandshakeSendsHelloVerTimeAndWatch() throws {
        let t = board()
        let s = session(t)
        XCTAssertTrue(try s.handshake())
        XCTAssertEqual(s.firmware?.version, "1.8.0")
        let lines = t.writtenLines
        XCTAssertEqual(Array(lines.prefix(3)), ["", "HELLO", "VER"])
        XCTAssertTrue(lines[3].hasPrefix("TIME "))
        XCTAssertEqual(lines[4], "WATCH 1")
    }

    func testNoPongMeansNotATouchDeck() throws {
        let t = FakeTransport()
        XCTAssertFalse(try session(t).handshake(timeout: 0.1))
    }

    func testOldFirmwareHasNoMirror() throws {
        let s = session(board(version: "1.5.0"))
        _ = try s.handshake()
        XCTAssertEqual(s.mirrorSupported, false)
    }

    func testLinesRacingTheHandshakeAreHeldNotDropped() throws {
        let t = board()
        t.replies["HELLO"] = ["K 00 04", "PONG"]
        let sink = RecordingSink()
        let s = session(t, sink: sink)
        _ = try s.handshake()
        try s.step()
        XCTAssertEqual(sink.events, [.key(code: 0x00, up: false)])
    }

    func testCopyRequestSendsTheSelection() throws {
        let t = board()
        let s = session(t)
        _ = try s.handshake()
        t.incoming = ["COPY"]
        try s.step()
        XCTAssertTrue(String(decoding: t.written, as: UTF8.self).hasSuffix("CLIP 6 select\npicked"))
    }

    func testRequestsAreSentOnTheSessionThread() throws {
        let t = board()
        let s = session(t)
        _ = try s.handshake()
        s.setJigConfig(menuOn: true, f15: true, openS: 99, pauseS: -3)
        s.tap(x: 10, y: 20)
        XCTAssertFalse(t.writtenLines.contains("TAP 10 20"))
        try s.step()
        XCTAssertTrue(t.writtenLines.contains("JIG CFG 1 1 60 0"))
        XCTAssertTrue(t.writtenLines.contains("TAP 10 20"))
    }

    func testRunAlwaysReleasesHeldInput() throws {
        let t = board()
        let sink = RecordingSink()
        let s = session(t, sink: sink)
        _ = try s.handshake()
        t.incoming = ["K 02 04"]
        var steps = 0
        try s.run { steps += 1; return steps > 3 }
        XCTAssertEqual(sink.events.last, .key(code: 0x38, up: true))
    }

    func testStateMarksTheMirrorSupported() throws {
        let t = board()
        let s = session(t)
        _ = try s.handshake()
        var got: StateReport?
        s.onState = { got = $0 }
        t.incoming = ["STATE jig=0 letter=O scale=0 phase=0 x=0 y=0 clip=0 paste=0"]
        try s.step()
        XCTAssertEqual(s.mirrorSupported, true)
        XCTAssertEqual(got?.letter, "O")
    }
}

final class ManagerTests: XCTestCase {
    private let rp = DeviceCandidate(port: "/dev/cu.usbmodem1", kind: .rp2040, usb: UsbId(vid: 0xCAFE, pid: 0x4011))

    func testBusyPortIsReported() {
        let t = FakeTransport()
        t.openError = SerialError.busy
        let m = DeviceManager(scan: { [self.rp] }, openTransport: { _ in t },
                              makeSession: { DeviceSession(transport: $0, injector: Injector(sink: RecordingSink()), keyboard: FakeKeyboard(), selection: FakeSelection()) })
        m.tick()
        XCTAssertEqual(m.state.status, .portBusy)
    }

    func testSilentPortIsNotResponding() {
        let m = DeviceManager(scan: { [self.rp] }, openTransport: { _ in FakeTransport() },
                              makeSession: { DeviceSession(transport: $0, injector: Injector(sink: RecordingSink()), keyboard: FakeKeyboard(), selection: FakeSelection()) })
        m.tick()
        XCTAssertEqual(m.state.status, .notResponding)
    }

    func testScannerKeepsTouchDecksRpFirst() {
        let ports = [
            SerialPortInfo(path: "/dev/cu.usbmodem9", usb: UsbId(vid: 0x303A, pid: 0x1001)),
            SerialPortInfo(path: "/dev/cu.Bluetooth-Incoming-Port", usb: nil),
            SerialPortInfo(path: "/dev/cu.usbmodem3", usb: UsbId(vid: 0xCAFE, pid: 0x4011)),
            SerialPortInfo(path: "/dev/cu.usbmodem5", usb: UsbId(vid: 0x2E8A, pid: 0x000A)),
        ]
        XCTAssertEqual(DeviceScanner.fromPorts(ports).map(\.port), ["/dev/cu.usbmodem3", "/dev/cu.usbmodem9"])
        XCTAssertFalse(BoardKinds.dtrHigh(.esp32c3))
        XCTAssertTrue(BoardKinds.dtrHigh(.rp2040))
    }
}
