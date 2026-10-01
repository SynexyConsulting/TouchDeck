import Foundation
import CryptoKit

/// A feed or download failed validation or couldn't be read (shown as text, never trusted).
public struct UpdateError: Error, Equatable, CustomStringConvertible {
    public var message: String
    public init(_ message: String) { self.message = message }
    public var description: String { message }
}

public struct UpdatePackage: Equatable {
    public var version: SemVer, url: URL, sha256: String, size: Int64
}

public struct FirmwarePackage: Equatable {
    public var board: String, version: SemVer, url: URL, sha256: String, size: Int64
}

/// Where updates come from, and which URLs that source may point at. Same rules as the Windows app.
public struct UpdateSource {
    public static let officialAssetPrefix = "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/"
    public static let officialFeed = URL(string: "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/latest/download/updates.json")!
    /// The feed-signing public key (ECDSA P-256, SubjectPublicKeyInfo). Only a feed signed with its
    /// private key is trusted; that key never lives in this repo.
    public static let officialPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEme/lCuDrapxNW0d+7Lk9/qQTKbXAPhv1RjJLIDbe8Go9JWZFXQOyQ41g/o2OlU5LJHjA01RYYgCOG7+Irdtwhw=="

    public static let official = UpdateSource(feedUrl: officialFeed, publicKey: officialPublicKey, isTest: false)

    public let feedUrl: URL
    public let publicKey: String
    public let isTest: Bool
    public var signatureUrl: URL { URL(string: feedUrl.absoluteString + ".sig")! }

    /// A loopback feed and its test key, for end-to-end tests only. Anything but loopback is refused.
    public static func forTest(feed: URL, publicKey: String) throws -> UpdateSource {
        guard ["http", "https"].contains(feed.scheme ?? ""), ["127.0.0.1", "localhost", "::1"].contains(feed.host ?? "") else {
            throw UpdateError("A test update feed must be an http(s) loopback URL.")
        }
        return UpdateSource(feedUrl: feed, publicKey: publicKey, isTest: true)
    }

    /// Asset URLs a feed may name: the update repo's release downloads (or, in test mode, the feed's own server).
    public func isAllowedAsset(_ u: URL) -> Bool {
        if isTest { return sameServer(u, feedUrl) }
        return u.scheme == "https" && u.absoluteString.hasPrefix(Self.officialAssetPrefix)
    }

    /// Redirect targets: GitHub's own https hosts (release downloads land on githubusercontent.com).
    public func isAllowedRedirect(_ u: URL) -> Bool {
        if isTest { return sameServer(u, feedUrl) }
        guard u.scheme == "https", let h = u.host?.lowercased() else { return false }
        return h == "github.com" || h.hasSuffix(".github.com") || h == "githubusercontent.com" || h.hasSuffix(".githubusercontent.com")
    }

    private func sameServer(_ a: URL, _ b: URL) -> Bool { a.scheme == b.scheme && a.host == b.host && a.port == b.port }
}

/// updates.json.sig: base64 of the raw ECDSA P-256 signature (r||s, 64 bytes) over the feed's bytes.
public enum FeedSignature {
    public static func verify(feed: Data, signatureFile: Data, publicKeySpki: String) -> Bool {
        guard let text = String(data: signatureFile, encoding: .ascii)?.trimmingCharacters(in: .whitespacesAndNewlines),
              let sig = Data(base64Encoded: text), sig.count == 64,
              let der = Data(base64Encoded: publicKeySpki),
              let key = try? P256.Signing.PublicKey(derRepresentation: der),
              let signature = try? P256.Signing.ECDSASignature(rawRepresentation: sig) else { return false }
        return key.isValidSignature(signature, for: feed)
    }
}

/// The published updates.json: the newest Mac app and the newest firmware per board.
public struct UpdateFeed: Equatable {
    public static let maxAppBytes: Int64 = 200 * 1024 * 1024
    public static let maxFirmwareBytes: Int64 = 4 * 1024 * 1024

    /// app.macos (the Windows app reads app.windows from the same feed).
    public var macApp: UpdatePackage?
    public var firmware: [FirmwarePackage]

    /// Parses and validates; any bad entry rejects the whole feed (a tampered feed is not partly trusted).
    public static func parse(_ data: Data, source: UpdateSource) throws -> UpdateFeed {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw UpdateError("The update feed is not valid JSON.")
        }
        guard let schema = root["schema"] as? NSNumber, schema.intValue == 1, !isJsonBool(schema) else {
            throw UpdateError("Unsupported update feed (schema).")
        }
        var app: UpdatePackage?
        if let apps = root["app"] as? [String: Any], let mac = apps["macos"] as? [String: Any] {
            let (v, u, sha, size) = try entry(mac, source, maxAppBytes)
            app = UpdatePackage(version: v, url: u, sha256: sha, size: size)
        }
        var fw: [FirmwarePackage] = []
        if let list = root["firmware"] as? [Any] {
            for item in list {
                guard let e = item as? [String: Any] else { throw UpdateError("The update feed has an unexpected shape.") }
                let board = e["board"] as? String ?? ""
                guard board.range(of: "^[a-z0-9][a-z0-9-]{0,31}$", options: .regularExpression) != nil else {
                    throw UpdateError("Invalid board id in the update feed.")
                }
                let (v, u, sha, size) = try entry(e, source, maxFirmwareBytes)
                fw.append(FirmwarePackage(board: board, version: v, url: u, sha256: sha, size: size))
            }
        }
        return UpdateFeed(macApp: app, firmware: fw)
    }

    /// JSON true/false arrive as CFBoolean. `is Bool` can't tell them apart: NSNumber 0 and 1 also pass it.
    private static func isJsonBool(_ n: NSNumber) -> Bool {
        CFGetTypeID(n) == CFBooleanGetTypeID()
    }

    private static func entry(_ e: [String: Any], _ source: UpdateSource, _ max: Int64) throws -> (SemVer, URL, String, Int64) {
        guard let vs = e["version"] as? String, vs.split(separator: ".").count >= 3, let v = SemVer(vs) else {
            throw UpdateError("Invalid version in the update feed.")
        }
        guard let us = e["url"] as? String, let url = URL(string: us), url.scheme != nil, source.isAllowedAsset(url) else {
            throw UpdateError("The update feed points outside the Touch Deck update repository.")
        }
        guard let sha = e["sha256"] as? String, sha.range(of: "^[0-9a-fA-F]{64}$", options: .regularExpression) != nil else {
            throw UpdateError("The update feed has no valid SHA-256.")
        }
        guard let n = e["size"] as? NSNumber, !isJsonBool(n), n.int64Value > 0, n.int64Value <= max,
              Double(n.int64Value) == n.doubleValue else {
            throw UpdateError("The update feed has an invalid size.")
        }
        return (v, url, sha.lowercased(), n.int64Value)
    }
}

public struct UpdateChoice: Equatable {
    public var app: UpdatePackage?
    public var firmware: FirmwarePackage?
}

public enum UpdateSelector {
    /// Strictly newer only: never a downgrade. Firmware only for the connected board.
    public static func select(_ feed: UpdateFeed, currentApp: SemVer, device: FirmwareInfo?) -> UpdateChoice {
        let app = feed.macApp.flatMap { $0.version > currentApp ? $0 : nil }
        var fw: FirmwarePackage?
        if let d = device, d.known, let mine = feed.firmware.first(where: { $0.board == d.board }) {
            if let running = d.semVer { if mine.version > running { fw = mine } } else { fw = mine }
        }
        return UpdateChoice(app: app, firmware: fw)
    }
}
