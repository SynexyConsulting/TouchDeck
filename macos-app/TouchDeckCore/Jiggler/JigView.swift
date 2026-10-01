import Foundation

/// What the app shows for a board STATE report (same wording as the device where it overlaps).
public enum JigView {
    private static let scales = ["1.0X", "1.5X", "2.0X"]

    public static func scaleText(_ scale: Int) -> String { scales.indices.contains(scale) ? scales[scale] : "?" }

    public static func status(_ s: StateReport) -> String {
        if !s.jigOn { return "Off" }
        if s.pasting { return "Paused: pasting" }
        switch s.phase {
        case 0: return "Moving"
        case 1: return "Pausing"
        case 2, 3: return "Right-click menu"
        case 4: return s.field("jkey") == 1 ? "F15" : "Esc"
        case 5: return "Resuming"
        default: return ""
        }
    }

    public static func clipText(_ s: StateReport) -> String {
        switch s.clipLength {
        case 0: return "Board clip: empty"
        case 1: return "Board clip: 1 char"
        case let n: return "Board clip: \(n) chars"
        }
    }

    /// The board's Jiggler settings (firmware 1.8.0+), or nil for older firmware.
    public static func config(_ s: StateReport) -> JigConfig? {
        guard s.fields["jmenu"] != nil else { return nil }
        return JigConfig(menuOn: s.field("jmenu") != 0, f15: s.field("jkey") == 1,
                         openS: Int(s.field("jopen")), pauseS: Int(s.field("jpause")))
    }

    /// The trash button: same rule as the board's (text, and no paste typing it).
    public static func canClear(_ s: StateReport) -> Bool { s.clipLength > 0 && !s.pasting }
}

/// The Jiggler settings on the board (its "Jiggler menu" panel): right-click and hold the context
/// menu (or not), Esc or F15, how long the menu stays open and the pause before the next letter.
public struct JigConfig: Equatable {
    public static let maxSeconds = 60
    public var menuOn: Bool
    public var f15: Bool
    public var openS: Int
    public var pauseS: Int

    public init(menuOn: Bool, f15: Bool, openS: Int, pauseS: Int) {
        self.menuOn = menuOn; self.f15 = f15; self.openS = openS; self.pauseS = pauseS
    }

    public func describe() -> String {
        let key = f15 ? "F15" : "Esc"
        return menuOn ? "Jiggler: menu on, \(key), open \(openS) s, pause \(pauseS) s"
                      : "Jiggler: menu off, \(key), pause \(pauseS) s"
    }
}
