import SwiftUI
import UserNotifications
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
            MenuBarLabel(health: app.health)
        }
        .menuBarExtraStyle(.menu)

        Window("Touch Deck", id: "main") {
            MainView()
                .environmentObject(app)
                .font(Theme.ui(14))
                .preferredColorScheme(.dark)
        }
        .defaultSize(width: 1000, height: 880)

        Settings {
            SettingsView()
                .environmentObject(app)
                .font(Theme.ui(14))
                .preferredColorScheme(.dark)
        }
    }
}

/// The menu bar icon. It is always on screen, so it also hands the app delegate a way to open the
/// main window (a reopen, a second launch, a clicked notification).
struct MenuBarLabel: View {
    var health: Health
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        // A template image: macOS draws it black or white to suit the menu bar.
        Image(health == .bad ? "MenuBarIconBad" : health == .ok ? "MenuBarIcon" : "MenuBarIconIdle")
            .onAppear {
                AppDelegate.openMain = {
                    openWindow(id: "main")
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    /// One controller for the app's lifetime (SwiftUI may build `TouchDeckApp` more than once).
    @MainActor static let controller = AppController()
    @MainActor static var openMain: (() -> Void)?

    /// Asks the running copy to come forward or to quit (`--quit`, as on Windows: frees the port
    /// for flash.py and the board tests).
    private static let showRequest = Notification.Name("com.synexyconsulting.touchdeck.show")
    private static let quitRequest = Notification.Name("com.synexyconsulting.touchdeck.quit")

    func applicationWillFinishLaunching(_ notification: Notification) {
        let args = CommandLine.arguments
        func others() -> [NSRunningApplication] {
            NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
                .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier && !$0.isTerminated }
        }
        if args.contains("--quit") {
            for app in others() { app.terminate() }
            DistributedNotificationCenter.default().postNotificationName(Self.quitRequest, object: nil, deliverImmediately: true)
            exit(0)
        }
        // One copy owns the serial port; a second launch brings the first forward.
        // A copy that is still quitting (it joins its session thread first) gets half a second.
        if !others().isEmpty { Thread.sleep(forTimeInterval: 0.5) }
        if !others().isEmpty {
            DistributedNotificationCenter.default().postNotificationName(Self.showRequest, object: nil, deliverImmediately: true)
            exit(0)
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Theme.registerFonts()
        ErrorLog.installExceptionHandler()
        UNUserNotificationCenter.current().delegate = self
        Notifier.requestPermission()
        let center = DistributedNotificationCenter.default()
        center.addObserver(forName: Self.showRequest, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { Self.showMainWindow() }
        }
        center.addObserver(forName: Self.quitRequest, object: nil, queue: .main) { _ in
            NSApp.terminate(nil)
        }
        MainActor.assumeIsolated {
            Self.controller.start()
            Smoke.runIfRequested(Self.controller)
        }
    }

    /// Opening the app again (Finder, Spotlight, `open`) shows the window, as the tray's Open does.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        MainActor.assumeIsolated { Self.showMainWindow() }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Ends the session, which releases any key or button the board held.
        MainActor.assumeIsolated { Self.controller.stop() }
    }

    @MainActor static func showMainWindow() {
        if let open = openMain { open() }
        else if let w = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true }) {
            w.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    // MARK: notifications (the Windows tray balloons)

    /// Show them even while Touch Deck is the active app.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    /// Clicking one opens the window, as clicking a balloon does on Windows.
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        DispatchQueue.main.async { MainActor.assumeIsolated { Self.showMainWindow() } }
        completionHandler()
    }
}
