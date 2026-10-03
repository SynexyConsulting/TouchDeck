import SwiftUI
import TouchDeckCore

/// The menu under the menu bar icon: the Windows tray menu (Open, Send selection, Dry run, Quit)
/// with the link status above it and the Mac's Settings item.
struct MenuContent: View {
    @EnvironmentObject private var app: AppController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if app.boards.isEmpty { Text("Looking for a Touch Deck...") }
        ForEach(app.boards) { b in BoardMenuLine(board: b) }
        Divider()
        Button("Open Touch Deck") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Send Selection to Board") { app.sendSelection() }
            .keyboardShortcut("c", modifiers: [.control, .option])
        Toggle("Dry Run", isOn: Binding(get: { app.dryRun }, set: { app.dryRun = $0 }))
        SelectedJigglerItem(board: app.selected)
        Divider()
        SettingsLink { Text("Settings...") }
            .keyboardShortcut(",")
        Button("Quit Touch Deck") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}

/// One board in the menu: its name and port, or what's wrong with it.
private struct BoardMenuLine: View {
    @ObservedObject var board: BoardController

    var body: some View {
        Text(board.isConnected ? "\(board.boardName) on \(BoardLabels.shortPort(board.port))" : "\(board.label): \(board.statusText)")
        if board.isConnected, !board.firmwareVersion.isEmpty { Text("Firmware \(board.firmwareVersion)") }
        if board.isConnected, !board.jigStatus.isEmpty { Text("Jiggler: \(board.jigStatus)") }
    }
}

/// The jiggler on/off item, for the selected board.
private struct SelectedJigglerItem: View {
    @ObservedObject var board: BoardController

    var body: some View {
        Button(board.jigOn ? "Turn Jiggler Off" : "Turn Jiggler On") { board.toggleJiggler() }
            .disabled(!board.isConnected)
    }
}
