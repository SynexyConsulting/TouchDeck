import SwiftUI
import TouchDeckCore

/// Touch Deck for macOS: a menu bar app (LSUIElement, no Dock icon) with a monochrome
/// template icon, the Mac version of the Windows tray app.
@main
struct TouchDeckApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var app = AppDelegate.controller

    var body: some Scene {
        MenuBarExtra {
            MenuContent()
                .environmentObject(app)
        } label: {
            // A template image: macOS draws it black or white to suit the menu bar.
            Image(app.health == .bad ? "MenuBarIconBad" : "MenuBarIcon")
        }
        .menuBarExtraStyle(.menu)

        Window("Touch Deck", id: "main") {
            MainView()
                .environmentObject(app)
                .preferredColorScheme(.dark)
        }
        .windowResizability(.contentSize)

        Settings {
            SettingsView()
                .environmentObject(app)
                .preferredColorScheme(.dark)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// One controller for the app's lifetime (SwiftUI may build `TouchDeckApp` more than once).
    @MainActor static let controller = AppController()

    func applicationDidFinishLaunching(_ notification: Notification) {
        Notifier.requestPermission()
        MainActor.assumeIsolated { Self.controller.start() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Ends the session, which releases any key or button the board held.
        MainActor.assumeIsolated { Self.controller.stop() }
    }
}
