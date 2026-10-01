import SwiftUI
import TouchDeckCore

/// The main window, laid out as the Windows MainWindow: a header with Settings; on the left the
/// device (live mirror) and its details; on the right Remote, recent clips, diagnostics and the
/// activity log.
struct MainView: View {
    @EnvironmentObject private var app: AppController
    @Environment(\.openSettings) private var openSettings
    @State private var text = ""
    @State private var confirmInstall = false
    @State private var confirmBootloader = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            HStack(alignment: .top, spacing: 16) {
                ScrollView {
                    VStack(spacing: 12) {
                        deviceCard
                        detailsCard
                    }
                }
                .frame(width: 400)
                .scrollIndicators(.never)

                VStack(spacing: 12) {
                    remoteCard
                    historyCard
                    if app.diagnosticsEnabled && app.isConnected { diagnosticsCard }
                    logCard
                }
                .frame(minWidth: 400, maxWidth: .infinity)
            }
        }
        .padding(18)
        .frame(minWidth: 840, minHeight: 780, alignment: .topLeading)
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
        .navigationTitle("Touch Deck \(AppController.appVersion)")
        .alert("Install firmware", isPresented: $confirmInstall) {
            Button("Continue") { Task { await app.updateFirmware() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The board restarts into its bootloader, the firmware is copied to it, and it reconnects. Keep it plugged in.")
        }
        .alert("Reboot to bootloader", isPresented: $confirmBootloader) {
            Button("Reboot") { app.rebootToBootloader() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The board will restart as a USB drive (RPI-RP2 or RP2350) and stop working as a Touch Deck until firmware is copied to it or it is unplugged and replugged.")
        }
    }

    // MARK: header

    private var header: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 30, height: 30)
            Text("Touch Deck").font(Theme.head(24))
            Spacer()
            Button {
                openSettings()
                NSApp.activate(ignoringOtherApps: true)
            } label: {
                Label("Settings", systemImage: "gearshape")
            }
            .buttonStyle(PillButtonStyle())
            .help("Settings: versions, startup, updates")
        }
    }

    // MARK: left: the device

    /// The live device view (firmware 1.7.0+), the jiggler card for firmware 1.6.x (whose STATE has
    /// no page), or the empty device with a note.
    private var deviceCard: some View {
        let legacy = app.isConnected && !app.fullMirror && app.mirrorAvailable
        return VStack(spacing: 10) {
            if legacy {
                JigglerCard()
            } else {
                DeviceMirrorView(placeholder: placeholder)
                if app.fullMirror {
                    Text("Click to tap · drag sideways to swipe").font(.system(size: 12)).foregroundStyle(Theme.faint)
                }
            }
            if !app.mirrorFallbackText.isEmpty {
                Text(app.mirrorFallbackText).foregroundStyle(Theme.accent)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 18)
        .frame(maxWidth: .infinity)
        .card()
    }

    /// Text over the dark panel; nil shows the frame.
    private var placeholder: String? {
        if app.fullMirror && app.mirrorImage != nil { return nil }
        if !app.isConnected { return "Connect a Touch Deck" }
        if app.fullMirror || !app.mirrorFallbackText.isEmpty { return "" }     // the note below says why
        return app.mirrorAvailable ? nil : "Waiting for the board..."
    }

    private var detailsCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Circle().fill(app.health == .ok ? Theme.ok : app.health == .bad ? Theme.bad : Theme.faint).frame(width: 12, height: 12)
                Text(app.boardName).font(Theme.head(19))
            }
            Text(app.statusText).foregroundStyle(Theme.dim).fixedSize(horizontal: false, vertical: true)
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 4) {
                GridRow { label("Port"); value(app.port) }
                GridRow { label("Firmware"); value(app.firmwareVersion) }
                GridRow { label("Built"); value(app.firmwareBuild) }
            }
            if !app.updateText.isEmpty {
                Text(app.updateText).foregroundStyle(Theme.accent).fixedSize(horizontal: false, vertical: true)
            }
            if app.newBoardModels.count > 1 {
                Picker("Model", selection: $app.selectedModel) {
                    ForEach(app.newBoardModels) { Text($0.name).tag(Optional($0)) }
                }
                .frame(maxWidth: 260)
            }
            HStack(spacing: 8) {
                Button(updateLabel) { confirmInstall = true }
                    .buttonStyle(PillButtonStyle(primary: app.canUpdate && (app.updateIsUpgrade || !app.isConnected)))
                    .disabled(!app.canUpdate || app.busy)
                Button("Bootloader") { confirmBootloader = true }
                    .buttonStyle(PillButtonStyle())
                    .disabled(!app.isConnected || app.busy)
                    .help("Reboot the board into its UF2 drive (RPI-RP2 or RP2350)")
            }
            .padding(.top, 4)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private var updateLabel: String {
        if app.isConnected && !app.updateIsUpgrade { return "Reinstall firmware" }
        if !app.isConnected && app.newBoard != nil { return "Install Touch Deck" }
        return "Install firmware"
    }

    private func label(_ s: String) -> some View { Text(s).foregroundStyle(Theme.dim) }
    private func value(_ s: String) -> some View {
        Text(s.isEmpty ? "—" : s).font(Theme.mono(13)).textSelection(.enabled)
    }

    // MARK: right: remote, clips, diagnostics, log

    private var remoteCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            CardTitle(text: "Remote", top: 0)
            HStack(spacing: 6) {
                Button("◀  Page") { app.swipe(left: false) }.buttonStyle(PillButtonStyle()).help("Previous page (swipe right)")
                Button("Page  ▶") { app.swipe(left: true) }.buttonStyle(PillButtonStyle()).help("Next page (swipe left)")
                // Every board answers BTN (the ESP32-C3 since its round watch, firmware 1.8.0).
                Button("BOOT") { app.pressButton(long: false) }.buttonStyle(PillButtonStyle())
                    .help("Press BOOT: start/pause the stopwatch, or change the jiggler size")
                Button("Hold BOOT") { app.pressButton(long: true) }.buttonStyle(PillButtonStyle())
                    .help("Hold BOOT: reset the stopwatch")
            }
            CardTitle(text: "Send to board", top: 6)
            TextEditor(text: $text)
                .font(Theme.mono(13))
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 8).fill(Theme.inner))
                .frame(height: 64)
            HStack {
                Text(app.sendInfo(text)).font(.system(size: 13)).foregroundStyle(Theme.dim).lineLimit(1).truncationMode(.tail)
                Spacer()
                Button("Send") { app.sendText(text) }
                    .buttonStyle(PillButtonStyle(primary: true))
                    .disabled(!app.isConnected || text.isEmpty)
            }
            HStack {
                Text(app.boardClipText).font(.system(size: 13)).foregroundStyle(Theme.dim)
                Spacer()
                Button { app.clearBoardClip() } label: { Label("Clear", systemImage: "trash") }
                    .buttonStyle(PillButtonStyle())
                    .disabled(!app.canClearBoardClip)
                    .help("Empty the board's clip (the trash can)")
            }
        }
        .disabled(!app.isConnected)
        .padding(16)
        .card()
    }

    private var historyCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                CardTitle(text: "Recent clips (kept in memory only)", top: 0)
                Spacer()
                Button("Clear") { app.clearHistory() }.buttonStyle(PillButtonStyle()).disabled(app.historyItems.isEmpty)
            }
            if app.historyItems.isEmpty {
                Text("Clips you send appear here.").foregroundStyle(Theme.faint)
            } else {
                ScrollView {
                    VStack(spacing: 6) {
                        ForEach(app.historyItems, id: \.self) { item in
                            HStack {
                                Text(AppController.preview(item)).font(Theme.mono(13)).lineLimit(1).help(item)
                                Spacer()
                                Button("Resend") { app.sendText(item) }.buttonStyle(PillButtonStyle()).disabled(!app.isConnected)
                                Button("Copy") { app.copyToPasteboard(item) }.buttonStyle(PillButtonStyle())
                                    .help("Put on the Mac clipboard")
                            }
                            .padding(.horizontal, 10).padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Theme.inner))
                        }
                    }
                }
            }
        }
        .padding(16)
        .frame(minHeight: app.historyItems.isEmpty ? 96 : 150, maxHeight: .infinity, alignment: .top)
        .card()
    }

    private var diagnosticsCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            CardTitle(text: "Board diagnostics", top: 0)
            FlowLayout(spacing: 6) {
                ForEach(app.diagnostics, id: \.0) { k, v in
                    (Text(k + " ").foregroundStyle(Theme.dim) + Text(v).foregroundStyle(Theme.text))
                        .font(Theme.mono(12))
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.inner))
                }
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private var logCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                CardTitle(text: "Activity", top: 0)
                Spacer()
                Button("Copy log") { app.copyLog() }.buttonStyle(PillButtonStyle()).disabled(app.logLines.isEmpty)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(app.logLines.enumerated()), id: \.offset) { i, line in
                            Text(line).font(Theme.mono(12)).foregroundStyle(Theme.dim).id(i)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    .textSelection(.enabled)
                }
                .onChange(of: app.logLines.count) { _, n in proxy.scrollTo(n - 1, anchor: .bottom) }
            }
        }
        .padding(16)
        .frame(minHeight: 140, maxHeight: .infinity, alignment: .top)
        .layoutPriority(1)
        .card()
    }
}

