import Foundation
import AppKit
import SwiftUI
import TouchDeckCore

enum Health { case idle, ok, bad }

/// The app-wide side of the window and menu bar: the boards (one `BoardController` per tab), which
/// one is selected, settings, app updates, recent clips and the activity log. A port of the Windows
/// AppController. Core events arrive on background threads and are applied on the main actor.
@MainActor
final class AppController: ObservableObject {
    private static let logLimit = 500

    private var settings: AppSettings
    private let sink = SwitchableSink(real: CGEventSink())
    private let selection = DefaultSelectionProvider()
    private let manager: DeviceManager
    private let updates = UpdateService(client: UpdateClient(source: .official))
    private var timers: [Timer] = []
    private var hotkey: Hotkey?

    let firmwareDir: URL
    let bundled: [BundledFirmware]
    let history = ClipHistory()

    @Published private(set) var historyItems: [String] = []
    @Published private(set) var logLines: [LogEntry] = []
    /// Show only the selected board's lines (and the app's own); offered with more than one board.
    @Published var onlySelectedLog = false

    // Boards
    /// Every attached board, one tab each, in the order they appeared.
    @Published private(set) var boards: [BoardController] = []
    /// The board the window shows; the placeholder ("No board") while none is attached. Implicitly
    /// unwrapped because the placeholder needs `self`, so it is published by hand rather than with
    /// @Published (whose wrapper doesn't reliably keep the implicit unwrap).
    private(set) var selected: BoardController! { willSet { objectWillChange.send() } }
    private var placeholder: BoardController!
    /// The menu bar icon's view of all boards: connected if one is, bad if one has a problem.
    @Published private(set) var health = Health.idle
    /// The board being installed; the others keep working, but wait to be installed.
    @Published private(set) var installing: BoardController? {
        didSet { boards.forEach { $0.refreshUpdateOffer(); $0.refreshFeedOffer() } }
    }
    private var scanningNewBoards = false

    // Updates
    @Published private(set) var appUpdateText = "Not checked yet"
    @Published private(set) var canInstallApp = false
    @Published private(set) var checkingUpdates = false
    /// The last verified feed: each board's firmware offer is worked out from it.
    private(set) var lastFeed: UpdateFeed?
    /// What a board's Firmware line says while there is no feed.
    private(set) var feedNote = ""
    private var lastChoice: UpdateChoice?
    private var checkedOnce = false

    // Permissions
    @Published private(set) var accessibilityTrusted = AccessibilityPermission.isTrusted

    static var appVersion: String { CoreInfo.version }

    init() {
        settings = AppSettings.load()
        firmwareDir = Bundle.main.resourceURL!.appendingPathComponent("firmware")
        bundled = BundledFirmware.loadManifest(firmwareDir)
        sink.dryRun = settings.dryRun

        let keyboard = MacKeyboardState()
        let sink = self.sink, selection = self.selection
        manager = DeviceManager(
            scan: DeviceScanner.scan,
            openTransport: { PosixSerialTransport(device: $0) },
            makeSession: { DeviceSession(transport: $0, injector: Injector(sink: sink), keyboard: keyboard, selection: selection) })
        // The placeholder needs `self`, so `selected` (implicitly unwrapped) is set once it exists.
        placeholder = BoardController(app: self, key: "", port: nil)
        selected = placeholder

        sink.dryRunEvent = { [weak self] e in Task { @MainActor in self?.addLog("dry run: \(e)") } }
        manager.onSlotChanged = { [weak self] port, s in Task { @MainActor in self?.board(port, port: port).applyState(s) } }
        manager.onSlotRemoved = { [weak self] port in Task { @MainActor in self?.removeBoard(port) } }
        manager.onSessionStarted = { [weak self] port, s in self?.hookSession(port, s) }
        history.changed = { [weak self] in Task { @MainActor in self?.historyItems = self?.history.items ?? [] } }
    }

