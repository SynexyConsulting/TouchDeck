import SwiftUI
import AppKit
import TouchDeckCore

/// The board's screen, drawn by the firmware's own page code (NativeUi), in a bezel shaped like
/// the board, as the Windows DeviceMirror: RP2040 a 240x280 panel with 44 px corners, round
/// boards a circle. A click is TAP x y, a sideways drag SWIPE L|R (MirrorInput).
struct DeviceMirrorView: View {
    @EnvironmentObject private var app: AppController
    /// Text over the dark panel (no board, or nothing to show yet); nil shows the frame.
    var placeholder: String?

    /// Points per device pixel, as on Windows.
    static let scale: CGFloat = 1.25
    private static let bezelPad: CGFloat = 14

    var body: some View {
        let model = app.mirrorModel
        let (w, h) = model.size
        let pw = CGFloat(w) * Self.scale, ph = CGFloat(h) * Self.scale
        let r = model.isRound ? pw / 2 : 44 * Self.scale
        let glass = model.isRound ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: r, style: .continuous))
        ZStack {
            Color(hex: 0x07090D)
            if placeholder == nil, let image = app.mirrorImage {
                Image(decorative: image, scale: 1)
                    .interpolation(.high)
                    .resizable()
            }
            if let placeholder, !placeholder.isEmpty {
                Text(placeholder).font(Theme.ui(14)).foregroundStyle(Theme.faint)
                    .multilineTextAlignment(.center).padding(.horizontal, 40)
            }
            // Only a live frame takes clicks, as on Windows.
            if placeholder == nil {
                MirrorClickLayer { x0, y0, x1, y1 in
                    switch MirrorInput.classify(x0: x0 / Self.scale, y0: y0 / Self.scale,
                                                x1: x1 / Self.scale, y1: y1 / Self.scale, width: w, height: h) {
                    case .tap(let x, let y)?: app.tap(x: x, y: y)
                    case .swipe(let left)?: app.swipe(left: left)
                    case nil: break
                    }
                }
            }
        }
        .frame(width: pw, height: ph)
        .clipShape(glass)
        .padding(Self.bezelPad)
        .background(bezel(model.isRound, radius: r + Self.bezelPad))
    }
}

extension DeviceMirrorView {
    /// The board around the glass: a circle for round boards, else the panel's corners plus the pad.
    @ViewBuilder
    private func bezel(_ round: Bool, radius: CGFloat) -> some View {
        let fill = Color(hex: 0x1B1F27), edge = Color(hex: 0x2E3542)
        if round {
            Circle().fill(fill).overlay(Circle().strokeBorder(edge, lineWidth: 1.5))
        } else {
            RoundedRectangle(cornerRadius: radius, style: .continuous).fill(fill)
                .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(edge, lineWidth: 1.5))
        }
    }
}

/// Mouse down/up on the panel in view points (top-left origin). An AppKit view so the first click
/// on an inactive window is a tap too (acceptsFirstMouse), not just a window activation.
struct MirrorClickLayer: NSViewRepresentable {
    var onGesture: (CGFloat, CGFloat, CGFloat, CGFloat) -> Void

    func makeNSView(context: Context) -> ClickView {
        let v = ClickView()
        v.onGesture = onGesture
        return v
    }

    func updateNSView(_ v: ClickView, context: Context) { v.onGesture = onGesture }

    final class ClickView: NSView {
        var onGesture: ((CGFloat, CGFloat, CGFloat, CGFloat) -> Void)?
        private var down: CGPoint?

        override var isFlipped: Bool { true }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }

        override func mouseDown(with event: NSEvent) {
            down = convert(event.locationInWindow, from: nil)
        }

        override func mouseUp(with event: NSEvent) {
            guard let a = down else { return }
            down = nil
            let b = convert(event.locationInWindow, from: nil)
            onGesture?(a.x, a.y, b.x, b.y)
        }
    }
}
