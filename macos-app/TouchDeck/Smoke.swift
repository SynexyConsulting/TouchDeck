import SwiftUI
import AppKit
import TouchDeckCore

/// `--smoke DIR` (as on Windows): wait for a board (up to 10 s), then write DIR/smoke.png (the
/// window), DIR/mirror.png (the device view at 1:1, firmware 1.7.0+), DIR/settings.png and
/// DIR/smoke.txt (what the app detected), and quit. `--smoke-steps` also drives the board and saves
/// the device view after each step: mirror-jig.png, mirror-anim1/2.png (ANIM 1: the dot moves, no
/// HID) and mirror-clip.png (a clip sent from the app), ending on the clipboard page.
/// `--smoke-check-updates` also checks the official feed and adds the outcome to smoke.txt.
@MainActor
enum Smoke {
    static func runIfRequested(_ app: AppController) {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--smoke"), i + 1 < args.count else { return }
        let dir = URL(fileURLWithPath: args[i + 1])
        let steps = args.contains("--smoke-steps"), updates = args.contains("--smoke-check-updates")
        Task { @MainActor in
            await run(app, dir: dir, steps: steps, updates: updates)
            NSApp.terminate(nil)
        }
    }

    private static func run(_ app: AppController, dir: URL, steps: Bool, updates: Bool) async {
        AppDelegate.showMainWindow()
        let deadline = Date().addingTimeInterval(10)
        while !app.anyConnected && Date() < deadline { try? await Task.sleep(for: .milliseconds(200)) }
        try? await Task.sleep(for: .milliseconds(2500))      // one heartbeat: the first diagnostics arrive
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if steps { await runSteps(app, dir) }
        let windowSaved = saveWindow(dir.appendingPathComponent("smoke.png"))
        let mirrorSaved = saveMirror(app, dir.appendingPathComponent("mirror.png"))
        let settings = ImageRenderer(content: SettingsContent().environmentObject(app).font(Theme.ui(14))
            .environment(\.colorScheme, .dark))
        settings.scale = 2
        if let img = settings.cgImage { writePNG(img, dir.appendingPathComponent("settings.png")) }
        var updateLines: [String] = []
        if updates {
            let o = await app.checkForUpdates(manual: true)
            updateLines = ["update_error=\(o.error ?? "")", "update_nothing=\(o.nothingPublished)",
                           "offer_app=\(o.choice?.app.map { "\($0.version)" } ?? "")",
                           "offer_fw=\(o.choice?.firmware.map { "\($0.board) \($0.version)" } ?? "")",
                           "app_text=\(app.appUpdateText)", "fw_text=\(app.selected.firmwareUpdateText)"]
        }
        let lines = [
            "app=\(AppController.appVersion)",
            "boards=\(app.boards.count)",
            "board_tabs=\(app.boards.map(\.label).joined(separator: ", "))",
            "status=\(app.selected.state.status)",
            "board=\(app.selected.state.firmware?.board ?? "")",
            "port=\(app.selected.port)",
            "firmware=\(app.selected.state.firmware?.version ?? "")",
            "mirror=\(app.selected.mirrorAvailable)",
            "fullmirror=\(app.selected.fullMirror)",
            "mirrorpng=\(mirrorSaved)",
            "windowpng=\(windowSaved)",
            "jig=\(app.selected.jigOn)",
            "letter=\(app.selected.jigLetter)",
            "boardclip=\(app.selected.boardClipText)",
            "bundled=\(app.bundledSummary)",
            "newboard=\(app.selected.newBoard?.describe() ?? "")",
            "offer=\(app.selected.updateText)",
            "accessibility=\(app.accessibilityTrusted)",
            "exe=\(Bundle.main.executablePath ?? "")",
        ] + updateLines
        try? (lines.joined(separator: "\n") + "\n").write(to: dir.appendingPathComponent("smoke.txt"), atomically: true, encoding: .utf8)
    }

    private static func runSteps(_ app: AppController, _ dir: URL) async {
        let clipPage = app.selected.mirrorModel.clipPage
        func go(_ page: Int) async {
            for _ in 0..<4 { app.selected.swipe(left: false) }
            for _ in 0..<page { app.selected.swipe(left: true) }
            try? await Task.sleep(for: .milliseconds(1500))
        }
        await go(clipPage + 1)
        _ = saveMirror(app, dir.appendingPathComponent("mirror-jig.png"))
        app.selected.animate(true)
        try? await Task.sleep(for: .milliseconds(800))
        _ = saveMirror(app, dir.appendingPathComponent("mirror-anim1.png"))
        try? await Task.sleep(for: .milliseconds(400))
        _ = saveMirror(app, dir.appendingPathComponent("mirror-anim2.png"))
        app.selected.animate(false)
        await go(clipPage)
        app.selected.sendText("Smoke test: sent from the app,\nshown by the board and its mirror.")
        try? await Task.sleep(for: .milliseconds(3300))       // the board's "Copied" message lasts 2.5 s
        _ = saveMirror(app, dir.appendingPathComponent("mirror-clip.png"))
    }

    /// The main window's content as the screen shows it (AppKit views included).
    private static func saveWindow(_ url: URL) -> Bool {
        guard let view = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true })?.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return false }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]) else { return false }
        return (try? data.write(to: url)) != nil
    }

    private static func saveMirror(_ app: AppController, _ url: URL) -> Bool {
        guard app.selected.fullMirror, let img = app.selected.mirrorImage else { return false }
        return writePNG(img, url)
    }

    @discardableResult
    private static func writePNG(_ img: CGImage, _ url: URL) -> Bool {
        guard let data = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) else { return false }
        return (try? data.write(to: url)) != nil
    }
}
