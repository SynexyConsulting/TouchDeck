import SwiftUI
import TouchDeckCore

/// Settings (⌘,): versions, startup, updates, the board's Jiggler settings and advanced
/// options, in the same order and wording as the Windows Settings window.
struct SettingsView: View {
    var body: some View {
        ScrollView { SettingsContent() }
            .frame(width: 400, height: 640)
            .background(Theme.bg)
    }
}

/// The settings themselves (also rendered on their own by --smoke: a ScrollView doesn't render offscreen).
struct SettingsContent: View {
    @EnvironmentObject private var app: AppController

    var body: some View { SettingsBody(board: app.selected) }
}

/// The board parts (its firmware, its Jiggler settings) are the selected board's.
private struct SettingsBody: View {
    @EnvironmentObject private var app: AppController
    @ObservedObject var board: BoardController

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            versions
            CardTitle(text: "Startup")
            Toggle("Launch at login", isOn: Binding(get: { app.launchAtLogin }, set: { app.launchAtLogin = $0 }))
                .toggleStyle(PillSwitchStyle())
            if app.launchAtLoginNeedsApproval {
                Text("Waiting for approval in System Settings > General > Login Items.")
                    .font(.caption).foregroundStyle(Theme.accent)
            }
            updatesSection
            jigglerSection
            advancedSection
        }
        .padding(20)
        .frame(width: 400, alignment: .topLeading)
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
    }

    private var versions: some View {
        Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 4) {
            GridRow { Text("App Version"); Text(AppController.appVersion).font(.system(.body, design: .monospaced)) }
            GridRow { Text("Device Firmware"); Text(board.firmwareVersion.isEmpty ? "—" : board.firmwareVersion).font(.system(.body, design: .monospaced)) }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.inner))
    }

    private var updatesSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            CardTitle(text: "Updates")
            Toggle("Check for updates automatically",
                   isOn: Binding(get: { app.checkForUpdatesAutomatically }, set: { app.checkForUpdatesAutomatically = $0 }))
                .toggleStyle(PillSwitchStyle())
            Text("The app and the board's firmware are checked separately.").font(.caption).foregroundStyle(Theme.faint)
            Button(app.checkingUpdates ? "Checking..." : "Check for updates now") { Task { await app.checkForUpdates(manual: true) } }
                .buttonStyle(PillButtonStyle(primary: true))
                .disabled(app.checkingUpdates)
            HStack {
                (Text("App: ").foregroundStyle(Theme.text) + Text(app.appUpdateText).foregroundStyle(Theme.dim)).font(.callout)
                Spacer()
                if app.canInstallApp { Button("Install") { Task { await app.installAppUpdate() } }.buttonStyle(PillButtonStyle(primary: true)) }
            }
            HStack {
                (Text("Firmware: ").foregroundStyle(Theme.text) + Text(board.firmwareUpdateText).foregroundStyle(Theme.dim)).font(.callout)
                Spacer()
                if board.canInstallFirmware { Button("Install") { Task { await board.installFirmwareUpdate() } }.buttonStyle(PillButtonStyle(primary: true)) }
            }
        }
    }

    /// The board's "Jiggler menu" panel, edited from here (firmware 1.8.0+). The controls show
    /// what the board reports; a change is sent and comes back in STATE.
    private var jigglerSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            CardTitle(text: "Jiggler")
            Text(board.jigConfig == nil ? (board.isConnected ? "This board's firmware has no Jiggler settings (1.8.0 or later)." : "Connect a board to change its Jiggler settings.")
                                      : "Saved on the board. Also on the board: the cog on its Jiggler page.")
                .font(.caption).foregroundStyle(Theme.faint)
            let c = board.jigConfig ?? JigConfig(menuOn: false, f15: false, openS: 0, pauseS: 0)
            Group {
                Toggle("Right-click context menu", isOn: Binding(get: { c.menuOn }, set: { on in board.setJigConfig(with(c) { $0.menuOn = on }) }))
                    .toggleStyle(PillSwitchStyle())
                HStack {
                    Text("Key")
                    Spacer()
                    ChoiceSwitch(left: "Esc", right: "F15", isRight: c.f15) { f15 in board.setJigConfig(with(c) { $0.f15 = f15 }) }
                }
                stepper("Menu open", value: c.openS) { $0.openS = $1 }
                    .disabled(!c.menuOn)
                stepper("Pause before next letter", value: c.pauseS) { $0.pauseS = $1 }
            }
            .disabled(board.jigConfig == nil)
        }
    }

    private func stepper(_ title: String, value: Int, set: @escaping (inout JigConfig, Int) -> Void) -> some View {
        HStack {
            Text(title)
            Spacer()
            Button("-") { if value > 0 { send(set, value - 1) } }.buttonStyle(PillButtonStyle())
            Text("\(value) s").frame(width: 52).font(.system(.body, design: .monospaced))
            Button("+") { if value < JigConfig.maxSeconds { send(set, value + 1) } }.buttonStyle(PillButtonStyle())
        }
    }

    private func send(_ set: (inout JigConfig, Int) -> Void, _ v: Int) {
        guard var c = board.jigConfig else { return }
        set(&c, v)
        board.setJigConfig(c)
    }

    private func with(_ c: JigConfig, _ change: (inout JigConfig) -> Void) -> JigConfig {
        var copy = c
        change(&copy)
        return copy
    }

    private var advancedSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            CardTitle(text: "Advanced")
            Toggle("Dry run (log keys, don't type)", isOn: Binding(get: { app.dryRun }, set: { app.dryRun = $0 }))
                .toggleStyle(PillSwitchStyle())
            Toggle("Board diagnostics", isOn: Binding(get: { app.diagnosticsEnabled }, set: { app.diagnosticsEnabled = $0 }))
                .toggleStyle(PillSwitchStyle())
            HStack {
                Text("Accessibility")
                Spacer()
                if app.accessibilityTrusted {
                    Text("Allowed").foregroundStyle(Theme.ok)
                } else {
                    Button("Allow...") { app.openAccessibilitySettings() }.buttonStyle(PillButtonStyle(primary: true))
                }
            }
            Text("Needed to read the selected text for COPY and to type for an ESP32-C3 in PC mode.")
                .font(.caption).foregroundStyle(Theme.faint)
            (Text("⌃⌥C").foregroundStyle(Theme.accent).font(.system(.callout, design: .monospaced)) +
             Text(" sends the selected text (or the clipboard) to the board, like tapping COPY.").foregroundStyle(Theme.dim))
                .font(.callout)
                .padding(.top, 6)
        }
    }
}
