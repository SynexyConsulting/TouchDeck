import SwiftUI
import AppKit
import CoreText

/// The devices' palette (../esp32c3/src/ui.cpp), as the Windows app uses it.
enum Theme {
    static let bg = Color(hex: 0x07090D)
    static let surf = Color(hex: 0x141A24)
    static let surf2 = Color(hex: 0x1C2432)
    static let inner = Color(hex: 0x10151E)
    static let text = Color(hex: 0xE8ECF2)
    static let dim = Color(hex: 0x8A94A6)
    static let faint = Color(hex: 0x4A5366)
    static let accent = Color(hex: 0xF2A33A)
    static let accentTint = Color(hex: 0x2D2214)
    static let ok = Color(hex: 0x3CCB7F)
    static let bad = Color(hex: 0xE5484D)

    // The Windows app's typefaces, bundled in Fonts/ (OFL): Barlow for text and headings,
    // JetBrains Mono for values and the log. The system fonts stand in if registration failed.
    static func ui(_ size: CGFloat) -> Font { font("Barlow-Medium", size) ?? .system(size: size, weight: .medium) }
    static func head(_ size: CGFloat) -> Font { font("Barlow-SemiBold", size) ?? .system(size: size, weight: .semibold) }
    static func mono(_ size: CGFloat) -> Font { font("JetBrainsMono-Regular", size) ?? .system(size: size, design: .monospaced) }

    private static func font(_ postScriptName: String, _ size: CGFloat) -> Font? {
        NSFont(name: postScriptName, size: size) != nil ? .custom(postScriptName, fixedSize: size) : nil
    }

    /// Registers the bundled fonts for this process (call once at launch).
    static func registerFonts() {
        let urls = Bundle.main.urls(forResourcesWithExtension: "ttf", subdirectory: nil) ?? []
        for url in urls { CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil) }
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// The settings switch: an amber knob and track when on, as on Windows.
struct PillSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack {
            configuration.label.foregroundStyle(Theme.text)
            Spacer()
            SwitchTrack(right: configuration.isOn, lit: configuration.isOn)
                .onTapGesture { withAnimation(.easeOut(duration: 0.12)) { configuration.isOn.toggle() } }
        }
    }
}

/// The 38x22 track and knob. `lit`: amber (on, or a two-way choice); otherwise grey.
struct SwitchTrack: View {
    var right: Bool
    var lit: Bool
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        let on = lit && enabled
        ZStack(alignment: right ? .trailing : .leading) {
            Capsule().fill(on ? Theme.accentTint : Theme.surf2)
            Capsule().strokeBorder(on ? Theme.accent : Theme.faint, lineWidth: 1)
            Circle().fill(on ? Theme.accent : Theme.dim).frame(width: 14, height: 14).padding(.horizontal, 4)
        }
        .frame(width: 38, height: 22)
        .contentShape(Rectangle())
    }
}

/// A two-way choice (Esc / F15): the switch stays amber, the knob points at the chosen side,
/// and the chosen label lights up. Clicking either label picks that side.
struct ChoiceSwitch: View {
    var left: String
    var right: String
    var isRight: Bool
    var pick: (Bool) -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text(left).fontWeight(.semibold).foregroundStyle(isRight ? Theme.dim : Theme.accent)
                .onTapGesture { pick(false) }
            SwitchTrack(right: isRight, lit: true)
                .onTapGesture { pick(!isRight) }
            Text(right).fontWeight(.semibold).foregroundStyle(isRight ? Theme.accent : Theme.dim)
                .onTapGesture { pick(true) }
        }
    }
}

/// Pill buttons; `primary` is the amber one.
struct PillButtonStyle: ButtonStyle {
    var primary = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Theme.head(13))
            .padding(.horizontal, 14).padding(.vertical, 6)
            .foregroundStyle(primary ? Theme.bg : Theme.text)
            .background(Capsule().fill(primary ? Theme.accent : Theme.surf2))
            .overlay(Capsule().strokeBorder(primary ? Color.clear : Theme.faint, lineWidth: 1))
            .opacity(configuration.isPressed ? 0.75 : 1)
    }
}

/// A section title, small caps like the Windows cards.
struct CardTitle: View {
    var text: String
    /// Space above: a section break in Settings; 0 at the top of a card.
    var top: CGFloat = 10
    var body: some View {
        Text(text.uppercased()).font(Theme.head(12)).kerning(0.6).foregroundStyle(Theme.dim).padding(.top, top)
    }
}
