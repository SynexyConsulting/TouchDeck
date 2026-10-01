import Foundation
import CoreGraphics
import ApplicationServices

/// Posts events with Quartz (CGEvent). The process needs Accessibility permission
/// (System Settings > Privacy & Security > Accessibility), or macOS drops them silently.
public final class CGEventSink: InputSink {
    private let source = CGEventSource(stateID: .hidSystemState)
    private var heldModifiers = Set<UInt16>()
    private var heldButtons = Set<MouseAction>()

    private static let modifierFlags: [UInt16: CGEventFlags] = [
        0x3B: .maskControl, 0x3E: .maskControl,
        0x38: .maskShift, 0x3C: .maskShift,
        0x3A: .maskAlternate, 0x3D: .maskAlternate,
        0x37: .maskCommand, 0x36: .maskCommand,
    ]

    public init() {}

    private var flags: CGEventFlags {
        heldModifiers.reduce(into: CGEventFlags()) { f, code in
            if let m = Self.modifierFlags[code] { f.insert(m) }
        }
    }

    public func send(_ events: [InputEvent]) {
        for e in events {
            switch e {
            case .key(let code, let up): postKey(code, up: up)
            case .move(let dx, let dy): postMove(dx: dx, dy: dy)
            case .button(let a): postButton(a)
            }
        }
    }

    private func postKey(_ code: UInt16, up: Bool) {
        guard let ev = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: !up) else { return }
        if Self.modifierFlags[code] != nil {
            if up { heldModifiers.remove(code) } else { heldModifiers.insert(code) }
            ev.type = .flagsChanged
        }
        ev.flags = flags          // typed characters see the modifiers the board holds, not the real keyboard's
        ev.post(tap: .cghidEventTap)
    }

    private func postMove(dx: Int, dy: Int) {
        let here = CGEvent(source: nil)?.location ?? .zero
        let to = Self.clampToDisplays(CGPoint(x: here.x + CGFloat(dx), y: here.y + CGFloat(dy)))
        let type: CGEventType = heldButtons.contains(.leftDown) ? .leftMouseDragged
            : heldButtons.contains(.rightDown) ? .rightMouseDragged : .mouseMoved
        guard let ev = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: to, mouseButton: .left) else { return }
        ev.setIntegerValueField(.mouseEventDeltaX, value: Int64(dx))
        ev.setIntegerValueField(.mouseEventDeltaY, value: Int64(dy))
        ev.post(tap: .cghidEventTap)
    }

    private func postButton(_ a: MouseAction) {
        let at = CGEvent(source: nil)?.location ?? .zero
        let (type, button): (CGEventType, CGMouseButton)
        switch a {
        case .leftDown: (type, button) = (.leftMouseDown, .left); heldButtons.insert(.leftDown)
        case .leftUp: (type, button) = (.leftMouseUp, .left); heldButtons.remove(.leftDown)
        case .rightDown: (type, button) = (.rightMouseDown, .right); heldButtons.insert(.rightDown)
        case .rightUp: (type, button) = (.rightMouseUp, .right); heldButtons.remove(.rightDown)
        case .middleDown: (type, button) = (.otherMouseDown, .center); heldButtons.insert(.middleDown)
        case .middleUp: (type, button) = (.otherMouseUp, .center); heldButtons.remove(.middleDown)
        }
        CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: at, mouseButton: button)?
            .post(tap: .cghidEventTap)
    }

    /// Keeps the pointer on some display (global coordinates, origin top-left of the main display).
    static func clampToDisplays(_ p: CGPoint) -> CGPoint {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        CGGetActiveDisplayList(count, &ids, &count)
        let rects = ids.map { CGDisplayBounds($0) }
        if rects.contains(where: { $0.contains(p) }) || rects.isEmpty { return p }
        // Off every screen: clamp into the nearest one.
        let best = rects.min { distance(p, $0) < distance(p, $1) }!
        return CGPoint(x: min(max(p.x, best.minX), best.maxX - 1), y: min(max(p.y, best.minY), best.maxY - 1))
    }

    private static func distance(_ p: CGPoint, _ r: CGRect) -> CGFloat {
        let dx = max(r.minX - p.x, 0, p.x - r.maxX), dy = max(r.minY - p.y, 0, p.y - r.maxY)
        return dx * dx + dy * dy
    }
}

/// Caps Lock from the HID system state (what the keyboard LED shows).
public struct MacKeyboardState: KeyboardState {
    public init() {}
    public var capsLock: Bool { CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift) }
}

/// Accessibility permission: needed to post input events and to read other apps' selected text.
public enum AccessibilityPermission {
    public static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Shows the system prompt (once per app identity) pointing at Privacy & Security > Accessibility.
    @discardableResult
    public static func request() -> Bool {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        return AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }
}
