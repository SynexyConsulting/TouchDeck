import SwiftUI
import TouchDeckCore

/// The menu under the menu bar icon (the Windows tray menu).
struct MenuContent: View {
    @EnvironmentObject private var app: AppController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(app.isConnected ? "\(app.boardName): \(app.statusText)" : app.statusText)
        if app.isConnected && !app.jigStatus.isEmpty {
            Text("Jiggler: \(app.jigStatus)")
        }
        Divider()
        Button("Open Touch Deck") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Send Selection to Board") { app.sendSelection() }
            .keyboardShortcut("c", modifiers: [.control, .option])
            .disabled(!app.isConnected)
        Button(app.jigOn ? "Turn Jiggler Off" : "Turn Jiggler On") { app.toggleJiggler() }
            .disabled(!app.isConnected)
        Divider()
        SettingsLink { Text("Settings...") }
            .keyboardShortcut(",")
        Button("Quit Touch Deck") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
