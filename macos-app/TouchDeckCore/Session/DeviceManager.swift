import Foundation

public enum LinkStatus: Equatable { case searching, portBusy, notResponding, connected }

/// What the app shows about one board's link.
public struct LinkState: Equatable {
    public var status: LinkStatus
    public var device: DeviceCandidate?
    public var firmware: FirmwareInfo?
    public init(_ status: LinkStatus, device: DeviceCandidate? = nil, firmware: FirmwareInfo? = nil) {
        self.status = status; self.device = device; self.firmware = firmware
    }
    public static let searching = LinkState(.searching)
}

/// One port the manager knows: its link state and, while connected, its session.
public struct BoardLink {
    public var port: String
    public var state: LinkState
    public var session: DeviceSession?
}

/// Keeps one `DeviceSession` running on every Touch Deck that is plugged in: one slot per port.
/// A board that goes away ends its own session (releasing any held input); the others carry on.
/// A port that is busy or doesn't answer is a slot too, so the app can say why.
/// Port of DeviceManager.cs.
public final class DeviceManager: @unchecked Sendable {
    private final class Slot {
        var device: DeviceCandidate
        var state = LinkState.searching
        var session: DeviceSession?
        var running = false
        var stop = false
        var done: DispatchSemaphore?
        init(_ device: DeviceCandidate) { self.device = device }
    }

    private let scan: () -> [DeviceCandidate]
    private let openTransport: (DeviceCandidate) -> SerialTransport
    private let makeSession: (SerialTransport) -> DeviceSession

    private let lock = NSLock()
    private var stopped = true
    private var slots: [String: Slot] = [:]
    private var order: [String] = []                      // ports in the order they were first seen

    /// A port's link changed (raised on a background thread). A new port is announced this way too.
    public var onSlotChanged: ((String, LinkState) -> Void)?
    /// A port went away and has no session left.
    public var onSlotRemoved: ((String) -> Void)?
    /// A new session is up on a port: hook its events here (before it starts reading).
    public var onSessionStarted: ((String, DeviceSession) -> Void)?

    public init(scan: @escaping () -> [DeviceCandidate],
                openTransport: @escaping (DeviceCandidate) -> SerialTransport,
                makeSession: @escaping (SerialTransport) -> DeviceSession) {
        self.scan = scan
        self.openTransport = openTransport
        self.makeSession = makeSession
    }

    /// Every known port, in the order they were first seen.
    public var links: [BoardLink] {
        lock.withLock { order.compactMap { p in slots[p].map { BoardLink(port: p, state: $0.state, session: $0.session) } } }
    }

    public var sessions: [DeviceSession] { lock.withLock { order.compactMap { slots[$0]?.session } } }

    public func session(for port: String) -> DeviceSession? { lock.withLock { slots[port]?.session } }

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

    /// One scan: connect every port without a session, drop the ports that are gone.
    public func tick() {
        let found = scan()
        for device in found {
            let slot: Slot? = lock.withLock {
                let s: Slot
                if let existing = slots[device.port] { s = existing } else {
                    s = Slot(device)
                    slots[device.port] = s
                    order.append(device.port)
                }
                if s.running { return nil }
                s.device = device
                return s
            }
            if let slot { tryConnect(slot) }
        }

        let present = Set(found.map(\.port))
        let gone: [String] = lock.withLock {
            let g = order.filter { !present.contains($0) && slots[$0]?.running == false }
            for p in g { slots[p] = nil }
            order.removeAll { g.contains($0) }
            return g
        }
        for p in gone { onSlotRemoved?(p) }
    }

    private func tryConnect(_ slot: Slot) {
        let device = slot.device
        let transport = openTransport(device)
        do {
            try transport.open()
        } catch {
            transport.close()                 // another program holds it
            publish(slot, LinkState(.portBusy, device: device))
            return
        }
        let session = makeSession(transport)
        session.kind = device.kind
        guard (try? session.handshake()) == true else {
            transport.close()
            publish(slot, LinkState(.notResponding, device: device))
            return
        }
        let done = DispatchSemaphore(value: 0)
        lock.withLock {
            slot.session = session
            slot.running = true
            slot.stop = false
            slot.done = done
        }
        onSessionStarted?(device.port, session)
        publish(slot, LinkState(.connected, device: device, firmware: session.firmware))
        let t = Thread { [weak self] in
            do {
                try session.run { self?.lock.withLock { slot.stop } ?? true }
            } catch {
                // unplugged: run's defer released held input
            }
            transport.close()
            guard let self else { done.signal(); return }
            let cancelled = self.lock.withLock { () -> Bool in
                slot.session = nil
                slot.running = false
                return slot.stop
            }
            if !cancelled { self.publish(slot, LinkState(.searching, device: device)) }
            done.signal()
        }
        t.name = "TouchDeck session \(device.port)"
        t.start()
    }

    private func publish(_ slot: Slot, _ state: LinkState) {
        let changed: Bool = lock.withLock {
            if slot.state == state { return false }
            slot.state = state
            return true
        }
        if changed { onSlotChanged?(slot.device.port, state) }
    }

    /// Ends every session and waits for them (each releases its held input).
    public func disconnectAll() {
        let waits: [DispatchSemaphore] = lock.withLock {
            for s in slots.values { s.stop = true }
            return slots.values.compactMap { $0.running ? $0.done : nil }
        }
        for d in waits { _ = d.wait(timeout: .now() + 2) }
    }

    public func stop() {
        lock.withLock { stopped = true }
        disconnectAll()
    }
}
