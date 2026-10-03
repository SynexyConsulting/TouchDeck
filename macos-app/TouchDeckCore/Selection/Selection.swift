import Foundation
import AppKit
import ApplicationServices
import Carbon.HIToolbox

/// One way of reading text: nil means "can't tell", "" means "nothing there".
public protocol TextSource {
    func read() -> String?
}

/// Where COPY text comes from: the focused selection, else the clipboard.
public protocol SelectionProvider {
    /// The text and a short source tag ("select" or "clipbd") shown on the board.
    func grab() -> (text: String, source: String)
}

/// COPY text, as on Windows: the selection in the app being used, else the clipboard.
/// macOS has no UI Automation equivalent that every app answers, so when Accessibility can't
/// tell (nil), the app is asked to copy (⌘C) and the clipboard is put back afterwards.
public final class DefaultSelectionProvider: SelectionProvider {
    private let selection: TextSource
    private let copyFallback: TextSource?
    private let clipboard: TextSource

    public init(selection: TextSource = AXSelection(), copyFallback: TextSource? = CopyCommandSelection(),
                clipboard: TextSource = Pasteboard()) {
        self.selection = selection
        self.copyFallback = copyFallback
        self.clipboard = clipboard
    }

    public func grab() -> (text: String, source: String) {
        switch selection.read() {
        case let sel? where !sel.isEmpty: return (sel, "select")
        case nil:
            if let sel = copyFallback?.read(), !sel.isEmpty { return (sel, "select") }
        default: break                                 // Accessibility says nothing is selected
        }
        return (clipboard.read() ?? "", "clipbd")
    }
}

/// The app the user is working in. Touch Deck's own windows don't count: clicking COPY on the
/// device mirror activates Touch Deck, and the selection wanted is in the app used before it.
public final class TargetApp {
    public static let shared = TargetApp()
    private let lock = NSLock()
    private var last: NSRunningApplication?
    private var observer: NSObjectProtocol?

    private init() {
        last = Self.other(NSWorkspace.shared.frontmostApplication)
        observer = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: nil) { [weak self] note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            guard let app = Self.other(app) else { return }
            self?.lock.withLock { self?.last = app }
        }
    }

    private static func other(_ app: NSRunningApplication?) -> NSRunningApplication? {
        guard let app, app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
        return app
    }

    /// The frontmost app, or the one used last when Touch Deck is in front.
    public var current: NSRunningApplication? {
        if let front = Self.other(NSWorkspace.shared.frontmostApplication) { return front }
        return lock.withLock { last.flatMap { $0.isTerminated ? nil : $0 } }
    }

    /// True when the target is the frontmost app, so a keystroke reaches it.
    public var isFrontmost: Bool { Self.other(NSWorkspace.shared.frontmostApplication) != nil }
}

/// The target app's selected text, through the Accessibility API. Apps that answer slowly are
/// given 1.5 s each call (the messaging timeout), as Windows time-boxes UI Automation.
public struct AXSelection: TextSource {
    public var timeout: Float = 1.5
    public init() {}

    public func read() -> String? {
        guard AXIsProcessTrusted(), let app = TargetApp.shared.current else { return nil }
        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(appElement, timeout)
        // Chromium and Electron apps build their accessibility tree only when asked.
        AXUIElementSetAttributeValue(appElement, "AXManualAccessibility" as CFString, kCFBooleanTrue)

        guard var element: AXUIElement = copy(appElement, kAXFocusedUIElementAttribute) else { return nil }
        for _ in 0..<4 {                                   // focus is sometimes on a child
            AXUIElementSetMessagingTimeout(element, timeout)
            if let text = selectedText(element) { return text }
            guard let parent: AXUIElement = copy(element, kAXParentAttribute) else { break }
            element = parent
        }
        return nil
    }

