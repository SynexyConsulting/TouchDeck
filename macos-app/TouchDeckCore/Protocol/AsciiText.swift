import Foundation

/// The boards type US-layout ASCII only, so text is transliterated before it is sent
/// (same table and rules as the Windows app's AsciiText.cs).
public enum AsciiText {
    private static let subs: [UInt32: String] = [
        0x2018: "'", 0x2019: "'", 0x201A: "'", 0x201C: "\"", 0x201D: "\"", 0x201E: "\"",
        0x2013: "-", 0x2014: "-", 0x2212: "-", 0x2026: "...", 0x00A0: " ", 0x2022: "*",
        0x00D7: "x", 0x2192: "->", 0x2190: "<-", 0x00AB: "<<", 0x00BB: ">>",
    ]

    /// ASCII text, plus how many characters had no equivalent and became '?'.
    public static func transliterate(_ input: String) -> (text: String, lost: Int) {
        var out = ""
        out.reserveCapacity(input.utf8.count)
        var lost = 0
        for scalar in input.unicodeScalars {
            if scalar.value < 0x80 { out.unicodeScalars.append(scalar); continue }
            if let sub = subs[scalar.value] { out += sub; continue }
            // Accented letters fold to their base letter (é -> e); anything else is lost.
            let folded = String(scalar).decomposedStringWithCompatibilityMapping
            let ascii = folded.unicodeScalars.filter { $0.value < 0x80 }
            if ascii.isEmpty {
                out += "?"
                lost += 1
            } else {
                out.unicodeScalars.append(contentsOf: ascii)
            }
        }
        return (out, lost)
    }
}

/// Mac -> board: new clip text, "CLIP <n> <src>\n" followed by n raw bytes.
public enum ClipMessage {
    /// The boards hold at most this many bytes of clip text.
    public static let maxBytes = 8192

    /// Frames already-ASCII text; anything past `maxBytes` is cut.
    public static func encode(_ asciiText: String, source: String) -> [UInt8] {
        var data = Array(asciiText.utf8)
        if data.count > maxBytes { data = Array(data[0..<maxBytes]) }
        return Array("CLIP \(data.count) \(source)\n".utf8) + data
    }
}
