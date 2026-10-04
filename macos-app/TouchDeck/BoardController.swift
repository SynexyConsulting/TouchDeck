import Foundation
import SwiftUI
import TouchDeckCore

/// One board's tab: its link, its mirror, its Remote actions, its firmware offers. A Touch Deck port
/// (a `DeviceManager` slot), a board without Touch Deck (a `NewBoard`), or the placeholder shown while
/// nothing is attached. Port of BoardController.cs. Views observe it directly (`@ObservedObject`):
/// the app's `@Published boards` array doesn't pass on changes inside its elements.
@MainActor
final class BoardController: ObservableObject, Identifiable {
    private unowned let app: AppController

    /// The port, or "boot:RP2040" for a board in its bootloader (a volume has no port).
    let key: String
    nonisolated var id: String { key }
    var isPlaceholder: Bool { key.isEmpty }
    /// Set when the board went away; late events for it are ignored.
    var detached = false

    init(app: AppController, key: String, port: String?) {
        self.app = app
        self.key = key
        self.port = port ?? ""
        refreshTexts()
    }

    // MARK: link

    @Published private(set) var state = LinkState.searching
    private(set) var session: DeviceSession?
    @Published private(set) var health = Health.idle
    @Published private(set) var statusText = ""
    @Published private(set) var boardName = "No board"
    @Published private(set) var port = ""
    @Published private(set) var firmwareVersion = ""
    @Published private(set) var firmwareBuild = ""
    @Published private(set) var isConnected = false
    /// The tab text: "RP2350 1.28 · usbmodem1101".
    @Published private(set) var label = ""
    @Published var isSelected = false
    @Published private(set) var diagnostics: [(String, String)] = []

    /// What this board's log lines are tagged with: its port, or the label of a board without one.
    var logTag: String { port.isEmpty ? label : BoardLabels.shortPort(port) }

    func log(_ text: String) { app.addLog(text, board: logTag) }

    /// A new state for this port from the manager.
    func applyState(_ s: LinkState) {
        let was = state
        state = s
        isConnected = s.status == .connected
        if isConnected && newBoard != nil { setNewBoard(nil) }      // its port now runs Touch Deck
        if !isConnected {
            session = nil
            jigConfig = nil
            diagnostics = []
            resetMirror()
        }
        if let p = s.device?.port { port = p }
        refreshTexts()
        refreshMirror()
        refreshUpdateOffer()
        refreshFeedOffer()
        app.refreshHealth()

        if s.status == was.status && s.device == was.device { return }
        if isConnected {
            log("Connected: \(boardName), firmware \(firmwareVersion)")
            app.notifyBoard(self, "Touch Deck connected", "\(boardName) on \(BoardLabels.shortPort(port)), firmware \(s.firmware?.version ?? "?")")
        } else if was.status == .connected {
            log("Disconnected")
            if !busy { app.notifyBoard(self, "Touch Deck disconnected", "Plug it back in; the app reconnects by itself.") }
        } else if s.status != .searching {
            log(statusText)
        }
    }

    private func refreshTexts() {
        let s = state
        let kind = s.device?.kind ?? .rp2040
        if let nb = newBoard {
            boardName = BoardLabels.model(nb)
            label = BoardLabels.tab(BoardLabels.model(nb), port: nb.port)
        } else {
            boardName = s.device.map { BoardKinds.displayName($0.kind, board: s.firmware?.board) } ?? "No board"
            label = BoardLabels.tab(BoardLabels.model(kind, board: s.firmware?.board), port: port.isEmpty ? nil : port)
        }
        firmwareVersion = s.firmware.map { $0.known ? "\($0.version)  (\($0.board))" : "unknown (older than 1.5.0)" } ?? ""
        firmwareBuild = s.firmware.flatMap { $0.known ? $0.build : nil } ?? ""
        let shown = BoardLabels.shortPort(port)
        if newBoard != nil {
            (health, statusText) = (.idle, "No Touch Deck firmware on this board yet.")
        } else {
            switch s.status {
            case .connected: (health, statusText) = (.ok, "Connected")
            case .portBusy: (health, statusText) = (.bad, "\(shown) is in use by another program.")
            case .notResponding: (health, statusText) = (.bad, "\(shown) doesn't answer. Is it running Touch Deck firmware?")
            case .searching: (health, statusText) = (.idle, busy ? "Installing firmware..." : isPlaceholder ? "Looking for a Touch Deck..." : "Reconnecting...")
            }
        }
    }

    /// The session came up: hooked by the app before it starts reading.
    func attach(_ s: DeviceSession, mirror: MirrorState) {
        session = s
        s.diagnosticsEnabled = app.diagnosticsEnabled
        startMirror(mirror)
    }

    func clearDiagnostics() { diagnostics = [] }

