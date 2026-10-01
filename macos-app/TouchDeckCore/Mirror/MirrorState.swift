import Foundation

/// The board's UI state as the device mirror knows it, built from the board's STATE, TEXT and
/// CLIPTEXT lines (firmware 1.7.0+; the protocol is at the top of src/usb_io.c).
public final class MirrorState {
    // STATE key -> field. The Windows MirrorState.cs and tools/tests/tdui_host.py have the same table.
    private static let numbers: [(key: String, field: UiState.Field)] = [
        ("page", .screen), ("sub", .sub), ("t", .timeS), ("pc", .helper), ("link", .linkOk), ("mute", .muted),
        ("timer", .timerS), ("mode", .btMode), ("bta", .btAvail), ("bts", .btState), ("btr", .btReady),
        ("left", .btSecsLeft), ("pk", .btPasskey), ("clip", .clipLen), ("cst", .clipState), ("ppos", .pastePos),
        ("jig", .jigOn), ("demo", .jigDemo), ("paused", .jigPaused), ("phase", .jigPhase), ("scale", .jigScale),
        ("next", .jigNextS), ("up", .jigUpS), ("menus", .jigMenus),
        ("jmenu", .jigMenuOn), ("jkey", .jigKey), ("jopen", .jigOpenS), ("jpause", .jigPauseS),
    ]

    public let model: UiModel
    public private(set) var state = UiState()
    /// A STATE with the full mirror fields has arrived (firmware 1.7.0+).
    public private(set) var complete = false
    /// STATE letter= -> renderer letter index; the renderer provides it (NativeUi.letterIndex).
    private let letterIndex: (Character) -> Int

    public init(model: UiModel, letterIndex: ((Character) -> Int)? = nil) {
        self.model = model
        self.letterIndex = letterIndex ?? { NativeUi.letterIndex(model, $0) }
        state.put(UiState.clipSrc, "-")
    }

    /// Applies one board line; true when it changed what the mirror shows.
    @discardableResult
    public func apply(_ message: BoardMessage) -> Bool {
        switch message {
        case .state(let st):
            for (key, field) in Self.numbers {
                if let v = st.fields[key] { state[field] = Int32(truncatingIfNeeded: v) }   // uint32 fields keep their bits
            }
            if let x = st.fields["x"] { state.setFloat(.jigX, Float(x)) }
            if let y = st.fields["y"] { state.setFloat(.jigY, Float(y)) }
            let li = letterIndex(st.letter)
            state[.jigLetter] = Int32(li < 0 ? 0 : li)
            complete = complete || st.isFullMirror
            return true
        case .text(let key, let value):
            switch key {
            case "msg": state.put(UiState.msg, value)
            case "src": state.put(UiState.clipSrc, value)
            case "host": state.put(UiState.btHost, value)
            case "down": state.put(UiState.downReason, value)
            default: return false
            }
            return true
        case .clipText(let bytes):
            state.put(UiState.clip, bytes)
            return true
        default:
            return false
        }
    }

    public var message: String { state.get(UiState.msg) }
}

/// What a pointer gesture on the device mirror means for the board.
public enum MirrorGesture: Equatable {
    /// TAP x y, in device pixels: the firmware's own hit testing decides what it hits.
    case tap(x: Int, y: Int)
    /// SWIPE L (finger moves left: next page) or SWIPE R.
    case swipe(left: Bool)
}

public enum MirrorInput {
    /// Press and release within this many device px: a tap.
    public static let tapSlop = 12.0
    /// Horizontal travel for a swipe (device px), and it must be mostly horizontal.
    public static let swipeMin = 36.0

    /// Maps a press at (x0, y0) and release at (x1, y1), in device pixels, to a gesture, or nil
    /// when it is neither (a short or vertical drag, or a tap off the panel).
    public static func classify(x0: Double, y0: Double, x1: Double, y1: Double, width: Int, height: Int) -> MirrorGesture? {
        let dx = x1 - x0, dy = y1 - y0
        if abs(dx) >= swipeMin && abs(dx) > 1.5 * abs(dy) { return .swipe(left: dx < 0) }
        if (dx * dx + dy * dy).squareRoot() <= tapSlop {
            if x0 < 0 || y0 < 0 || x0 >= Double(width) || y0 >= Double(height) { return nil }
            return .tap(x: Int(x0.rounded(.down)), y: Int(y0.rounded(.down)))
        }
        return nil
    }
}
