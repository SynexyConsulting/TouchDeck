import XCTest
import CryptoKit
@testable import TouchDeckCore

final class UpdateTests: XCTestCase {
    private let key = P256.Signing.PrivateKey()
    private var spki: String { key.publicKey.derRepresentation.base64EncodedString() }
    private let sha = String(repeating: "ab", count: 32)

    private func feed(_ app: String = "1.4.0", fw: String = "1.9.0", url: String? = nil) -> Data {
        let u = url ?? UpdateSource.officialAssetPrefix + "app-v\(app)/TouchDeck-\(app).pkg"
        return Data("""
        {"schema": 1,
         "app": {"macos": {"version": "\(app)", "url": "\(u)", "sha256": "\(sha)", "size": 1000},
                 "windows": {"version": "9.9.9", "url": "\(UpdateSource.officialAssetPrefix)x/y.msi", "sha256": "\(sha)", "size": 5}},
         "firmware": [{"board": "rp2040-169", "version": "\(fw)", "url": "\(UpdateSource.officialAssetPrefix)fw/watch.uf2", "sha256": "\(sha)", "size": 2048}]}
        """.utf8)
    }

    func testSignatureFromThePinnedKeyVerifies() throws {
        let f = feed()
        let sig = Data(try key.signature(for: f).rawRepresentation.base64EncodedString().utf8)
        XCTAssertTrue(FeedSignature.verify(feed: f, signatureFile: sig, publicKeySpki: spki))
        var tampered = f
        tampered[tampered.startIndex + 3] ^= 1
        XCTAssertFalse(FeedSignature.verify(feed: tampered, signatureFile: sig, publicKeySpki: spki))
        let other = P256.Signing.PrivateKey().publicKey.derRepresentation.base64EncodedString()
        XCTAssertFalse(FeedSignature.verify(feed: f, signatureFile: sig, publicKeySpki: other))
        XCTAssertFalse(FeedSignature.verify(feed: f, signatureFile: Data("bm90IGEgc2ln".utf8), publicKeySpki: spki))
    }

    func testOfficialKeyParses() {
        XCTAssertNotNil(Data(base64Encoded: UpdateSource.officialPublicKey).flatMap { try? P256.Signing.PublicKey(derRepresentation: $0) })
    }

    func testFeedParsesTheMacEntry() throws {
        let parsed = try UpdateFeed.parse(feed(), source: .official)
        XCTAssertEqual(parsed.macApp?.version, SemVer(1, 4, 0))
        XCTAssertEqual(parsed.firmware.first?.board, "rp2040-169")
    }

    func testFeedPointingOutsideTheRepoIsRejected() {
        XCTAssertThrowsError(try UpdateFeed.parse(feed(url: "https://evil.example/x.pkg"), source: .official))
        XCTAssertThrowsError(try UpdateFeed.parse(feed(url: "http://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/x.pkg"), source: .official))
    }

    func testBadEntriesRejectTheWholeFeed() {
        let bad = Data(String(decoding: feed(), as: UTF8.self).replacingOccurrences(of: "\"size\": 2048", with: "\"size\": 99999999").utf8)
        XCTAssertThrowsError(try UpdateFeed.parse(bad, source: .official))
        XCTAssertThrowsError(try UpdateFeed.parse(Data("{\"schema\": 2}".utf8), source: .official))
    }

    func testSelectorNeverDowngrades() throws {
        let parsed = try UpdateFeed.parse(feed("1.4.0", fw: "1.9.0"), source: .official)
        let fw = FirmwareInfo(board: "rp2040-169", version: "1.9.0", build: "")
        XCTAssertNil(UpdateSelector.select(parsed, currentApp: SemVer(1, 4, 0), device: fw).app)
        XCTAssertNil(UpdateSelector.select(parsed, currentApp: SemVer(1, 4, 0), device: fw).firmware)
        let older = FirmwareInfo(board: "rp2040-169", version: "1.8.0", build: "")
        XCTAssertEqual(UpdateSelector.select(parsed, currentApp: SemVer(1, 3, 9), device: older).firmware?.version, SemVer(1, 9, 0))
        let other = FirmwareInfo(board: "rp2350-128", version: "1.0.0", build: "")
        XCTAssertNil(UpdateSelector.select(parsed, currentApp: SemVer(1, 3, 9), device: other).firmware)
    }

    func testRedirectsStayOnGitHub() {
        let s = UpdateSource.official
        XCTAssertTrue(s.isAllowedRedirect(URL(string: "https://objects.githubusercontent.com/a")!))
        XCTAssertFalse(s.isAllowedRedirect(URL(string: "https://githubusercontent.com.evil.example/a")!))
        XCTAssertFalse(s.isAllowedRedirect(URL(string: "http://github.com/a")!))
        XCTAssertThrowsError(try UpdateSource.forTest(feed: URL(string: "https://example.com/u.json")!, publicKey: spki))
    }

