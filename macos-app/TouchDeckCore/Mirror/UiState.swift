import Foundation

/// Which page code and screen a board has. Not the same as `BoardKind` (the USB link):
/// both RP boards are CAFE:4011, and only VER tells the 1.69 from the round RP2350.
public enum UiModel: Equatable {
    /// RP2040-Touch-LCD-1.69: 240x280, Watch / Clipboard / Jiggler (libtdui_rp2040).
    case rp2040Rect
    /// ESP32-2424S012C: 240x240 round, Watch / Clipboard / Jiggler / Settings (libtdui_esp32c3).
    case esp32Round
    /// RP2350-Touch-LCD-1.28: 240x240 round, Watch / Clipboard / Jiggler, USB (libtdui_rp2350).
    case rp2350Round

    /// The model for a link and the board name VER reported (nil or "?" before VER existed).
    public static func `for`(_ kind: BoardKind, board: String?) -> UiModel {
        if kind == .esp32c3 { return .esp32Round }
        return board == "rp2350-128" ? .rp2350Round : .rp2040Rect
    }

    public var isRound: Bool { self != .rp2040Rect }
    /// Index of the Clipboard page: every board starts on its watch (round boards since 1.8.0).
    public var clipPage: Int { 1 }
    public var pageCount: Int { self == .esp32Round ? 4 : 3 }   // the ESP32-C3 adds Settings
    public var size: (width: Int, height: Int) { self == .rp2040Rect ? (240, 280) : (240, 240) }
    /// File name stem of the renderer library.
    public var library: String {
        switch self {
        case .rp2040Rect: return "tdui_rp2040"
        case .esp32Round: return "tdui_esp32c3"
        case .rp2350Round: return "tdui_rp2350"
        }
    }
}

/// The firmware's ui_state_t (src/ui_state.h) as raw bytes, field by field: everything a device
/// page needs to draw itself. The C struct is fixed-size fields only (no padding), so offsets
/// are plain sums; a test pins `size` against the renderer's tdui_state_size().
public struct UiState: Equatable {
    public static let size = 1264
    public static let clipView = 1024

    /// Byte offsets of each ui_state_t field.
    public enum Field: Int {
        case screen = 0, sub = 4, timeS = 8, helper = 12, linkOk = 16, muted = 20, timerS = 24
        case btMode = 28, btAvail = 32, btState = 36, btReady = 40, btSecsLeft = 44, btPasskey = 48
        case clipLen = 52, clipState = 56, pastePos = 60
        case jigOn = 64, jigDemo = 68, jigPaused = 72, jigPhase = 76, jigLetter = 80, jigScale = 84
        case jigX = 88, jigY = 92, jigNextS = 96, jigUpS = 100, jigMenus = 104
        case jigMenuOn = 1248, jigKey = 1252, jigOpenS = 1256, jigPauseS = 1260
    }

    /// Fixed char arrays: (offset, size).
    public static let clipSrc = (offset: 108, size: 12)
    public static let msg = (offset: 120, size: 40)
    public static let btHost = (offset: 160, size: 32)
    public static let downReason = (offset: 192, size: 32)
    public static let clip = (offset: 224, size: clipView)

    public private(set) var bytes = [UInt8](repeating: 0, count: size)

    public init() {}

    public subscript(_ f: Field) -> Int32 {
        get { bytes.withUnsafeBytes { $0.load(fromByteOffset: f.rawValue, as: Int32.self) } }
        set { bytes.withUnsafeMutableBytes { $0.storeBytes(of: newValue, toByteOffset: f.rawValue, as: Int32.self) } }
    }

    /// The two float fields (jig_x, jig_y).
    public func float(_ f: Field) -> Float { bytes.withUnsafeBytes { $0.load(fromByteOffset: f.rawValue, as: Float.self) } }
    public mutating func setFloat(_ f: Field, _ v: Float) {
        bytes.withUnsafeMutableBytes { $0.storeBytes(of: v, toByteOffset: f.rawValue, as: Float.self) }
    }

    /// Writes ASCII into a fixed field, NUL-terminated and cut to fit.
    public mutating func put(_ field: (offset: Int, size: Int), _ value: String) {
        put(field, Array(value.utf8.map { $0 < 0x80 ? $0 : UInt8(ascii: "?") }), terminated: true)
    }

    public mutating func put(_ field: (offset: Int, size: Int), _ value: [UInt8], terminated: Bool = false) {
        let n = min(value.count, terminated ? field.size - 1 : field.size)
        for i in 0..<field.size { bytes[field.offset + i] = i < n ? value[i] : 0 }
    }

    public func get(_ field: (offset: Int, size: Int)) -> String {
        let slice = bytes[field.offset..<(field.offset + field.size)]
        let end = slice.firstIndex(of: 0) ?? slice.endIndex
        return String(decoding: slice[..<end], as: UTF8.self)
    }

    public static func == (a: UiState, b: UiState) -> Bool { a.bytes == b.bytes }
}
