import SwiftUI
import TouchDeckCore

/// The board's screen, drawn by the firmware's own page code (NativeUi). A click is TAP x y,
/// a sideways drag is SWIPE L|R (MirrorInput), exactly as on Windows. Below it, BOOT.
struct DeviceMirrorView: View {
    @EnvironmentObject private var app: AppController
    /// Points per device pixel.
    private let scale: CGFloat = 1.0

    var body: some View {
        let (w, h) = app.mirrorModel.size
        VStack(spacing: 10) {
            ZStack {
                if let image = app.mirrorImage {
                    Image(decorative: image, scale: 1)
                        .interpolation(.none)
                        .resizable()
                } else {
                    Theme.bg
                }
            }
            .frame(width: CGFloat(w) * scale, height: CGFloat(h) * scale)
            .clipShape(app.mirrorModel.isRound ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: 36)))
            .overlay(
                (app.mirrorModel.isRound ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: 36)))
                    .stroke(Theme.surf2, lineWidth: 8)
                    .padding(-4)
            )
            .gesture(DragGesture(minimumDistance: 0).onEnded { v in
                let g = MirrorInput.classify(x0: v.startLocation.x / scale, y0: v.startLocation.y / scale,
                                             x1: v.location.x / scale, y1: v.location.y / scale, width: w, height: h)
                switch g {
                case .tap(let x, let y)?: app.tap(x: x, y: y)
                case .swipe(let left)?: app.swipe(left: left)
                case nil: break
                }
            })
            .padding(8)

            HStack(spacing: 8) {
                Button("BOOT") { app.pressButton(long: false) }.buttonStyle(PillButtonStyle())
                Button("BOOT (long)") { app.pressButton(long: true) }.buttonStyle(PillButtonStyle())
            }
        }
    }
}
