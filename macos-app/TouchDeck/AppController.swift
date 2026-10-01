import Foundation
import AppKit
import SwiftUI
import TouchDeckCore

enum Health { case idle, ok, bad }

/// The app's state and actions: a port of the Windows AppController. Core events arrive on
/// background threads and are applied on the main actor.
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

    // Link
    @Published private(set) var state = LinkState.searching
    @Published private(set) var health = Health.idle
    @Published private(set) var statusText = ""
    @Published private(set) var boardName = "No board"
    @Published private(set) var port = ""
    @Published private(set) var firmwareVersion = ""
    @Published private(set) var isConnected = false
    @Published private(set) var historyItems: [String] = []
    @Published private(set) var logLines: [String] = []
    @Published private(set) var diagnostics: [(String, String)] = []

    // Board state (firmware 1.6.0+)
    @Published private(set) var jigOn = false
    @Published private(set) var jigStatus = ""
    @Published private(set) var jigScaleText = "1.0X"
    @Published private(set) var boardClipText = ""
    @Published private(set) var canClearBoardClip = false
    @Published private(set) var jigConfig: JigConfig?
    @Published private(set) var mirrorFallbackText = ""
    private var jigScale = 0

    // Device mirror (firmware 1.7.0+)
    @Published private(set) var fullMirror = false
    @Published private(set) var mirrorModel = UiModel.rp2040Rect
    @Published private(set) var mirrorImage: CGImage?
    private var mirror: MirrorState?
    private var mirrorFramePending = false
    private(set) var rendererError: String?

    // Firmware install and new boards
    @Published private(set) var newBoard: NewBoard?
    @Published private(set) var newBoardModels: [BoardModel] = []
    @Published var selectedModel: BoardModel? { didSet { refreshUpdateOffer() } }
    @Published private(set) var updateText = ""
    @Published private(set) var canUpdate = false
    @Published private(set) var busy = false { didSet { refreshUpdateOffer() } }
    private var scanningNewBoards = false

    // Updates
    @Published private(set) var appUpdateText = "Not checked yet"
    @Published private(set) var firmwareUpdateText = ""
    @Published private(set) var canInstallApp = false
    @Published private(set) var canInstallFirmware = false
    @Published private(set) var checkingUpdates = false
    private var lastChoice: UpdateChoice?

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
        manager.preferredPort = settings.preferredPort

        sink.dryRunEvent = { [weak self] e in Task { @MainActor in self?.addLog("dry run: \(e)") } }
        manager.onStateChanged = { [weak self] s in Task { @MainActor in self?.applyState(s) } }
        manager.onSessionStarted = { [weak self] s in self?.hookSession(s) }
        history.changed = { [weak self] in Task { @MainActor in self?.historyItems = self?.history.items ?? [] } }
        applyState(.searching)
    }

    func start() {
        _ = TargetApp.shared                      // starts tracking the app COPY reads from
        manager.start()
        // The board mirror: known once the session has either seen STATE or given up waiting.
        timers.append(Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.refreshMirror()
                self?.accessibilityTrusted = AccessibilityPermission.isTrusted
            }
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkNewBoards() }
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.dailyUpdateCheck() }
        })
        // ⌃⌥C sends the selection, like Ctrl+Alt+C on Windows.
        hotkey = Hotkey { [weak self] in Task { @MainActor in self?.sendSelection() } }
        if hotkey == nil { addLog("⌃⌥C is taken by another app; the send-selection hotkey is off") }
        if !AccessibilityPermission.isTrusted { AccessibilityPermission.request() }
        addLog("Touch Deck \(Self.appVersion) started; bundled firmware: \(bundledSummary)")
        Task { await dailyUpdateCheck() }
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

    // MARK: link

    private func applyState(_ s: LinkState) {
        let was = state
        state = s
        isConnected = s.status == .connected
        if !isConnected { jigConfig = nil }
        port = s.device?.port ?? ""
        boardName = s.device.map { BoardKinds.displayName($0.kind, board: s.firmware?.board) } ?? "No board"
        firmwareVersion = s.firmware.map { $0.known ? "\($0.version)  (\($0.board))" : "unknown (older than 1.5.0)" } ?? ""
        switch s.status {
        case .connected: (health, statusText) = (.ok, "Connected")
        case .portBusy: (health, statusText) = (.bad, "\(port) is in use by another program")
        case .notResponding: (health, statusText) = (.bad, "\(port) doesn't answer. Is it running Touch Deck firmware?")
        case .searching: (health, statusText) = (.idle, "Looking for a Touch Deck...")
        }
        if !settings.diagnostics || !isConnected { diagnostics = [] }
        if !isConnected { resetMirror() }
        refreshUpdateOffer()

        if s.status == was.status && s.device == was.device { return }
        if isConnected {
            addLog("Connected: \(boardName) on \(port), firmware \(firmwareVersion)")
            Notifier.post("Touch Deck connected", "\(boardName), firmware \(s.firmware?.version ?? "?")")
            if settings.preferredPort != port { update { $0.preferredPort = self.port } }
        } else if was.status == .connected {
            addLog("Disconnected")
            if !busy { Notifier.post("Touch Deck disconnected", "Plug it back in; the app reconnects by itself.") }
        } else if s.status != .searching {
            addLog(statusText)
        }
    }

    /// Runs on the manager's thread, before the session reads anything.
    nonisolated private func hookSession(_ s: DeviceSession) {
        let model = UiModel.for(s.kind, board: s.firmware?.board)   // both RP boards are CAFE:4011
        Task { @MainActor in
            s.diagnosticsEnabled = self.settings.diagnostics
            self.startMirror(MirrorState(model: model))
        }
        s.onLog = { [weak self] t in Task { @MainActor in self?.addLog("board: \(t)") } }
        s.onDiagnostics = { [weak self] d in Task { @MainActor in self?.diagnostics = d.sorted { $0.key < $1.key }.map { ($0.key, $0.value) } } }
        s.onState = { [weak self] st in Task { @MainActor in self?.applyBoardState(st); self?.applyMirror(.state(st)) } }
        s.onText = { [weak self] k, v in Task { @MainActor in self?.applyMirror(.text(key: k, value: v)) } }
        s.onClipText = { [weak self] b in Task { @MainActor in self?.applyMirror(.clipText(b)) } }
        s.onClipSent = { [weak self] text, src, lost in
            Task { @MainActor in
                self?.history.add(text)
                let note = lost > 0 ? ", \(lost) non-ASCII characters as '?'" : ""
                self?.addLog("Sent \(text.count) characters from \(Self.sourceName(src))\(note)")
            }
        }
    }

    private static func sourceName(_ src: String) -> String {
        switch src {
        case "select": return "the selection"
        case "clipbd": return "the clipboard"
        default: return "the app"
        }
    }

    // MARK: actions

    func sendText(_ text: String) {
        if let s = manager.session, !text.isEmpty { s.sendText(text) }
    }

    /// The global hotkey: the same as tapping COPY on the board.
    func sendSelection() {
        guard let s = manager.session else {
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

    func swipe(left: Bool) { manager.session?.swipe(left: left) }
    func tap(x: Int, y: Int) { manager.session?.tap(x: x, y: y) }
    func toggleJiggler() { manager.session?.setJiggler(!jigOn) }
    func cycleScale() { manager.session?.setScale((jigScale + 1) % 3) }
    func clearBoardClip() { manager.session?.clearClip() }
    func pressButton(long: Bool) { manager.session?.pressButton(long: long) }

    /// Jiggler settings: send the change; the controls follow what the board reports back.
    func setJigConfig(_ c: JigConfig) {
        manager.session?.setJigConfig(menuOn: c.menuOn, f15: c.f15, openS: c.openS, pauseS: c.pauseS)
    }

    func copyToPasteboard(_ text: String) { Pasteboard.write(text) }

    // MARK: board state

    private func applyBoardState(_ st: StateReport) {
        jigOn = st.jigOn
        jigScale = st.scale
        jigScaleText = JigView.scaleText(st.scale)
        jigStatus = JigView.status(st)
        boardClipText = JigView.clipText(st)
        canClearBoardClip = JigView.canClear(st)
        jigConfig = JigView.config(st)
    }

    private func refreshMirror() {
        guard isConnected, let supported = manager.session?.mirrorSupported else {
            if !isConnected {
                // A new board must not inherit the last one's jiggler state or screen.
                resetMirror()
                mirrorFallbackText = "Connect a board to see and control it here."
                jigOn = false
                jigStatus = ""
                boardClipText = ""
                canClearBoardClip = false
            }
            return
        }
        if fullMirror { mirrorFallbackText = "" }
        else if !supported { mirrorFallbackText = "This board's firmware is older than 1.6.0. Install newer firmware to see it here." }
        else if rendererError != nil { mirrorFallbackText = "The device view couldn't load, so only the jiggler is shown." }
        else if let v = state.firmware?.semVer, v < SemVer(1, 7, 0) { mirrorFallbackText = "Install firmware 1.7.0 or later to see and use the whole device here." }
        else { mirrorFallbackText = "" }
    }

    private func startMirror(_ m: MirrorState) {
        mirror = m
        mirrorModel = m.model
        fullMirror = false
        mirrorImage = nil
        rendererError = NativeUi.unavailableReason(m.model)
        if let e = rendererError { addLog("Device view unavailable: \(e)") }
    }

    private func applyMirror(_ message: BoardMessage) {
        guard let m = mirror, m.apply(message), m.complete, rendererError == nil else { return }
        if !fullMirror {
            fullMirror = true
            refreshMirror()
        }
        // Lines arrive in bursts (TEXT, CLIPTEXT, STATE): draw once when the burst is applied.
        guard !mirrorFramePending else { return }
        mirrorFramePending = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.mirrorFramePending = false
            guard let m = self.mirror, self.fullMirror, let px = NativeUi.render(m.model, m.state) else { return }
            self.mirrorImage = NativeUi.image(px, width: m.model.size.width, height: m.model.size.height)
        }
    }

    private func resetMirror() {
        guard mirror != nil || fullMirror else { return }
        mirror = nil
        fullMirror = false
        mirrorImage = nil
    }

    // MARK: firmware

    private func updateCandidate() -> BundledFirmware? {
        if state.device?.kind == .esp32c3 { return nil }               // flashed with PlatformIO, not UF2
        let board = state.firmware.flatMap { $0.known ? $0.board : nil } ?? "rp2040-169"   // pre-VER boards were all the 1.69
        return BundledFirmware.for(bundled, board: board)
    }

    private func refreshUpdateOffer() {
        if busy { canUpdate = false; return }
        if !isConnected {
            if let nb = newBoard {
                if let m = selectedModel, let fw = BundledFirmware.for(bundled, board: m.board) {
                    (canUpdate, updateText) = (true, "Found an \(nb.describe()). Install Touch Deck \(fw.version) for the \(m.name)?")
                } else {
                    (canUpdate, updateText) = (false, "Found an \(nb.describe()), but this app has no firmware for it.")
                }
            } else {
                (canUpdate, updateText) = (false, "")
            }
        } else if state.device?.kind == .esp32c3 {
            (canUpdate, updateText) = (false, "ESP32-C3 firmware is updated with PlatformIO.")
        } else if let fw = updateCandidate() {
            canUpdate = true
            updateText = fw.isNewer(than: state.firmware) ? "Firmware \(fw.version) is available." : "Up to date (bundled \(fw.version))."
        } else {
            (canUpdate, updateText) = (false, "")
        }
    }

    var updateIsUpgrade: Bool { updateCandidate()?.isNewer(than: state.firmware) ?? false }

    /// Every 2 s while no Touch Deck is connected: a Raspberry Pi board on its factory firmware or
    /// in its bootloader (by USB ID).
    private func checkNewBoards() {
        if busy || isConnected {
            if newBoard != nil { newBoard = nil; refreshUpdateOffer() }
            return
        }
        guard !scanningNewBoards else { return }
        scanningNewBoards = true
        Task.detached {
            let found = NewBoards.scan()
            await MainActor.run {
                self.scanningNewBoards = false
                guard !self.busy, !self.isConnected else { return }
                let board = found.first
                if board == self.newBoard { return }
                self.newBoard = board
                self.newBoardModels = board.map { BoardModels.for($0.chip) } ?? []
                self.selectedModel = self.newBoardModels.first { BundledFirmware.for(self.bundled, board: $0.board) != nil } ?? self.newBoardModels.first
                if let board { self.addLog("Found an \(board.describe())") }
                self.refreshUpdateOffer()
            }
        }
    }

    func updateFirmware() async {
        guard !busy else { return }
        if !isConnected {
            guard let nb = newBoard, let model = selectedModel, let fw = BundledFirmware.for(bundled, board: model.board) else { return }
            // A stock program reboots at 1200 baud; a board already in its bootloader needs nothing.
            let reboot: () -> Void = nb.state == .stockFirmware && nb.port != nil ? { _ = NewBoards.rebootToBootloader(nb.port!) } : {}
            _ = await flash(firmwareDir.appendingPathComponent(fw.file), label: "\(fw.version) (\(fw.file)) on a new \(model.name)",
                            model: model, enterBootloader: reboot)
            return
        }
        guard let fw = updateCandidate(), let m = BoardModels.find(fw.board) else { return }
        _ = await flash(firmwareDir.appendingPathComponent(fw.file), label: "\(fw.version) (\(fw.file))", model: m)
    }

    /// Installs a UF2 (bundled, or downloaded and verified) for `model`: chip and model are checked
    /// before the board is touched.
    private func flash(_ uf2: URL, label: String, model: BoardModel, enterBootloader: (() -> Void)? = nil) async -> Bool {
        guard !busy else { return false }
        busy = true
        manager.requiredBoard = model.board       // another RP board (also CAFE:4011) must not take the session
        let old = manager.session
        let manager = self.manager
        addLog("Installing firmware \(label)")
        let steps = UpdateSteps(
            enterBootloader: enterBootloader ?? { old?.requestBootloader() },
            findBootDrive: { Uf2.findBootDrive(Uf2.mountedVolumes(), chip: model.chip) },
            copyImage: { try Uf2.copy($0, toDrive: $1) },
            // Only a new session counts: the old one may not have noticed the reboot yet.
            readRunningFirmware: { manager.session.flatMap { $0 !== old ? $0.firmware : nil } },
            bootloaderChip: {
                if Uf2.findBootDrive(Uf2.mountedVolumes(), chip: .rp2350) != nil { return .rp2350 }
                if Uf2.findBootDrive(Uf2.mountedVolumes(), chip: .rp2040) != nil { return .rp2040 }
                return nil
            })
        let result = await FirmwareUpdater.install(uf2, model: model, steps: steps) { [weak self] m in
            Task { @MainActor in self?.addLog(m); self?.updateText = m }
        }
        addLog(result.message)
        Notifier.post(result.ok ? "Firmware updated" : "Firmware update failed", result.message)
        manager.requiredBoard = nil
        busy = false
        return result.ok
    }

    // MARK: updates

    private func dailyUpdateCheck() async {
        guard settings.checkForUpdates, !checkingUpdates else { return }
        if let last = settings.lastUpdateCheck, Date().timeIntervalSince(last) < 24 * 3600, lastChoice != nil { return }
        _ = await checkForUpdates(manual: false)
    }

    @discardableResult
    func checkForUpdates(manual: Bool) async -> UpdateCheckOutcome {
        checkingUpdates = true
        defer { checkingUpdates = false }
        let device = isConnected ? state.firmware : nil
        let outcome = await updates.check(currentApp: SemVer(Self.appVersion) ?? SemVer(0, 0, 0), device: device)
        update { $0.lastUpdateCheck = Date() }
        lastChoice = outcome.choice
        if let e = outcome.error {
            appUpdateText = e
            canInstallApp = false
            canInstallFirmware = false
        } else if outcome.nothingPublished {
            appUpdateText = "No updates published yet"
        } else if let c = outcome.choice {
            appUpdateText = c.app.map { "Version \($0.version) is available" } ?? "Up to date"
            canInstallApp = c.app != nil
            if state.device?.kind == .esp32c3 { firmwareUpdateText = "This board is updated with PlatformIO" }
            else { firmwareUpdateText = c.firmware.map { "Firmware \($0.version) is available" } ?? (isConnected ? "Up to date" : "") }
            canInstallFirmware = c.firmware != nil && state.device?.kind != .esp32c3
            if !manual, let app = c.app { Notifier.post("Touch Deck update", "Version \(app.version) is available in Settings.") }
        }
        return outcome
    }

    func installAppUpdate() async {
        guard let p = lastChoice?.app else { return }
        do {
            appUpdateText = "Downloading \(p.version)..."
            let pkg = try await updates.downloadApp(p, progress: nil)
            addLog("Update \(p.version) verified; opening the installer")
            UpdateService.launchInstaller(pkg)
            NSApp.terminate(nil)
        } catch {
            appUpdateText = "\(error)"
        }
    }

    func installFirmwareUpdate() async {
        guard let p = lastChoice?.firmware, let model = BoardModels.find(p.board) else { return }
        do {
            firmwareUpdateText = "Downloading \(p.version)..."
            let uf2 = try await updates.downloadFirmware(p, progress: nil)
            if await flash(uf2, label: "\(p.version) (downloaded)", model: model) { canInstallFirmware = false }
        } catch {
            firmwareUpdateText = "\(error)"
        }
    }

    // MARK: settings

    var dryRun: Bool {
        get { settings.dryRun }
        set {
            sink.dryRun = newValue
            update { $0.dryRun = newValue }
            addLog(newValue ? "Dry run on: PC-mode keys are logged, not typed" : "Dry run off")
        }
    }

    var diagnosticsEnabled: Bool {
        get { settings.diagnostics }
        set {
            manager.session?.diagnosticsEnabled = newValue
            update { $0.diagnostics = newValue }
            if !newValue { diagnostics = [] }
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

    func openAccessibilitySettings() {
        AccessibilityPermission.request()
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    private func update(_ change: (inout AppSettings) -> Void) {
        change(&settings)
        do { try settings.save() } catch { ErrorLog.append("settings: \(error)") }
        objectWillChange.send()
    }

    func addLog(_ text: String) {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        logLines.append("\(f.string(from: Date()))  \(text)")
        if logLines.count > Self.logLimit { logLines.removeFirst(logLines.count - Self.logLimit) }
    }
}