    func showDiagnostics(_ d: [String: String]) {
        guard app.diagnosticsEnabled, isConnected else { return }
        diagnostics = d.sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    // MARK: actions

    func sendText(_ text: String) {
        if let s = session, !text.isEmpty { s.sendText(text) }
    }

    func swipe(left: Bool) { session?.swipe(left: left) }
    func tap(x: Int, y: Int) { session?.tap(x: x, y: y) }
    func toggleJiggler() { session?.setJiggler(!jigOn) }
    func cycleScale() { session?.setScale((jigScale + 1) % 3) }
    func clearBoardClip() { session?.clearClip() }
    func animate(_ on: Bool) { session?.animate(on) }
    func pressButton(long: Bool) { session?.pressButton(long: long) }

    /// Restarts the board as its UF2 drive (Bootloader button, after the user confirms).
    func rebootToBootloader() {
        guard let s = session else { return }
        log("Rebooting the board into its bootloader")
        s.requestBootloader()
    }

    func sendInfo(_ text: String) -> String { ClipMessage.sendInfo(text, connected: isConnected) }

    // MARK: board state (firmware 1.6.0+)

    @Published private(set) var jigOn = false
    @Published private(set) var jigLetter: Character = "O"
    @Published private(set) var jigStatus = ""
    @Published private(set) var jigScaleText = "1.0X"
    @Published private(set) var boardClipText = ""
    @Published private(set) var canClearBoardClip = false
    @Published private(set) var jigConfig: JigConfig?
    @Published private(set) var mirrorFallbackText = ""
    /// True when the board streams STATE (the jiggler card is live).
    @Published private(set) var mirrorAvailable = false
    private var jigScale = 0

    func applyBoardState(_ st: StateReport) {
        jigOn = st.jigOn
        jigLetter = st.letter
        jigScale = st.scale
        jigScaleText = JigView.scaleText(st.scale)
        jigStatus = JigView.status(st)
        boardClipText = JigView.clipText(st)
        canClearBoardClip = JigView.canClear(st)
        jigConfig = JigView.config(st)
        mirrorAvailable = true               // the fallback text comes from refreshMirror (once a second)
    }

    /// Once a second, and on link changes: is the mirror known, and what note goes under it.
    func refreshMirror() {
        guard isConnected, let supported = session?.mirrorSupported else {
            if !isConnected {
                // A board must not keep its last jiggler state or screen.
                resetMirror()
                mirrorAvailable = false
                mirrorFallbackText = ""
                jigOn = false
                jigLetter = "O"
                jigStatus = ""
                boardClipText = ""
                canClearBoardClip = false
            }
            return
        }
        mirrorAvailable = supported
        if fullMirror { mirrorFallbackText = "" }
        else if !supported { mirrorFallbackText = "This board's firmware is older than 1.6.0. Use Install firmware below to see and control its jiggler here." }
        else if rendererError != nil { mirrorFallbackText = "The device view couldn't load, so only the jiggler is shown." }
        else if let v = state.firmware?.semVer, v < SemVer(1, 7, 0) { mirrorFallbackText = "Install firmware 1.7.0 or later (below) to see and use the whole device here." }
        else { mirrorFallbackText = "" }
    }

    /// Jiggler settings: send the change; the controls follow what the board reports back.
    func setJigConfig(_ c: JigConfig) {
        guard let s = session, jigConfig != nil else { return }
        var n = c
        n.openS = min(max(c.openS, 0), JigConfig.maxSeconds)
        n.pauseS = min(max(c.pauseS, 0), JigConfig.maxSeconds)
        s.setJigConfig(menuOn: n.menuOn, f15: n.f15, openS: n.openS, pauseS: n.pauseS)
        log(n.describe())
    }

    // MARK: device mirror (firmware 1.7.0+)

    @Published private(set) var fullMirror = false
    @Published private(set) var mirrorModel = UiModel.rp2040Rect
    @Published private(set) var mirrorImage: CGImage?
    private var mirror: MirrorState?
    private var mirrorFramePending = false
    private(set) var rendererError: String?

    private func startMirror(_ m: MirrorState) {
        mirror = m
        mirrorModel = m.model
        fullMirror = false
        mirrorImage = nil
        rendererError = NativeUi.unavailableReason(m.model)
        if let e = rendererError { log("Device view unavailable: \(e)") }
    }

    func applyMirror(_ m: MirrorState, _ message: BoardMessage) {
        guard m === mirror, m.apply(message), m.complete, rendererError == nil else { return }
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
            guard !self.detached, let m = self.mirror, self.fullMirror, let px = NativeUi.render(m.model, m.state) else { return }
            self.mirrorImage = NativeUi.image(px, width: m.model.size.width, height: m.model.size.height)
        }
    }

    private func resetMirror() {
        guard mirror != nil || fullMirror else { return }
        mirror = nil
        fullMirror = false
        mirrorImage = nil
    }

    // MARK: new boards (no Touch Deck firmware yet)

    @Published private(set) var newBoard: NewBoard?
    @Published private(set) var newBoardModels: [BoardModel] = []
    @Published var selectedModel: BoardModel? { didSet { refreshUpdateOffer() } }

