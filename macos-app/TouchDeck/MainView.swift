import SwiftUI
import TouchDeckCore

/// The main window: the board itself on the left (device mirror), the link, firmware, text
/// sending and activity log on the right, as in the Windows MainWindow.
struct MainView: View {
    @EnvironmentObject private var app: AppController
    @State private var text = ""

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(spacing: 12) {
                if app.fullMirror {
                    DeviceMirrorView()
                } else {
                    JigglerCard()
                }
                if !app.mirrorFallbackText.isEmpty {
                    Text(app.mirrorFallbackText).font(.caption).foregroundStyle(Theme.dim)
                        .frame(width: 260).fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(width: 280)

            VStack(alignment: .leading, spacing: 10) {
                deviceCard
                sendCard
                logCard
            }
            .frame(minWidth: 380)
        }
        .padding(16)
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
    }

    private var deviceCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Circle().fill(app.health == .ok ? Theme.ok : app.health == .bad ? Theme.bad : Theme.faint).frame(width: 9, height: 9)
                Text(app.boardName).font(.headline)
                Spacer()
                Text(app.port).font(.system(.caption, design: .monospaced)).foregroundStyle(Theme.dim)
            }
            Text(app.statusText).foregroundStyle(Theme.dim)
            if !app.firmwareVersion.isEmpty {
                Text("Firmware \(app.firmwareVersion)").font(.system(.body, design: .monospaced))
            }
            if !app.updateText.isEmpty {
                Text(app.updateText).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
            }
            if app.newBoardModels.count > 1 {
                Picker("Model", selection: $app.selectedModel) {
                    ForEach(app.newBoardModels) { Text($0.name).tag(Optional($0)) }
                }
                .frame(maxWidth: 320)
            }
            if app.canUpdate {
                Button(app.isConnected ? (app.updateIsUpgrade ? "Install firmware" : "Reinstall firmware") : "Install Touch Deck") {
                    Task { await app.updateFirmware() }
                }
                .buttonStyle(PillButtonStyle(primary: app.updateIsUpgrade || !app.isConnected))
                .disabled(app.busy)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.surf))
    }

    private var sendCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            CardTitle(text: "Send to the board")
            TextEditor(text: $text)
                .font(.system(.body, design: .monospaced))
                .scrollContentBackground(.hidden)
                .background(Theme.inner)
                .frame(height: 70)
            HStack {
                Button("Send") { app.sendText(text) }
                    .buttonStyle(PillButtonStyle(primary: true))
                    .disabled(!app.isConnected || text.isEmpty)
                Text("⌃⌥C sends the selection (or the clipboard), like tapping COPY.")
                    .font(.caption).foregroundStyle(Theme.dim)
            }
            if !app.historyItems.isEmpty {
                CardTitle(text: "Recent (this session only)")
                ForEach(app.historyItems, id: \.self) { item in
                    HStack {
                        Text(AppController.preview(item)).lineLimit(1).font(.system(.caption, design: .monospaced))
                        Spacer()
                        Button("Send") { app.sendText(item) }.buttonStyle(.link).disabled(!app.isConnected)
                        Button("Copy") { app.copyToPasteboard(item) }.buttonStyle(.link)
                    }
                }
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.surf))
    }

    private var logCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(text: "Activity")
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(app.logLines.enumerated()), id: \.offset) { i, line in
                            Text(line).font(.system(size: 11, design: .monospaced)).foregroundStyle(Theme.dim).id(i)
                                .textSelection(.enabled)
                        }
                    }
                }
                .frame(height: 160)
                .onChange(of: app.logLines.count) { _, n in proxy.scrollTo(n - 1, anchor: .bottom) }
            }
            if !app.diagnostics.isEmpty {
                CardTitle(text: "Board diagnostics")
                Text(app.diagnostics.map { "\($0.0)=\($0.1)" }.joined(separator: "  "))
                    .font(.system(size: 11, design: .monospaced)).foregroundStyle(Theme.dim)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.surf))
    }
}

/// Firmware 1.6.x boards (no full mirror): the jiggler state and controls only.
struct JigglerCard: View {
    @EnvironmentObject private var app: AppController

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardTitle(text: "Jiggler")
            HStack {
                Text(app.jigStatus.isEmpty ? "—" : app.jigStatus).font(.title3)
                Spacer()
                Button(app.jigScaleText) { app.cycleScale() }.buttonStyle(PillButtonStyle())
            }
            Toggle("Jiggler", isOn: Binding(get: { app.jigOn }, set: { _ in app.toggleJiggler() }))
                .toggleStyle(PillSwitchStyle())
            HStack {
                Text(app.boardClipText).foregroundStyle(Theme.dim)
                Spacer()
                Button("Clear") { app.clearBoardClip() }.buttonStyle(PillButtonStyle()).disabled(!app.canClearBoardClip)
            }
        }
        .disabled(!app.isConnected)
        .padding(12)
        .frame(width: 260)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.surf))
    }
}
