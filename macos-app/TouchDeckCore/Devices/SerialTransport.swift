import Foundation
import Darwin

public enum SerialError: Error, Equatable {
    /// Another program holds the port (open() said EBUSY, or TIOCEXCL refused us).
    case busy
    case notFound
    case io(Int32)
    case closed
}

/// Byte stream to a board, read as protocol lines.
public protocol SerialTransport: AnyObject {
    func open() throws
    func writeLine(_ line: String) throws
    func write(_ data: [UInt8]) throws
    /// Next complete line, or nil when none arrived within the timeout.
    func readLine(timeout: TimeInterval) throws -> String?
    func close()
}

extension SerialTransport {
    public func writeLine(_ line: String) throws { try write(Array((line + "\n").utf8)) }
}

/// Accumulates bytes and hands back complete lines (CR dropped, LF terminated).
public final class LineSplitter {
    private var partial: [UInt8] = []
    public init() {}

    public func feed<S: Sequence>(_ bytes: S) -> [String] where S.Element == UInt8 {
        var lines: [String] = []
        for b in bytes {
            if b == 13 { continue }
            if b == 10 {
                lines.append(String(decoding: partial, as: UTF8.self))
                partial.removeAll(keepingCapacity: true)
            } else {
                partial.append(b)
            }
        }
        return lines
    }
}

// ioctl request codes from <sys/ttycom.h>: function-like macros, which Swift doesn't import.
private let TIOCEXCL: UInt = 0x2000_740D      // _IO('t', 13)
private let TIOCMGET: UInt = 0x4004_746A      // _IOR('t', 106, int)
private let TIOCMSET: UInt = 0x8004_746D      // _IOW('t', 109, int)
private let modemDTR: Int32 = 0x002           // TIOCM_DTR
private let modemRTS: Int32 = 0x004           // TIOCM_RTS

/// A POSIX serial port (termios) on a /dev/cu.* device, raw 8N1.
public final class PosixSerialTransport: SerialTransport {
    public let device: DeviceCandidate
    private let baud: speed_t
    private let dtr: Bool
    private var fd: Int32 = -1
    private let splitter = LineSplitter()
    private var ready: [String] = []
    private var buffer = [UInt8](repeating: 0, count: 512)

    public init(device: DeviceCandidate, baud: speed_t = 115_200) {
        self.device = device
        self.baud = baud
        self.dtr = BoardKinds.dtrHigh(device.kind)
    }

    public func open() throws {
        fd = try Self.openRaw(device.port, baud: baud)
        // Set the modem lines in one TIOCMSET so the ESP32-C3 never sees a reset edge
        // (open() may have raised both; clearing them one at a time can pass through RTS-only).
        var bits: Int32 = 0
        _ = ioctl(fd, TIOCMGET, &bits)
        bits &= ~(modemDTR | modemRTS)
        if dtr { bits |= modemDTR }
        _ = ioctl(fd, TIOCMSET, &bits)
    }

    /// Opens a call-out device raw and exclusive. Throws `.busy` when another program has it.
    static func openRaw(_ path: String, baud: speed_t) throws -> Int32 {
        let fd = Darwin.open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        if fd < 0 {
            switch errno {
            case EBUSY, EACCES: throw SerialError.busy
            case ENOENT, ENXIO: throw SerialError.notFound
            default: throw SerialError.io(errno)
            }
        }
        if ioctl(fd, TIOCEXCL) != 0 {
            Darwin.close(fd)
            throw SerialError.busy
        }
        var t = termios()
        tcgetattr(fd, &t)
        cfmakeraw(&t)
        t.c_cflag |= tcflag_t(CLOCAL | CREAD)
        t.c_cflag &= ~tcflag_t(HUPCL)           // closing must not toggle DTR (ESP32-C3 reset line)
        cfsetspeed(&t, baud)
        if tcsetattr(fd, TCSANOW, &t) != 0 {
            let e = errno
            Darwin.close(fd)
            throw SerialError.io(e)
        }
        return fd
    }

    public func write(_ data: [UInt8]) throws {
        guard fd >= 0 else { throw SerialError.closed }
        var sent = 0
        let deadline = Date().addingTimeInterval(1)
        while sent < data.count {
            let n = data[sent...].withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
            if n > 0 { sent += n; continue }
            if n < 0 && errno != EAGAIN { throw SerialError.io(errno) }
            if Date() > deadline { throw SerialError.io(ETIMEDOUT) }
            usleep(1000)
        }
    }

    public func readLine(timeout: TimeInterval) throws -> String? {
        guard fd >= 0 else { throw SerialError.closed }
        let deadline = Date().addingTimeInterval(timeout)
        while ready.isEmpty {
            let left = max(0, deadline.timeIntervalSinceNow)
            var pfd = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let r = poll(&pfd, 1, Int32(left * 1000))
            if r < 0 && errno != EINTR { throw SerialError.io(errno) }
            if r > 0 {
                if pfd.revents & Int16(POLLHUP | POLLERR | POLLNVAL) != 0 { throw SerialError.closed }   // unplugged
                let n = buffer.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
                if n < 0 && errno != EAGAIN { throw SerialError.io(errno) }
                if n == 0 { throw SerialError.closed }
                if n > 0 { ready.append(contentsOf: splitter.feed(buffer[0..<n])) }
            }
            if ready.isEmpty && Date() >= deadline { return nil }
        }
        return ready.removeFirst()
    }

    public func close() {
        if fd >= 0 { Darwin.close(fd) }
        fd = -1
    }

    deinit { close() }

    /// Opening a stock Pico SDK program's USB serial port at 1200 baud reboots it into its
    /// bootloader (the SDK's reset signal). False if the port couldn't be opened.
    public static func touch1200(_ path: String) -> Bool {
        guard let fd = try? openRaw(path, baud: 1200) else { return false }
        var bits: Int32 = modemDTR
        _ = ioctl(fd, TIOCMSET, &bits)
        usleep(100_000)
        Darwin.close(fd)
        return true
    }
}