    func setNewBoard(_ board: NewBoard?) {
        if board == newBoard { return }
        let first = newBoard == nil && board != nil
        newBoard = board
        newBoardModels = board.map { BoardModels.for($0.chip) } ?? []
        selectedModel = newBoardModels.first { BundledFirmware.for(app.bundled, board: $0.board) != nil } ?? newBoardModels.first
        if let board { mirrorModel = board.chip == .rp2350 ? .rp2350Round : .rp2040Rect }   // the empty screen in the board's shape
        refreshTexts()
        refreshUpdateOffer()
        if first, let board { log("Found an \(board.describe())") }
    }

    // MARK: bundled firmware: install, update

    @Published private(set) var updateText = ""
    @Published private(set) var canUpdate = false
    /// This board is being installed.
    @Published private(set) var busy = false { didSet { refreshTexts(); refreshUpdateOffer() } }

    private func updateCandidate() -> BundledFirmware? {
        if state.device?.kind == .esp32c3 { return nil }               // flashed with PlatformIO, not UF2
        let board = state.firmware.flatMap { $0.known ? $0.board : nil } ?? "rp2040-169"   // pre-VER boards were all the 1.69
        return BundledFirmware.for(app.bundled, board: board)
    }

    func refreshUpdateOffer() {
        if busy { canUpdate = false; return }
        if let other = app.installing, other !== self { canUpdate = false; return }   // one install at a time
        if !isConnected {
            if let nb = newBoard {
                if let m = selectedModel, let fw = BundledFirmware.for(app.bundled, board: m.board) {
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

    /// Install firmware (bundled): a new board gets Touch Deck, a Touch Deck gets the bundled build.
    func updateFirmware() async {
        guard !busy, app.installing == nil else { return }
        if !isConnected {
            guard let nb = newBoard, let model = selectedModel, let fw = BundledFirmware.for(app.bundled, board: model.board) else { return }
            // A stock program reboots at 1200 baud; a board already in its bootloader needs nothing.
            let reboot: () -> Void = nb.state == .stockFirmware && nb.port != nil ? { _ = NewBoards.rebootToBootloader(nb.port!) } : {}
            _ = await install(app.firmwareDir.appendingPathComponent(fw.file), label: "\(fw.version) (\(fw.file)) on a new \(model.name)",
                              model: model, enterBootloader: reboot)
            return
        }
        guard let fw = updateCandidate(), let m = BoardModels.find(fw.board) else { return }
        _ = await install(app.firmwareDir.appendingPathComponent(fw.file), label: "\(fw.version) (\(fw.file))", model: m)
    }

    private func install(_ uf2: URL, label: String, model: BoardModel, enterBootloader: (() -> Void)? = nil) async -> Bool {
        busy = true
        defer { busy = false }
        let old = session
        return await app.flash(self, uf2, label: label, model: model, enterBootloader: enterBootloader ?? { old?.requestBootloader() }) { [weak self] m in
            self?.updateText = m
        }
    }

    // MARK: firmware from the update feed

    @Published private(set) var firmwareUpdateText = ""
    @Published private(set) var canInstallFirmware = false
    private var feedFirmware: FirmwarePackage?

    /// This board's offer from the last verified feed (no network: the feed is kept).
    func refreshFeedOffer() {
        let device = isConnected ? state.firmware : nil
        let feed = app.lastFeed
        let fw = feed.flatMap { UpdateSelector.select($0, currentApp: SemVer(AppController.appVersion) ?? SemVer(0, 0, 0), device: device).firmware }
        // The app flashes the RP boards (UF2); the ESP32-C3 is updated with PlatformIO.
        feedFirmware = fw.flatMap { BoardModels.find($0.board) != nil ? $0 : nil }
        if feed == nil { firmwareUpdateText = app.feedNote }
        else if let device, device.known {
            firmwareUpdateText = BoardModels.find(device.board) == nil ? "This board is updated with PlatformIO"
                : feedFirmware.map { "\($0.version) available" } ?? "Up to date"
        } else {
            firmwareUpdateText = "Connect a board to check its firmware"
        }
        canInstallFirmware = feedFirmware != nil && device != nil && !busy && app.installing == nil
    }

    func installFirmwareUpdate() async {
        guard let p = feedFirmware, let dev = state.firmware, dev.known, dev.board == p.board,
              let model = BoardModels.find(p.board), canInstallFirmware else { return }
        canInstallFirmware = false
        firmwareUpdateText = "Downloading..."
        do {
            let uf2 = try await app.downloadFirmware(p)
            firmwareUpdateText = "Installing..."
            let ok = await install(uf2, label: "\(p.version) (downloaded, verified)", model: model)
            firmwareUpdateText = ok ? "Updated to \(p.version)" : "Install failed: see the activity log"
            canInstallFirmware = !ok
        } catch {
            firmwareUpdateText = "Update failed: \(AppController.describe(error))"
            canInstallFirmware = true
            log(firmwareUpdateText)
        }
    }
}
