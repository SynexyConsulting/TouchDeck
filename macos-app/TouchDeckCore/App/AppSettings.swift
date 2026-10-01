import Foundation
import ServiceManagement

public enum CoreInfo {
    /// The app's marketing version (CFBundleShortVersionString of the main bundle).
    public static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }
}

/// User preferences, stored as JSON in ~/Library/Application Support/TouchDeck/settings.json
/// (the same fields as the Windows app's settings.json, where they apply on a Mac).
public struct AppSettings: Codable, Equatable {
    /// Log PC-mode keys/mouse instead of performing them.
    public var dryRun = false
    /// Poll DBG every 2 s and show the board's diagnostics.
    public var diagnostics = false
    /// Port of the last board used, preferred when several are plugged in.
    public var preferredPort: String?
    /// Check the public update feed at start and daily.
    public var checkForUpdates = true
    /// When the feed was last checked.
    public var lastUpdateCheck: Date?

    public init() {}

    // Missing keys keep their defaults, so an older file still loads.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        dryRun = try c.decodeIfPresent(Bool.self, forKey: .dryRun) ?? false
        diagnostics = try c.decodeIfPresent(Bool.self, forKey: .diagnostics) ?? false
        preferredPort = try c.decodeIfPresent(String.self, forKey: .preferredPort)
        checkForUpdates = try c.decodeIfPresent(Bool.self, forKey: .checkForUpdates) ?? true
        lastUpdateCheck = try c.decodeIfPresent(Date.self, forKey: .lastUpdateCheck)
    }

    public static var defaultURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TouchDeck/settings.json")
    }

    /// A missing or unreadable file gives defaults: settings must never stop the app starting.
    public static func load(_ url: URL = defaultURL) -> AppSettings {
        guard let data = try? Data(contentsOf: url) else { return AppSettings() }
        return (try? JSONDecoder().decode(AppSettings.self, from: data)) ?? AppSettings()
    }

    /// Atomic write (temp file, then rename), so a crash mid-write can't leave half a file.
    public func save(_ url: URL = defaultURL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: url, options: .atomic)
    }
}

/// Recently sent clips, newest first. RAM only, like the board's clip: nothing is written to disk.
public final class ClipHistory {
    public private(set) var items: [String] = []
    private let capacity: Int
    public var changed: (() -> Void)?

    public init(capacity: Int = 10) { self.capacity = capacity }

    public func add(_ text: String) {
        guard !text.isEmpty else { return }
        items.removeAll { $0 == text }            // a repeat moves to the top
        items.insert(text, at: 0)
        if items.count > capacity { items.removeLast(items.count - capacity) }
        changed?()
    }

    public func clear() {
        items.removeAll()
        changed?()
    }
}

/// "Launch at login": the app itself as a login item (SMAppService, macOS 13+). Replaces the
/// Windows Run entry; there is no start-minimized option because a menu bar app starts without a window.
public enum LoginItem {
    public static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    /// The user turned it off in System Settings > General > Login Items, or approval is pending there.
    public static var needsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    public static func set(_ enabled: Bool) throws {
        if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    }
}

/// Scrubs the home folder from text written to errors.log (users may share that file).
public enum LogRedaction {
    public static func redact(_ text: String, home: String = NSHomeDirectory()) -> String {
        let h = home.hasSuffix("/") ? String(home.dropLast()) : home
        guard !h.isEmpty else { return text }
        // Only the whole home folder (/Users/nik, not /Users/nikolai).
        let pattern = NSRegularExpression.escapedPattern(for: h) + "(?=[/]|$|[\\s\"';:,)])"
        return text.replacingOccurrences(of: pattern, with: "~", options: [.regularExpression, .caseInsensitive])
    }
}

/// Unexpected errors, appended to ~/Library/Logs/TouchDeck/errors.log (home folder redacted).
public enum ErrorLog {
    public static var url: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs/TouchDeck/errors.log")
    }

    public static func append(_ text: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(LogRedaction.redact(text))\n"
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let h = try? FileHandle(forWritingTo: url) {
            defer { try? h.close() }
            _ = try? h.seekToEnd()
            try? h.write(contentsOf: Data(line.utf8))
        } else {
            try? Data(line.utf8).write(to: url)
        }
    }
}
