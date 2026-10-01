import XCTest
@testable import TouchDeckCore

final class MirrorTests: XCTestCase {
    func testUiStateLayoutMatchesUiStateH() {
        XCTAssertEqual(UiState.size, 1264)
        XCTAssertEqual(UiState.clip.offset + UiState.clip.size, UiState.Field.jigMenuOn.rawValue)
        XCTAssertEqual(UiState.Field.jigPauseS.rawValue + 4, UiState.size)
    }

    func testMirrorAppliesStateTextAndClipText() {
        let m = MirrorState(model: .rp2350Round, letterIndex: { $0 == "W" ? 1 : -1 })
        XCTAssertEqual(m.state.get(UiState.clipSrc), "-")
        guard case .state(let st) = BoardLine.parse("STATE jig=1 letter=W scale=1 phase=0 x=250 y=750 clip=3 paste=0 page=2 pk=4294967295") else {
            return XCTFail()
        }
        XCTAssertTrue(m.apply(.state(st)))
        XCTAssertTrue(m.complete)
        XCTAssertEqual(m.state[.screen], 2)
        XCTAssertEqual(m.state[.jigLetter], 1)
        XCTAssertEqual(m.state.float(.jigX), 250)
        XCTAssertEqual(UInt32(bitPattern: m.state[.btPasskey]), 4_294_967_295)
        m.apply(.text(key: "msg", value: String(repeating: "m", count: 60)))
        XCTAssertEqual(m.message.count, 39)                       // NUL-terminated, cut to fit
        m.apply(.clipText(Array("abc".utf8)))
        XCTAssertEqual(m.state.get(UiState.clip), "abc")
        XCTAssertFalse(m.apply(.text(key: "nope", value: "x")))
    }

    func testGestures() {
        XCTAssertEqual(MirrorInput.classify(x0: 100.4, y0: 50.9, x1: 104, y1: 52, width: 240, height: 240), .tap(x: 100, y: 50))
        XCTAssertEqual(MirrorInput.classify(x0: 200, y0: 100, x1: 120, y1: 110, width: 240, height: 240), .swipe(left: true))
        XCTAssertEqual(MirrorInput.classify(x0: 20, y0: 100, x1: 90, y1: 100, width: 240, height: 240), .swipe(left: false))
        XCTAssertNil(MirrorInput.classify(x0: 100, y0: 20, x1: 105, y1: 120, width: 240, height: 240))   // vertical drag
        XCTAssertNil(MirrorInput.classify(x0: -3, y0: 20, x1: -3, y1: 20, width: 240, height: 240))      // off the panel
    }

    func testModels() {
        XCTAssertEqual(UiModel.for(.rp2040, board: "rp2350-128"), .rp2350Round)
        XCTAssertEqual(UiModel.for(.rp2040, board: nil), .rp2040Rect)
        XCTAssertEqual(UiModel.for(.esp32c3, board: "esp32c3-128"), .esp32Round)
        XCTAssertEqual(UiModel.rp2350Round.clipPage, 1)
        XCTAssertEqual(UiModel.esp32Round.pageCount, 4)
        XCTAssertEqual(UiModel.rp2040Rect.pageCount, 3)
    }

    /// Needs the renderers: the scheme sets TOUCHDECK_TDUI_DIR to ../hostui/out (run hostui/build.sh).
    func testRenderersAgreeWithTheLayout() throws {
        for model in [UiModel.rp2040Rect, .esp32Round, .rp2350Round] {
            guard let size = NativeUi.stateSize(model) else { throw XCTSkip("lib\(model.library).dylib not built (hostui/build.sh)") }
            XCTAssertEqual(size, UiState.size, model.library)
            XCTAssertNil(NativeUi.unavailableReason(model))
            var s = UiState()
            s[.screen] = Int32(model.clipPage)
            let px = try XCTUnwrap(NativeUi.render(model, s))
            XCTAssertEqual(px.count, model.size.width * model.size.height)
            XCTAssertTrue(px.contains { $0 != 0 }, "a page draws something")
            XCTAssertNotNil(NativeUi.image(px, width: model.size.width, height: model.size.height))
            XCTAssertGreaterThanOrEqual(NativeUi.letterIndex(model, "O"), 0)
            XCTAssertTrue(NativeUi.stateLine(model, s)?.hasPrefix("STATE ") ?? false)
        }
    }

    func testJigView() {
        let s = StateReport(jigOn: true, letter: "O", scale: 1, phase: 4, x: 0, y: 0, clipLength: 1, pasting: false,
                            fields: ["jkey": 1, "jmenu": 1, "jopen": 2, "jpause": 0])
        XCTAssertEqual(JigView.status(s), "F15")
        XCTAssertEqual(JigView.scaleText(1), "1.5X")
        XCTAssertEqual(JigView.clipText(s), "Board clip: 1 char")
        XCTAssertEqual(JigView.config(s), JigConfig(menuOn: true, f15: true, openS: 2, pauseS: 0))
        XCTAssertTrue(JigView.canClear(s))
    }
}