    /// nil when the element doesn't support text selection at all.
    private func selectedText(_ e: AXUIElement) -> String? {
        if let s: String = copy(e, kAXSelectedTextAttribute) { return s }
        // Some text views only give the range; read the string for it.
        if let rangeValue: AXValue = copy(e, kAXSelectedTextRangeAttribute) {
            var range = CFRange()
            guard AXValueGetValue(rangeValue, .cfRange, &range) else { return nil }
            if range.length == 0 { return "" }
            var out: CFTypeRef?
            if AXUIElementCopyParameterizedAttributeValue(e, kAXStringForRangeParameterizedAttribute as CFString,
                                                          rangeValue, &out) == .success, let s = out as? String {
                return s
            }
        }
        // WebKit web areas: text marker ranges.
        if let marker: CFTypeRef = copy(e, "AXSelectedTextMarkerRange") {
            var out: CFTypeRef?
            if AXUIElementCopyParameterizedAttributeValue(e, "AXStringForTextMarkerRange" as CFString,
                                                          marker, &out) == .success, let s = out as? String {
                return s
            }
        }
        return nil
    }

    private func copy<T>(_ e: AXUIElement, _ attribute: String) -> T? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(e, attribute as CFString, &value) == .success, let value else { return nil }
        if T.self == AXUIElement.self {
            guard CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        } else if T.self == AXValue.self {
            guard CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        }
        return value as? T
    }
}

/// Asks the frontmost app to copy (⌘C), takes the text it puts on the clipboard, then restores
/// what the clipboard held. nil when the app copied nothing (no selection) or isn't in front.
public struct CopyCommandSelection: TextSource {
    public var wait: TimeInterval = 0.5
    /// After `wait`, a copy that still lands within this long is undone (a slow app must not leave
    /// the selection on the user's clipboard).
    public var lateCopyWatch: TimeInterval = 2
    public init() {}

    /// The board's COPY (session thread) and ⌃⌥C (a task) can overlap; one ⌘C at a time, or the
    /// second snapshot would capture the first one's text and restore that.
    private static let lock = NSLock()

    public func read() -> String? {
        guard AXIsProcessTrusted(), TargetApp.shared.isFrontmost else { return nil }
        Self.lock.lock()
        let pb = NSPasteboard.general
        let saved = Pasteboard.snapshot()
        let before = pb.changeCount

        let source = CGEventSource(stateID: .privateState)
        for down in [true, false] {
            guard let ev = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_C), keyDown: down) else {
                Self.lock.unlock()
                return nil
            }
            ev.flags = .maskCommand                        // only ⌘, even while the hotkey's ⌃⌥ are still held
            ev.post(tap: .cghidEventTap)
        }

        let deadline = Date().addingTimeInterval(wait)
        while pb.changeCount == before && Date() < deadline { Thread.sleep(forTimeInterval: 0.01) }
        guard pb.changeCount != before else {
            // Nothing yet: keep the lock while watching, and put the clipboard back if the copy lands late.
            let watchUntil = Date().addingTimeInterval(lateCopyWatch)
            DispatchQueue.global(qos: .utility).async {
                defer { Self.lock.unlock() }
                while Date() < watchUntil {
                    if pb.changeCount != before {
                        Pasteboard.restore(saved)
                        return
                    }
                    Thread.sleep(forTimeInterval: 0.02)
                }
            }
            return nil
        }
        let text = pb.string(forType: .string)
        Pasteboard.restore(saved)
        Self.lock.unlock()
        return text
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

    /// Every item with every type's data, so `restore` puts back images and files too.
    static func snapshot() -> [[NSPasteboard.PasteboardType: Data]] {
        (NSPasteboard.general.pasteboardItems ?? []).map { item in
            var types: [NSPasteboard.PasteboardType: Data] = [:]
            for t in item.types { if let d = item.data(forType: t) { types[t] = d } }
            return types
        }
    }

    static func restore(_ items: [[NSPasteboard.PasteboardType: Data]]) {
        let pb = NSPasteboard.general
        pb.clearContents()
        guard !items.isEmpty else { return }
        pb.writeObjects(items.map { types in
            let item = NSPasteboardItem()
            for (t, d) in types { item.setData(d, forType: t) }
            return item
        })
    }
}
