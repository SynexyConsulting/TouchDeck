import Foundation
import CryptoKit
import AppKit

/// Reads the feed and downloads packages. Each redirect hop is checked against the source (at
/// most 5); the feed must carry a valid signature from the pinned key before it is parsed;
/// downloads are size-capped and SHA-256 verified while streaming. Port of UpdateClient (Windows).
public final class UpdateClient: NSObject, URLSessionTaskDelegate {
    private static let maxFeedBytes = 1_000_000, maxSignatureBytes = 1024, maxHops = 5
    private let source: UpdateSource
    private var session: URLSession!
    private let feedTimeout: TimeInterval
    private let idleTimeout: TimeInterval
    private let hopsLock = NSLock()
    private var hops: [Int: Int] = [:]

    public init(source: UpdateSource, feedTimeout: TimeInterval = 30, idleTimeout: TimeInterval = 30) {
        self.source = source
        self.feedTimeout = feedTimeout
        self.idleTimeout = idleTimeout
        super.init()
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = idleTimeout            // longest wait for the next bytes
        config.httpAdditionalHeaders = ["User-Agent": "TouchDeck-macOS/\(CoreInfo.version)"]
        session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }

    /// Redirects: only to GitHub https hosts (or the test server), at most 5 hops. A refused
    /// redirect hands back the 3xx response, which the callers reject.
    public func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                           newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        let n = hopsLock.withLock { () -> Int in hops[task.taskIdentifier, default: 0] += 1; return hops[task.taskIdentifier]! }
        guard n <= Self.maxHops, let url = request.url, source.isAllowedRedirect(url) else { return completionHandler(nil) }
        completionHandler(request)
    }

    /// The feed, or nil when nothing has been published yet (404).
    public func fetchFeed() async throws -> UpdateFeed? {
        try await withTimeout(feedTimeout, message: "The update server timed out.") { [self] in
            guard let feed = try await readSmall(source.feedUrl, max: Self.maxFeedBytes) else { return nil }
            guard let sig = try await readSmall(source.signatureUrl, max: Self.maxSignatureBytes) else {
                throw UpdateError("The update feed has no signature; it is not trusted.")
            }
            guard FeedSignature.verify(feed: feed, signatureFile: sig, publicKeySpki: source.publicKey) else {
                throw UpdateError("The update feed's signature doesn't match; it is not trusted.")
            }
            return try UpdateFeed.parse(feed, source: source)
        }
    }

    /// A small resource (feed, signature), read with a hard size cap; nil on 404.
    private func readSmall(_ url: URL, max: Int) async throws -> Data? {
        let (bytes, response) = try await get(url)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { return nil }
        guard (200..<300).contains(code) else { throw UpdateError("The update server answered \(code).") }
        if response.expectedContentLength > Int64(max) { throw UpdateError("The update feed is too large.") }
        var data = Data()
        for try await b in bytes {
            data.append(b)
            if data.count > max { throw UpdateError("The update feed is too large.") }
        }
        return data
    }

    /// Downloads `url` to `dest`; any mismatch deletes it and throws.
    public func download(_ url: URL, sha256: String, size: Int64, to dest: URL, progress: ((Double) -> Void)?) async throws {
        guard source.isAllowedAsset(url) else { throw UpdateError("Download refused: not from the Touch Deck update repository.") }
        let tmp = dest.appendingPathExtension("part")
        defer { try? FileManager.default.removeItem(at: tmp) }
        let (bytes, response) = try await get(url)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else { throw UpdateError("Download failed (\(code)).") }
        if response.expectedContentLength >= 0 && response.expectedContentLength != size {
            throw UpdateError("Download refused: its size doesn't match the update feed.")
        }
        FileManager.default.createFile(atPath: tmp.path, contents: nil)
        let file = try FileHandle(forWritingTo: tmp)
        defer { try? file.close() }
        var hash = SHA256()
        var total: Int64 = 0
        var chunk = Data()
        chunk.reserveCapacity(1 << 16)
        func flush() throws {
            hash.update(data: chunk)
            try file.write(contentsOf: chunk)
            chunk.removeAll(keepingCapacity: true)
            progress?(Double(total) / Double(size))
        }
        for try await b in bytes {
            total += 1
            if total > size { throw UpdateError("Download refused: longer than the update feed says.") }
            chunk.append(b)
            if chunk.count >= 1 << 16 { try flush() }
        }
        try flush()
        guard total == size else { throw UpdateError("Download incomplete.") }
        let got = hash.finalize().map { String(format: "%02x", $0) }.joined()
        guard got == sha256.lowercased() else { throw UpdateError("Download refused: its SHA-256 doesn't match the update feed.") }
        try? file.close()
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: tmp, to: dest)
    }

    private func get(_ url: URL) async throws -> (URLSession.AsyncBytes, URLResponse) {
        do {
            let (bytes, response) = try await session.bytes(for: URLRequest(url: url))
            if let http = response as? HTTPURLResponse, (300..<400).contains(http.statusCode) {
                throw UpdateError("Download refused: redirected outside GitHub.")
            }
            return (bytes, response)
        } catch let e as UpdateError {
            throw e
        } catch let e as URLError where e.code == .timedOut {
            throw UpdateError("The update server timed out.")
        } catch {
            throw UpdateError("Couldn't reach the update server: \(error.localizedDescription)")
        }
    }

    private func withTimeout<T>(_ seconds: TimeInterval, message: String, _ body: @escaping () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await body() }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                throw UpdateError(message)
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }
}

