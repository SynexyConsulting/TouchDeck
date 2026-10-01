import Foundation

/// A line sent by a Touch Deck board (protocol: the top of src/usb_io.c).
/// Port of windows-app/src/TouchDeck.Core/Protocol/BoardLine.cs.
public enum BoardMessage: Equatable {
    /// The user tapped COPY: send the Mac's selected text.
    case copyRequest
    /// Answer to HELLO / PING.
    case pong
    /// Debug or diagnostics text (DBG replies arrive as LOG lines).
    case log(String)
    /// PC output mode keyboard report: HID modifier byte and usage (0 = release all).
    case key(mods: Int, usage: Int)
    /// PC output mode mouse report: HID button byte and relative motion (clamped to a HID byte).
    case mouse(buttons: Int, dx: Int, dy: Int)
    /// Answer to VER.
    case version(board: String, version: String, build: String)
    /// Live board state after WATCH 1 (firmware 1.6.0+).
    case state(StateReport)
    /// TEXT key value (firmware 1.7.0+): a string field of the device mirror (msg, src, host, down).
    case text(key: String, value: String)
    /// CLIPTEXT (firmware 1.7.0+): the first 1024 bytes of the board's clip, unescaped.
    case clipText([UInt8])
    /// Anything else, including malformed K/M lines (ignored, never half-applied).
    case unknown(String)
}

/// A STATE line. X/Y: the jiggler dot in 0..1000 letter-box units. `fields` holds every numeric
/// key=value of the line, including the 1.7.0 device-mirror fields; `page` is present from 1.7.0 on.
public struct StateReport: Equatable {
    public var jigOn: Bool
    public var letter: Character
    public var scale: Int
    public var phase: Int
    public var x: Int
    public var y: Int
    public var clipLength: Int
    public var pasting: Bool
    public var fields: [String: Int64]

    public init(jigOn: Bool, letter: Character, scale: Int, phase: Int, x: Int, y: Int,
                clipLength: Int, pasting: Bool, fields: [String: Int64] = [:]) {
        self.jigOn = jigOn; self.letter = letter; self.scale = scale; self.phase = phase
        self.x = x; self.y = y; self.clipLength = clipLength; self.pasting = pasting; self.fields = fields
    }

    /// Firmware 1.7.0+: the line carries the whole UI state (the device mirror).
    public var isFullMirror: Bool { fields["page"] != nil }

    public func field(_ key: String, _ fallback: Int64 = 0) -> Int64 { fields[key] ?? fallback }
}

public enum BoardLine {
    public static func parse(_ line: String) -> BoardMessage {
        let parts = line.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        guard let head = parts.first else { return .unknown(line) }
        switch head {
        case "COPY" where parts.count == 1:
            return .copyRequest
        case "PONG" where parts.count == 1:
            return .pong
        case "LOG":
            return .log(line.count > 4 ? String(line.dropFirst(4)) : "")
        case "K" where parts.count == 3:
            if let mods = hexByte(parts[1]), let usage = hexByte(parts[2]) { return .key(mods: mods, usage: usage) }
        case "M" where parts.count == 4:
            if let buttons = hexByte(parts[1]), let dx = Int(parts[2]), let dy = Int(parts[3]) {
                return .mouse(buttons: buttons, dx: min(max(dx, -127), 127), dy: min(max(dy, -127), 127))
            }
        case "STATE":
            if let st = parseState(parts) { return .state(st) }
        case "TEXT" where parts.count >= 2:
            let prefix = "TEXT " + parts[1] + " "
            return .text(key: parts[1], value: line.hasPrefix(prefix) ? String(line.dropFirst(prefix.count)) : "")
        case "CLIPTEXT":
            return .clipText(ClipEscape.unescape(line.count > 9 ? String(line.dropFirst(9)) : ""))
        case "VERSION" where parts.count >= 3:
            return .version(board: parts[1], version: parts[2], build: parts.dropFirst(3).joined(separator: " "))
        default:
            break
        }
        return .unknown(line)
    }

    private static func parseState(_ parts: [String]) -> StateReport? {
        var f: [String: String] = [:]
        for p in parts.dropFirst() {
            if let eq = p.firstIndex(of: "="), eq != p.startIndex {
                f[String(p[..<eq])] = String(p[p.index(after: eq)...])
            }
        }
        func num(_ k: String) -> Int? { f[k].flatMap { Int($0) } }
        guard let letter = f["letter"], letter.count == 1,
              let jig = num("jig"), let scale = num("scale"), let phase = num("phase"),
              let x = num("x"), let y = num("y"), let clip = num("clip"), let paste = num("paste") else { return nil }
        var numbers: [String: Int64] = [:]
        for (k, v) in f { if let n = Int64(v) { numbers[k] = n } }
        return StateReport(jigOn: jig == 1, letter: letter.first!, scale: scale, phase: phase, x: x, y: y,
                           clipLength: clip, pasting: paste == 1, fields: numbers)
    }

    /// One report byte: hex, 0..FF. Signs and anything larger make the line malformed.
    private static func hexByte(_ s: String) -> Int? {
        guard !s.isEmpty, s.allSatisfy({ $0.isHexDigit }), let v = Int(s, radix: 16), v <= 0xFF else { return nil }
        return v
    }
}

/// CLIPTEXT escapes (ui_sync.c): backslash-backslash, \n, \r, \t and \xHH.
public enum ClipEscape {
    public static func unescape(_ s: String) -> [UInt8] {
        let c = Array(s.utf8)
        var out: [UInt8] = []
        out.reserveCapacity(c.count)
        var i = 0
        while i < c.count {
            if c[i] == UInt8(ascii: "\\") && i + 1 < c.count {
                i += 1
                let n = c[i]
                if n == UInt8(ascii: "x") && i + 2 < c.count,
                   let b = UInt8(String(decoding: c[(i + 1)...(i + 2)], as: UTF8.self), radix: 16) {
                    out.append(b)
                    i += 2
                } else {
                    switch n {
                    case UInt8(ascii: "n"): out.append(10)
                    case UInt8(ascii: "r"): out.append(13)
                    case UInt8(ascii: "t"): out.append(9)
                    default: out.append(n)
                    }
                }
            } else {
                out.append(c[i])
            }
            i += 1
        }
        return out
    }
}

/// key=value fields of a DBG diagnostics line (the '|' separators are ignored).
public enum DbgFields {
    public static func parse(_ text: String) -> [String: String] {
        var fields: [String: String] = [:]
        for token in text.split(separator: " ") {
            if let eq = token.firstIndex(of: "="), eq != token.startIndex {
                fields[String(token[..<eq])] = String(token[token.index(after: eq)...])
            }
        }
        return fields
    }
}