    func testClearDownloadsRemovesOnlyItsOwnFiles() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for f in ["TouchDeck-update.pkg", "rp2040-169-update.uf2.part", "keep.txt"] {
            try Data().write(to: dir.appendingPathComponent(f))
        }
        try UpdateService.clearDownloads(dir)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: dir.path), ["keep.txt"])
    }
}

final class Uf2Tests: XCTestCase {
    /// One UF2 block: family, target address and payload.
    private func block(_ family: UInt32, addr: UInt32, _ payload: [UInt8]) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: 512)
        func put(_ at: Int, _ v: UInt32) { for i in 0..<4 { b[at + i] = UInt8((v >> (8 * UInt32(i))) & 0xFF) } }
        put(0, Uf2.magic0); put(4, Uf2.magic1); put(8, Uf2.flagFamilyId)
        put(12, addr); put(16, UInt32(payload.count)); put(28, family); put(508, Uf2.magicEnd)
        for (i, v) in payload.enumerated() { b[32 + i] = v }
        return b
    }

    func testChipAndBoardFromAMarkerSplitAcrossBlocks() {
        let marker = Array("xxTDBOARD:rp2350-128;yy".utf8)
        let image = block(Uf2.rp2350ArmSFamily, addr: 0x1000_0000, Array(repeating: 0, count: 250) + marker.prefix(10))
            + block(Uf2.rp2350ArmSFamily, addr: 0x1000_0000 + 260, Array(marker.dropFirst(10)))
            + block(Uf2.rp2350AbsoluteFamily, addr: 0x1FFF_0000, [1, 2, 3])
        XCTAssertEqual(Uf2.inspect(image), Uf2Info(valid: true, chip: .rp2350, board: "rp2350-128"))
    }

    func testMixedOrBrokenImagesAreInvalid() {
        let rp2040 = block(Uf2.rp2040Family, addr: 0, [1])
        XCTAssertEqual(Uf2.inspect(rp2040).chip, .rp2040)
        XCTAssertFalse(Uf2.inspect(rp2040 + block(Uf2.rp2350ArmSFamily, addr: 512, [1])).valid)
        XCTAssertFalse(Uf2.inspect(rp2040 + block(Uf2.rp2350AbsoluteFamily, addr: 512, [1])).valid)
        XCTAssertFalse(Uf2.inspect(Array(rp2040.dropLast())).valid)
        var broken = rp2040
        broken[0] = 0
        XCTAssertFalse(Uf2.inspect(broken).valid)
    }

    func testBootDriveIsFoundByItsInfoFile() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let drive = root.appendingPathComponent("RP2350")
        try FileManager.default.createDirectory(at: drive, withIntermediateDirectories: true)
        try "UF2 Bootloader v1.0\nModel: Raspberry Pi RP2350\nBoard-ID: RP2350\n".write(to: drive.appendingPathComponent("INFO_UF2.TXT"), atomically: true, encoding: .utf8)
        XCTAssertEqual(Uf2.findBootDrive([root, drive], chip: .rp2350), drive)
        XCTAssertNil(Uf2.findBootDrive([drive], chip: .rp2040))
    }

    func testInstallRefusesTheWrongBoardBeforeTouchingIt() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).uf2")
        try Data(block(Uf2.rp2040Family, addr: 0, Array("TDBOARD:rp2040-169;".utf8))).write(to: file)
        var touched = false
        let steps = UpdateSteps(enterBootloader: { touched = true }, findBootDrive: { nil },
                                copyImage: { _, _ in touched = true }, readRunningFirmware: { nil })
        let r = await FirmwareUpdater.install(file, model: BoardModels.find("rp2350-128")!, steps: steps)
        XCTAssertFalse(r.ok)
        XCTAssertFalse(touched)
    }

    func testNewBoardsByUsbId() {
        let boards = NewBoards.fromRegistry(
            devices: [UsbId(vid: 0x2E8A, pid: 0x000F), UsbId(vid: 0x2E8A, pid: 0x000A), UsbId(vid: 0x05AC, pid: 0x1234)],
            ports: [SerialPortInfo(path: "/dev/cu.usbmodem7", usb: UsbId(vid: 0x2E8A, pid: 0x000A))])
        XCTAssertEqual(boards, [NewBoard(chip: .rp2040, state: .stockFirmware, port: "/dev/cu.usbmodem7"),
                                NewBoard(chip: .rp2350, state: .bootloader, port: nil)])
    }

    func testSettingsLoadOlderFilesWithDefaults() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).json")
        try Data("{\"dryRun\": true}".utf8).write(to: url)
        let s = AppSettings.load(url)
        XCTAssertTrue(s.dryRun)
        XCTAssertTrue(s.checkForUpdates)
        XCTAssertEqual(AppSettings.load(url.appendingPathExtension("missing")), AppSettings())
    }

    func testRedactionHidesOnlyTheWholeHomeFolder() {
        XCTAssertEqual(LogRedaction.redact("/Users/nik/Library x", home: "/Users/nik"), "~/Library x")
        XCTAssertEqual(LogRedaction.redact("/Users/nikolai/x", home: "/Users/nik"), "/Users/nikolai/x")
    }
}