/// Outcome of a check: a choice (possibly empty), "nothing published yet", or an error text.
public struct UpdateCheckOutcome: Equatable {
    public var choice: UpdateChoice?
    public var nothingPublished: Bool
    public var error: String?
    /// The verified feed, kept so each attached board's firmware offer is worked out without fetching it again.
    public var feed: UpdateFeed?
    public init(choice: UpdateChoice?, nothingPublished: Bool, error: String?, feed: UpdateFeed? = nil) {
        self.choice = choice
        self.nothingPublished = nothingPublished
        self.error = error
        self.feed = feed
    }
}

/// Checks the feed and downloads packages into one per-user folder under fixed names.
public final class UpdateService {
    private let client: UpdateClient
    private let downloadDir: URL

    public static var defaultDownloadDir: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("TouchDeck/Updates")
    }

    public init(client: UpdateClient, downloadDir: URL = UpdateService.defaultDownloadDir) {
        self.client = client
        self.downloadDir = downloadDir
    }

    public func check(currentApp: SemVer, device: FirmwareInfo?) async -> UpdateCheckOutcome {
        do {
            guard let feed = try await client.fetchFeed() else { return UpdateCheckOutcome(choice: nil, nothingPublished: true, error: nil) }
            return UpdateCheckOutcome(choice: UpdateSelector.select(feed, currentApp: currentApp, device: device), nothingPublished: false,
                                      error: nil, feed: feed)
        } catch {
            return UpdateCheckOutcome(choice: nil, nothingPublished: false, error: "\(error)")
        }
    }

    /// The Mac app ships as a signed, notarized installer package (.pkg).
    public func downloadApp(_ p: UpdatePackage, progress: ((Double) -> Void)?) async throws -> URL {
        try await download(p.url, p.sha256, p.size, "TouchDeck-update.pkg", progress)
    }

    /// The board id was validated by the feed parser ([a-z0-9-]), so it is safe in a file name.
    public func downloadFirmware(_ p: FirmwarePackage, progress: ((Double) -> Void)?) async throws -> URL {
        try await download(p.url, p.sha256, p.size, "\(p.board)-update.uf2", progress)
    }

    /// Removes the updater's own files (never anything else), after checking the folder is a real
    /// folder: a symlink planted in its place would redirect the deletes and the downloads.
    public static func clearDownloads(_ dir: URL) throws {
        guard FileManager.default.fileExists(atPath: dir.path) else { return }
        let values = try dir.resourceValues(forKeys: [.isSymbolicLinkKey])
        if values.isSymbolicLink == true { throw UpdateError("The update folder is a link; refusing to use it.") }
        for f in try FileManager.default.contentsOfDirectory(atPath: dir.path)
        where f.range(of: "^(TouchDeck-update\\.pkg|[a-z0-9][a-z0-9-]*-update\\.uf2)(\\.part)?$", options: .regularExpression) != nil {
            try FileManager.default.removeItem(at: dir.appendingPathComponent(f))
        }
    }

    private func download(_ url: URL, _ sha: String, _ size: Int64, _ name: String, _ progress: ((Double) -> Void)?) async throws -> URL {
        try FileManager.default.createDirectory(at: downloadDir, withIntermediateDirectories: true)
        try Self.clearDownloads(downloadDir)                       // nothing stale is ever reused
        let dest = downloadDir.appendingPathComponent(name)
        try await client.download(url, sha256: sha, size: size, to: dest, progress: progress)
        return dest
    }

    /// Opens the verified package in Installer; the app quits so Installer can replace it. A detached
    /// shell waits for Installer to close and opens Touch Deck again (the Windows app restarts
    /// after msiexec the same way); a cancelled install just reopens the old version.
    public static func launchInstaller(_ pkg: URL, relaunch bundleID: String? = Bundle.main.bundleIdentifier) {
        guard let bundleID else { NSWorkspace.shared.open(pkg); return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", relaunchScript, "touchdeck-update", pkg.path, bundleID]
        do { try p.run() } catch { NSWorkspace.shared.open(pkg) }
    }

    /// $1 the package, $2 the bundle id. `open -W` returns once Installer quits.
    static let relaunchScript = #"/usr/bin/open -W "$1" && sleep 1; /usr/bin/open -b "$2""#
}