/// Firmware 1.6.x boards (no full mirror): the jiggler state and controls only.
struct JigglerCard: View {
    @EnvironmentObject private var app: AppController

    var body: some View {
        VStack(spacing: 10) {
            ZStack {
                Text("JIGGLER").font(.system(size: 11, weight: .bold)).foregroundStyle(Theme.dim)
                HStack {
                    Button(app.jigScaleText) { app.cycleScale() }.buttonStyle(PillButtonStyle()).help("Jiggler size: 1x, 1.5x, 2x")
                    Spacer()
                    Button(app.jigOn ? "ON" : "OFF") { app.toggleJiggler() }
                        .buttonStyle(PillButtonStyle(primary: app.jigOn))
                        .help("Turn the jiggler on or off")
                }
            }
            Text(app.jigStatus.isEmpty ? "—" : app.jigStatus).foregroundStyle(Theme.dim)
        }
        .disabled(!app.isConnected)
    }
}

extension View {
    /// The Windows "Card" style.
    func card() -> some View {
        background(RoundedRectangle(cornerRadius: 14).fill(Theme.surf))
    }
}

/// Wraps its children onto new rows, like a WPF WrapPanel.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(proposal.width ?? .infinity, subviews)
        return CGSize(width: proposal.width ?? rows.width, height: rows.height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let rows = arrange(bounds.width, subviews)
        for (i, p) in rows.points.enumerated() {
            subviews[i].place(at: CGPoint(x: bounds.minX + p.x, y: bounds.minY + p.y), proposal: .unspecified)
        }
    }

    private func arrange(_ width: CGFloat, _ subviews: Subviews) -> (points: [CGPoint], width: CGFloat, height: CGFloat) {
        var points: [CGPoint] = []
        var x: CGFloat = 0, y: CGFloat = 0, rowH: CGFloat = 0, maxW: CGFloat = 0
        for v in subviews {
            let s = v.sizeThatFits(.unspecified)
            if x > 0 && x + s.width > width { x = 0; y += rowH + spacing; rowH = 0 }
            points.append(CGPoint(x: x, y: y))
            x += s.width + spacing
            rowH = max(rowH, s.height)
            maxW = max(maxW, x - spacing)
        }
        return (points, maxW, y + rowH)
    }
}
