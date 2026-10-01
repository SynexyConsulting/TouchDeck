import Foundation

public enum LinkStatus: Equatable { case searching, portBusy, notResponding, connected }

/// What the app shows about the board link.
public struct LinkState: Equatable {
    public var status: LinkStatus
    public var device: DeviceCandidate?
    public var firmware: FirmwareInfo?
    public init(_ status: LinkStatus, device: DeviceCandidate? = nil, firmware: FirmwareInfo? = nil) {
        self.status = status; self.device = device; self.firmware = firmware
    }
    public static let searching = LinkState(.searching)
}

/// Finds a Touch Deck and keeps one `DeviceSession` running on it. When the board goes away
/// the session ends (releasing any held input) and scanning resumes. Port of DeviceManager.cs.
public final class DeviceManager: @unchecked Sendable {
    private let scan: () -> [DeviceCandidate]
    private let openTransport: (DeviceCandidate) -> SerialTransport
    private let makeSession: (SerialTransport) -> DeviceSession

    private let lock = NSLock()
    private var stopped = true
    private var sessionRunning = false
    private var sessionStop = false
    private var sessionDone: DispatchSemaphore?
    private var stateValue = LinkState.searching
    private var sessionValue: DeviceSession?

    /// A port to prefer when several boards are plugged in (e.g. the last one used).
    public var preferredPort: String?
    /// While set (during a firmware install), only a board whose VER reports this model is connected:
    /// both RP boards are CAFE:4011, and the manager must not settle on the other one.
    public var requiredBoard: String? {
        get { lock.withLock { requiredBoardValue } }
        set { lock.withLock { requiredBoardValue = newValue } }
    }
    private var requiredBoardValue: String?

    /// Raised on a background thread.
    public var onStateChanged: ((LinkState) -> Void)?
    /// A new session is up: hook its events here (before it starts reading).
    public var onSessionStarted: ((DeviceSession) -> Void)?

    public var state: LinkState { lock.withLock { stateValue } }
    public var session: DeviceSession? { lock.withLock { sessionValue } }

    public init(scan: @escaping () -> [DeviceCandidate],
                openTransport: @escaping (DeviceCandidate) -> SerialTransport,
                makeSession: @escaping (SerialTransport) -> DeviceSession) {
        self.scan = scan
        self.openTransport = openTransport
        self.makeSession = makeSession
    }

    public func start(pollEvery: TimeInterval = 2) {
        lock.withLock { stopped = false }
        let t = Thread { [weak self] in
            while let self, !self.lock.withLock({ self.stopped }) {
                self.tick()
                Thread.sleep(forTimeInterval: pollEvery)
            }
        }
        t.name = "TouchDeck scan"
        t.start()
    }

    /// One scan-and-connect attempt, if no session is running.
    public func tick() {
        if lock.withLock({ sessionRunning }) { return }
        let found = scan()
        guard let device = found.first(where: { $0.port == preferredPort }) ?? found.first else {
            publish(.searching)
            return
        }
        if let required = requiredBoard {
            // Only the board being flashed may connect: try each candidate, keep the one whose VER matches.
            for d in found.sorted(by: { ($0.port == preferredPort ? 0 : 1) < ($1.port == preferredPort ? 0 : 1) }) {
                if tryConnect(d, required: required) { return }
            }
            publish(.searching)
            return
        }
        _ = tryConnect(device, required: nil)
    }

    private func tryConnect(_ device: DeviceCandidate, required: String?) -> Bool {
        let transport = openTransport(device)
        do {
            try transport.open()
        } catch {
            transport.close()                 // another program holds it
            if required == nil { publish(LinkState(.portBusy, device: device)) }
            return false
        }
        let session = makeSession(transport)
        session.kind = device.kind
        guard (try? session.handshake()) == true else {
            transport.close()
            if required == nil { publish(LinkState(.notResponding, device: device)) }
            return false
        }
        if let required, session.firmware?.board != required {
            transport.close()                 // another board: leave it for after the install
            return false
        }
        let done = DispatchSemaphore(value: 0)
        lock.withLock {
            sessionValue = session
            sessionRunning = true
            sessionStop = false
            sessionDone = done
        }
        onSessionStarted?(session)
        publish(LinkState(.connected, device: device, firmware: session.firmware))
        let t = Thread { [weak self] in
            do {
                try session.run { self?.lock.withLock { self?.sessionStop ?? true } ?? true }
            } catch {
                // unplugged: run's defer released held input
            }
            transport.close()
            guard let self else { done.signal(); return }
            let cancelled = self.lock.withLock { () -> Bool in
                self.sessionValue = nil
                self.sessionRunning = false
                return self.sessionStop
            }
            if !cancelled { self.publish(.searching) }
            done.signal()
        }
        t.name = "TouchDeck session"
        t.start()
        return true
    }

    /// Ends the current session (e.g. before flashing firmware) and waits for it.
    public func disconnect() {
        let done: DispatchSemaphore? = lock.withLock {
            sessionStop = true
            return sessionRunning ? sessionDone : nil
        }
        _ = done?.wait(timeout: .now() + 2)
        publish(.searching)
    }

    public func stop() {
        lock.withLock { stopped = true }
        disconnect()
    }

    private func publish(_ state: LinkState) {
        let changed: Bool = lock.withLock {
            if stateValue == state { return false }
            stateValue = state
            return true
        }
        if changed { onStateChanged?(state) }
    }
}