    func start() {
        _ = TargetApp.shared                      // starts tracking the app COPY reads from
        manager.start()
        // The board mirrors: known once a session has either seen STATE or given up waiting.
        timers.append(Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.boards.forEach { $0.refreshMirror() }
                self?.accessibilityTrusted = AccessibilityPermission.isTrusted
            }
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkNewBoards() }
        })
        // First check shortly after start (below), then an hourly tick that checks once a day.
        timers.append(Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.dailyUpdateCheck() }
        })
        // ⌃⌥C sends the selection, like Ctrl+Alt+C on Windows.
        hotkey = Hotkey { [weak self] in Task { @MainActor in self?.sendSelection() } }
        if hotkey == nil { addLog("⌃⌥C is taken by another app; the send-selection hotkey is off") }
        if !AccessibilityPermission.isTrusted { AccessibilityPermission.request() }
        addLog("Touch Deck \(Self.appVersion) started; bundled firmware: \(bundledSummary)")
        Task {
            try? await Task.sleep(for: .seconds(15))
            await dailyUpdateCheck()
        }
    }

    func stop() {
        timers.forEach { $0.invalidate() }
        timers.removeAll()
        hotkey = nil
        manager.stop()
    }

    var bundledSummary: String {
        bundled.isEmpty ? "none" : bundled.map { "\($0.board) \($0.version)" }.joined(separator: ", ")
    }

    // MARK: boards

    /// The tab strip and the log filter appear once there is more than one board.
    var showTabs: Bool { boards.count > 1 }
    var anyConnected: Bool { boards.contains { $0.isConnected } }

    func find(_ key: String) -> BoardController? { boards.first { $0.key == key } }

    /// - Parameter remember: false when the app picks the tab itself (boards coming and going): only
    ///   the user's choice (a tab click, a just-installed board) is the "last used" board, selected
    ///   again when it returns.
    func select(_ b: BoardController, remember: Bool = true) {
        let next = boards.contains(where: { $0 === b }) ? b : (boards.first ?? placeholder!)
        if remember, !next.port.isEmpty, settings.preferredPort != next.port { update { $0.preferredPort = next.port } }
        if next === selected { return }
        selected.isSelected = false
        selected = next
        selected.isSelected = !selected.isPlaceholder
    }

    @discardableResult
    private func board(_ key: String, port: String?, newBoard: NewBoard? = nil) -> BoardController {
        if let b = find(key) { return b }
        let b = BoardController(app: self, key: key, port: port)
        if let newBoard { b.setNewBoard(newBoard) }
        boards.append(b)
        reselect(added: key)
        boardsChanged()
        return b
    }

    private func removeBoard(_ key: String) {
        guard let b = find(key), installing !== b else { return }   // being installed: its port comes and goes
        b.detached = true
        boards.removeAll { $0 === b }
        if b === selected { selected.isSelected = false }
        reselect(added: nil)
        boardsChanged()
    }

    private func reselect(added: String?) {
        let current = selected.isPlaceholder || !boards.contains(where: { $0 === selected }) ? nil : selected.key
        let key = BoardSelection.next(boards.map(\.key), current: current, added: added, preferred: settings.preferredPort)
        select(key.flatMap { find($0) } ?? placeholder!, remember: false)
    }

    private func boardsChanged() {
        if !showTabs { onlySelectedLog = false }
        refreshHealth()
    }

    /// Called by a board when its link or name changes.
    func refreshHealth() {
        health = boards.contains { $0.isConnected } ? .ok : boards.contains { $0.health == .bad } ? .bad : .idle
    }

    /// A board's notification names the board once there is more than one.
    func notifyBoard(_ b: BoardController, _ title: String, _ text: String) {
        refreshHealth()
        Notifier.post(title, showTabs ? "\(b.label): \(text)" : text)
    }

    /// Runs on the manager's thread, before the session reads anything.
    nonisolated private func hookSession(_ port: String, _ s: DeviceSession) {
        let mirror = MirrorState(model: UiModel.for(s.kind, board: s.firmware?.board))   // both RP boards are CAFE:4011
        Task { @MainActor in self.board(port, port: port).attach(s, mirror: mirror) }
        s.onLog = { [weak self] t in Task { @MainActor in self?.live(port, s)?.log("board: \(t)") } }
        s.onDiagnostics = { [weak self] d in Task { @MainActor in self?.live(port, s)?.showDiagnostics(d) } }
        s.onState = { [weak self] st in
            Task { @MainActor in
                guard let b = self?.live(port, s) else { return }
                b.applyBoardState(st)
                b.applyMirror(mirror, .state(st))
            }
        }
        s.onText = { [weak self] k, v in Task { @MainActor in self?.live(port, s)?.applyMirror(mirror, .text(key: k, value: v)) } }
        s.onClipText = { [weak self] bytes in Task { @MainActor in self?.live(port, s)?.applyMirror(mirror, .clipText(bytes)) } }
        s.onClipSent = { [weak self] text, src, lost in
            Task { @MainActor in
                guard let self else { return }
                self.history.add(text)
                let note = lost > 0 ? ", \(lost) non-ASCII characters as '?'" : ""
                let line = "Sent \(text.count) characters from \(Self.sourceName(src))\(note)"
                if let b = self.live(port, s) { b.log(line) } else { self.addLog(line) }
            }
        }
    }

    /// The board `s` is still the session of: a closing session's late events must not touch the
    /// board's next session.
    private func live(_ port: String, _ s: DeviceSession) -> BoardController? {
        guard let b = find(port), b.session === s else { return nil }
        return b
    }

    private static func sourceName(_ src: String) -> String {
        switch src {
        case "select": return "the selection"
        case "clipbd": return "the clipboard"
        default: return "the app"
        }
    }

    // MARK: actions

    /// The global hotkey: the same as tapping COPY on the selected board.
    func sendSelection() {
        guard let s = selected.session else {
            Notifier.post("Touch Deck", "No board connected.")
            return
        }
        let selection = self.selection
        Task.detached {
            let (text, src) = selection.grab()
            guard !text.isEmpty else {
                Notifier.post("Touch Deck", "Nothing selected and the clipboard is empty.")
                return
            }
            s.sendText(text, source: src)
            Notifier.post("Sent to Touch Deck", Self.preview(AsciiText.transliterate(text).text))
        }
    }

    nonisolated static func preview(_ text: String) -> String {
        let one = text.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)
        return one.count <= 60 ? one : String(one.prefix(57)) + "..."
    }

    func copyToPasteboard(_ text: String) {
        if Pasteboard.write(text) { addLog("Copied a recent clip to the clipboard") }
    }

    func clearHistory() { history.clear() }

    /// The log as shown: everything, or the selected board's lines and the app's own.
    var shownLog: [LogEntry] {
        let tag = selected.logTag
        return logLines.filter { $0.shows(selected: tag, onlySelected: onlySelectedLog) }
    }

    /// Copies what the log shows (with "Only selected board", that board's lines).
    func copyLog() { Pasteboard.write(shownLog.map(\.line).joined(separator: "\n")) }

    // MARK: new boards (no Touch Deck firmware)

    /// Every 2 s: Raspberry Pi boards on their factory firmware or in their bootloader (by USB ID),
    /// each its own tab. Paused while an install runs: the board being flashed passes through its
    /// bootloader.
    private func checkNewBoards() {
        guard installing == nil, !scanningNewBoards else { return }
        scanningNewBoards = true
        Task.detached {
            let found = NewBoards.scan()
            await MainActor.run {
                self.scanningNewBoards = false
                guard self.installing == nil else { return }
                var keys = Set<String>()
                for nb in found {
                    let key = Self.newBoardKey(nb)
                    keys.insert(key)
                    let b = self.board(key, port: nb.port, newBoard: nb)
                    if !b.isConnected { b.setNewBoard(nb) }
                }
                for b in self.boards where b.newBoard != nil && !keys.contains(b.key) {
                    if self.manager.session(for: b.key) != nil { b.setNewBoard(nil) }   // its port runs Touch Deck now
                    else { self.removeBoard(b.key) }
                }
            }
        }
    }

    nonisolated private static func newBoardKey(_ nb: NewBoard) -> String { nb.port ?? "boot:\(nb.chip)" }

    // MARK: firmware installs (one at a time)

    /// Installs a UF2 (bundled, or downloaded and verified) for `model` on `board`: chip and model are
    /// checked before the board is touched. Success is a session that wasn't there before reporting
    /// the model, so another board of the same model can't be mistaken for it.
    func flash(_ board: BoardController, _ uf2: URL, label: String, model: BoardModel, enterBootloader: @escaping () -> Void,
               progress: @escaping @MainActor (String) -> Void) async -> Bool {
        guard installing == nil else { return false }
        installing = board
        let manager = self.manager
        let before = manager.sessions.map { ObjectIdentifier($0) }
        let fresh = FreshSession()
        board.log("Installing firmware \(label)")
        let steps = UpdateSteps(
            enterBootloader: enterBootloader,
            findBootDrive: { Uf2.findBootDrive(Uf2.mountedVolumes(), chip: model.chip) },
            copyImage: { try Uf2.copy($0, toDrive: $1) },
            readRunningFirmware: {
                let s = manager.sessions.first { !before.contains(ObjectIdentifier($0)) && $0.firmware?.board == model.board }
                fresh.session = s
                return s?.firmware
            },
            bootloaderChip: {
                if Uf2.findBootDrive(Uf2.mountedVolumes(), chip: .rp2350) != nil { return .rp2350 }
                if Uf2.findBootDrive(Uf2.mountedVolumes(), chip: .rp2040) != nil { return .rp2040 }
                return nil
            })
        let result = await FirmwareUpdater.install(uf2, model: model, steps: steps) { [weak board] m in
            Task { @MainActor in board?.log(m); progress(m) }
        }
        board.log(result.message)
        notifyBoard(board, result.ok ? "Firmware updated" : "Firmware update failed", result.message)
        installing = nil
        // The installed board's tab is the one to show: its port may be new (a new board gets one).
        if let s = fresh.session, let port = manager.links.first(where: { $0.session === s })?.port, port != board.key {
            let now = self.board(port, port: port)
            if !board.isConnected && manager.session(for: board.key) == nil { removeBoard(board.key) }
            select(now)
        } else if manager.session(for: board.key) == nil && board.newBoard == nil && !board.isConnected {
            removeBoard(board.key)                    // gone and not back: drop the tab
        }
        return result.ok
    }

    func downloadFirmware(_ p: FirmwarePackage) async throws -> URL { try await updates.downloadFirmware(p, progress: nil) }

    // MARK: updates

    /// The first check comes shortly after start, then the hourly tick checks once a day. A failed
    /// check also counts, so an offline Mac doesn't ask GitHub every tick.
    private func dailyUpdateCheck() async {
        guard settings.checkForUpdates, !checkingUpdates else { return }
        if checkedOnce, let last = settings.lastUpdateCheck, Date().timeIntervalSince(last) < 24 * 3600 { return }
        _ = await checkForUpdates(manual: false)
    }

    @discardableResult
    func checkForUpdates(manual: Bool) async -> UpdateCheckOutcome {
        if checkingUpdates { return UpdateCheckOutcome(choice: lastChoice, nothingPublished: false, error: nil, feed: lastFeed) }
        checkingUpdates = true
        defer { checkingUpdates = false }
        if manual { (appUpdateText, feedNote) = ("Checking...", "") }
        let device = selected.isConnected ? selected.state.firmware : nil
        let outcome = await updates.check(currentApp: SemVer(Self.appVersion) ?? SemVer(0, 0, 0), device: device)
        checkedOnce = true
        update { $0.lastUpdateCheck = Date() }
        applyOutcome(outcome)
        return outcome
    }

    private func applyOutcome(_ o: UpdateCheckOutcome) {
        lastChoice = o.choice
        if let err = o.error {
            (appUpdateText, canInstallApp, feedNote) = ("Couldn't check: \(err)", false, "")
            addLog("Update check failed: \(err)")
        } else if o.nothingPublished {
            (appUpdateText, canInstallApp, feedNote, lastFeed) = ("No updates published yet", false, "", nil)
        } else {
            lastFeed = o.feed
            let app = o.choice?.app
            appUpdateText = app.map { "\($0.version) available" } ?? "Up to date"
            canInstallApp = app != nil
            if let app { Notifier.post("Touch Deck update", "Version \(app.version) is available. Open Settings to install it.") }
        }
        for b in boards {
            b.refreshFeedOffer()
            if b.canInstallFirmware { b.log("Firmware \(b.firmwareUpdateText) on the update feed") }
        }
        placeholder.refreshFeedOffer()
    }

    func installAppUpdate() async {
        guard let p = lastChoice?.app, canInstallApp else { return }
        canInstallApp = false
        appUpdateText = "Downloading..."
        do {
            let pkg = try await updates.downloadApp(p) { f in
                Task { @MainActor in self.appUpdateText = "Downloading... \(Int((f * 100).rounded()))%" }
            }
            addLog("Downloaded and verified Touch Deck \(p.version); opening the installer")
            appUpdateText = "Installing... Touch Deck quits so the installer can replace it."
            UpdateService.launchInstaller(pkg)
            NSApp.terminate(nil)
        } catch {
            appUpdateText = "Update failed: \(Self.describe(error))"
            canInstallApp = true
            addLog(appUpdateText)
        }
    }

    nonisolated static func describe(_ error: Error) -> String {
        if let e = error as? UpdateError { return e.message }
        return error.localizedDescription
    }

    // MARK: settings

    var dryRun: Bool {
        get { settings.dryRun }
        set {
            sink.dryRun = newValue
            update { $0.dryRun = newValue }
            addLog(newValue ? "Dry run on: keys and mouse from the board are logged, not performed" : "Dry run off")
        }
    }

    var diagnosticsEnabled: Bool {
        get { settings.diagnostics }
        set {
            manager.sessions.forEach { $0.diagnosticsEnabled = newValue }
            update { $0.diagnostics = newValue }
            if !newValue { boards.forEach { $0.clearDiagnostics() } }
        }
    }

    var checkForUpdatesAutomatically: Bool {
        get { settings.checkForUpdates }
        set { update { $0.checkForUpdates = newValue } }
    }

    var launchAtLogin: Bool {
        get { LoginItem.isEnabled }
        set {
            do { try LoginItem.set(newValue) } catch { addLog("Launch at login: \(error.localizedDescription)") }
            objectWillChange.send()
        }
    }

    var launchAtLoginNeedsApproval: Bool { LoginItem.needsApproval }

    func openAccessibilitySettings() {
        AccessibilityPermission.request()
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    private func update(_ change: (inout AppSettings) -> Void) {
        change(&settings)
        do { try settings.save() } catch {
            ErrorLog.append("settings: \(error)")
            addLog("Couldn't save settings: \(error.localizedDescription)")
        }
        objectWillChange.send()
    }

    func addLog(_ text: String, board: String? = nil) {
        logLines.append(LogEntry(port: board, text: text))
        if logLines.count > Self.logLimit { logLines.removeFirst(logLines.count - Self.logLimit) }
    }
}

/// The session an install found coming back (set from the updater's thread, read after it returns).
private final class FreshSession: @unchecked Sendable {
    var session: DeviceSession?
}
