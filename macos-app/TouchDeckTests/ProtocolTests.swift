import XCTest
@testable import TouchDeckCore

/// The same cases as windows-app/tests/TouchDeck.Tests/ProtocolTests.cs.
final class ProtocolTests: XCTestCase {
    func testSmartPunctuationAndAccentsBecomeAscii() {
        let (text, lost) = AsciiText.transliterate("“Café” – it’s naïve…")
        XCTAssertEqual(text, "\"Cafe\" - it's naive...")
        XCTAssertEqual(lost, 0)
    }

    func testUntypeableCharactersBecomeQuestionMarksAndAreCounted() {
        let (text, lost) = AsciiText.transliterate("a😀b漢")
        XCTAssertEqual(text, "a?b?")
        XCTAssertEqual(lost, 2)
    }

    func testSubstitutionTable() {
        for (input, expected) in [("\u{00A0}", " "), ("•", "*"), ("→", "->"), ("«", "<<")] {
            XCTAssertEqual(AsciiText.transliterate(input).text, expected, input)
        }
    }

    func testClipIsHeaderLineThenRawBytes() {
        XCTAssertEqual(ClipMessage.encode("hi\n", source: "select"), Array("CLIP 3 select\nhi\n".utf8))
    }

    func testClipPayloadIsCappedAt8192Bytes() {
        let framed = ClipMessage.encode(String(repeating: "x", count: 9000), source: "app")
        XCTAssertEqual(framed.count, "CLIP 8192 app\n".utf8.count + 8192)
    }

    func testSimpleLines() {
        XCTAssertEqual(BoardLine.parse("COPY"), .copyRequest)
        XCTAssertEqual(BoardLine.parse("PONG"), .pong)
        XCTAssertEqual(BoardLine.parse("LOG hello there"), .log("hello there"))
        XCTAssertEqual(BoardLine.parse("K 02 04"), .key(mods: 2, usage: 4))
        XCTAssertEqual(BoardLine.parse("M 01 -5 7"), .mouse(buttons: 1, dx: -5, dy: 7))
        XCTAssertEqual(BoardLine.parse("M 00 300 -300"), .mouse(buttons: 0, dx: 127, dy: -127))
        XCTAssertEqual(BoardLine.parse("VERSION rp2040-169 1.8.0 Sep 30 2026"),
                       .version(board: "rp2040-169", version: "1.8.0", build: "Sep 30 2026"))
        XCTAssertEqual(BoardLine.parse("TEXT msg Sent  to PC"), .text(key: "msg", value: "Sent  to PC"))
    }

    func testMalformedLinesAreUnknown() {
        for line in ["K zz 04", "K 02", "M 00 x 1", "M", "K -1 04", "K 100 04", "K 00 -4", "M -1 0 0", "M 1FF 0 0", "", "   ", "VERSION rp2040"] {
            guard case .unknown = BoardLine.parse(line) else { return XCTFail("parsed: \(line)") }
        }
    }

    func testDiagnosticsFieldsParseFromADbgLogLine() {
        let f = DbgFields.parse("up=12 | ev=3 jig=1")
        XCTAssertEqual(f["up"], "12")
        XCTAssertEqual(f["ev"], "3")
        XCTAssertEqual(f["jig"], "1")
    }

    func testParsesState() {
        guard case .state(let s) = BoardLine.parse("STATE jig=1 letter=W scale=2 phase=3 x=100 y=-4 clip=12 paste=1 page=2 jkey=1") else {
            return XCTFail()
        }
        XCTAssertTrue(s.jigOn)
        XCTAssertEqual(s.letter, "W")
        XCTAssertEqual(s.scale, 2)
        XCTAssertEqual(s.phase, 3)
        XCTAssertEqual(s.y, -4)
        XCTAssertEqual(s.clipLength, 12)
        XCTAssertTrue(s.pasting)
        XCTAssertTrue(s.isFullMirror)
        XCTAssertEqual(s.field("jkey"), 1)
    }

    func testMalformedStateIsUnknown() {
        for line in ["STATE jig=1", "STATE jig=1 letter=W scale=x phase=0 x=1 y=2 clip=0 paste=0"] {
            guard case .unknown = BoardLine.parse(line) else { return XCTFail(line) }
        }
    }

    func testClipTextUnescapes() {
        guard case .clipText(let b) = BoardLine.parse(#"CLIPTEXT a\\b\nc\x41\x4"#) else { return XCTFail() }
        XCTAssertEqual(b, Array("a\\b\ncA".utf8) + Array(#"x4"#.utf8))
    }

    func testLineSplitterDropsCrAndKeepsPartialLines() {
        let s = LineSplitter()
        XCTAssertEqual(s.feed(Array("PO".utf8)), [])
        XCTAssertEqual(s.feed(Array("NG\r\nLOG a\n".utf8)), ["PONG", "LOG a"])
    }

    func testSemVerComparesNumerically() {
        XCTAssertLessThan(SemVer("1.9.0")!, SemVer("1.10.0")!)
        XCTAssertNil(SemVer("unknown"))
        XCTAssertEqual(SemVer("1.8")?.description, "1.8.0")
    }
}
