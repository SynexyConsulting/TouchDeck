import Foundation
import AppKit
import ApplicationServices

/// One way of reading text: nil means "can't tell", "" means "nothing there".
public protocol TextSource {
    func read() -> String?
}

/// Where COPY text comes from: the focused selection, else the clipboard.
public protocol SelectionProvider {
    /// The text and a short source tag ("select" or "clipbd") shown on the board.
    func grab() -> (text: String, source: String)
}

/// COPY text: the focused control's selection, else the clipboard (as on Windows).
public final class DefaultSelectionProvider: SelectionProvider {
    private let selection: TextSource
    private let clipboard: TextSource

    public init(selection: TextSource = AXSelection(), clipboard: TextSource = Pasteboard()) {
        self.selection = selection
        self.clipboard = clipboard
    }

    public func grab() -> (text: String, source: String) {
        if let sel = selection.read(), !sel.isEmpty { return (sel, "select") }
        return (clipboard.read() ?? "", "clipbd")
    }
}

/// The focused element's selected text, through the Accessibility API. Apps that answer slowly
/// are given 1.5 s (the messaging timeout), then COPY falls back to the clipboard.
public struct AXSelection: TextSource {
    public var timeout: Float = 1.5
    public init() {}

    public func read() -> String? {
        guard AXIsProcessTrusted() else { return nil }
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, timeout)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focusedRef = focused, CFGetTypeID(focusedRef) == AXUIElementGetTypeID() else { return nil }
        let element = focusedRef as! AXUIElement
        AXUIElementSetMessagingTimeout(element, timeout)
        var selected: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextAttribute as CFString, &selected) == .success else { return nil }
        return selected as? String
    }
}

/// Plain text on the general pasteboard.
public struct Pasteboard: TextSource {
    public init() {}
    public func read() -> String? { NSPasteboard.general.string(forType: .string) ?? "" }

    @discardableResult
    public static func write(_ text: String) -> Bool {
        NSPasteboard.general.clearContents()
        return NSPasteboard.general.setString(text, forType: .string)
    }
}
