import Foundation

/// One firmware image shipped with the app (Resources/firmware/manifest.json).
public struct BundledFirmware: Codable, Equatable {
    public var board: String
    public var version: String
    public var file: String

    public static func loadManifest(_ dir: URL) -> [BundledFirmware] {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("manifest.json")) else { return [] }
        return (try? JSONDecoder().decode([BundledFirmware].self, from: data)) ?? []
    }

    public static func `for`(_ bundle: [BundledFirmware], board: String) -> BundledFirmware? {
        bundle.first { $0.board == board }
    }

    /// Offer the update when the board is older, or too old to report a version at all.
    public func isNewer(than running: FirmwareInfo?) -> Bool {
        guard let running, running.known, let theirs = running.semVer else { return true }
        guard let mine = SemVer(version) else { return false }
        return mine > theirs
    }
}

public enum Uf2Chip: Equatable { case unknown, rp2040, rp2350 }

/// What a firmware file is for: its chip (UF2 family IDs) and board (the TDBOARD marker, or nil).
public struct Uf2Info: Equatable {
    public var valid: Bool
    public var chip: Uf2Chip
    public var board: String?
}

public enum Uf2 {
    static let magic0: UInt32 = 0x0A32_4655, magic1: UInt32 = 0x9E5D_5157, magicEnd: UInt32 = 0x0AB1_6F30
    static let flagFamilyId: UInt32 = 0x0000_2000
    public static let rp2040Family: UInt32 = 0xE48B_FF56
    public static let rp2350ArmSFamily: UInt32 = 0xE48B_FF59
    /// SDK 2.x adds one block of this family to RP2350 images (a bootrom erratum workaround).
    public static let rp2350AbsoluteFamily: UInt32 = 0xE48B_FF57
    private static let marker = Array("TDBOARD:".utf8)

    /// Checks every 512-byte block and names the chip and board. Valid only if every block is well
    /// formed and all blocks are for one chip. The board comes from the firmware's "TDBOARD:<model>;"
    /// marker, found in the payload reassembled by address, so a marker split across blocks counts.
    public static func inspect(_ image: [UInt8]) -> Uf2Info {
        let invalid = Uf2Info(valid: false, chip: .unknown, board: nil)
        guard !image.isEmpty, image.count % 512 == 0 else { return invalid }
        var rp2040 = 0, rp2350 = 0, absolute = 0
        var payload: [UInt32: [UInt8]] = [:]
        for off in stride(from: 0, to: image.count, by: 512) {
            func u32(_ at: Int) -> UInt32 {
                UInt32(image[off + at]) | UInt32(image[off + at + 1]) << 8 | UInt32(image[off + at + 2]) << 16 | UInt32(image[off + at + 3]) << 24
            }
            guard u32(0) == magic0, u32(4) == magic1, u32(508) == magicEnd, u32(8) & flagFamilyId != 0 else { return invalid }
            let family = u32(28), addr = u32(12), size = u32(16)
            switch family {
            case rp2040Family: rp2040 += 1
            case rp2350ArmSFamily: rp2350 += 1
            case rp2350AbsoluteFamily: absolute += 1
            default: return invalid
            }
            guard size <= 476 else { return invalid }
            payload[addr] = Array(image[(off + 32)..<(off + 32 + Int(size))])
        }
        if (rp2040 > 0) == (rp2350 > 0) { return invalid }          // one chip, and some program
        if rp2040 > 0 && absolute > 0 { return invalid }            // no RP2350 blocks in an RP2040 image
        return Uf2Info(valid: true, chip: rp2040 > 0 ? .rp2040 : .rp2350, board: findBoard(payload))
    }

    // Joins address-contiguous blocks and looks for TDBOARD:<model>; in each run.
    private static func findBoard(_ blocks: [UInt32: [UInt8]]) -> String? {
        var run: [UInt8] = []
        var next: UInt32 = 0
        for addr in blocks.keys.sorted() {
            let data = blocks[addr]!
            if !run.isEmpty && addr != next {
                if let found = markerIn(run) { return found }
                run.removeAll()
            }
            run += data
            next = addr &+ UInt32(data.count)
        }
        return markerIn(run)
    }

    private static func markerIn(_ run: [UInt8]) -> String? {
        guard run.count >= marker.count else { return nil }
        for at in 0...(run.count - marker.count) where Array(run[at..<(at + marker.count)]) == marker {
            let rest = run[(at + marker.count)...]
            guard let end = rest.firstIndex(of: UInt8(ascii: ";")) else { return nil }
            let len = end - rest.startIndex
            guard len > 0, len <= 32 else { return nil }
            let name = String(decoding: rest[rest.startIndex..<end], as: UTF8.self)
            return name.allSatisfy({ ($0.isASCII && ($0.isLetter || $0.isNumber)) || $0 == "-" }) ? name : nil
        }
        return nil
    }

    /// Board-ID in each chip's bootloader drive INFO_UF2.TXT.
    static func bootId(_ chip: Uf2Chip) -> String { chip == .rp2350 ? "RP2350" : "RPI-RP2" }

    /// An RP bootloader's drive: a volume whose INFO_UF2.TXT names Board-ID RPI-RP2 (RP2040)
    /// or RP2350; with `chip`, only that chip's.
    public static func findBootDrive(_ roots: [URL], chip: Uf2Chip = .unknown) -> URL? {
        let ids = chip == .unknown ? [bootId(.rp2040), bootId(.rp2350)] : [bootId(chip)]
        for root in roots {
            guard let text = try? String(contentsOf: root.appendingPathComponent("INFO_UF2.TXT"), encoding: .utf8) else { continue }
            let id = text.split(whereSeparator: \.isNewline).first { $0.hasPrefix("Board-ID:") }
                .map { $0.dropFirst("Board-ID:".count).trimmingCharacters(in: .whitespaces) }
            if let id, ids.contains(id) { return root }
        }
        return nil
    }

    /// Mounted volumes (a UF2 bootloader shows up as /Volumes/RPI-RP2 or /Volumes/RP2350).
    public static func mountedVolumes() -> [URL] {
        FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
    }

    /// Writes the image's bytes straight to the drive. Not Finder-style copying: that adds AppleDouble
    /// "._" files and extended attributes, which some macOS versions fail to write to the bootloader.
    public static func copy(_ uf2: URL, toDrive drive: URL) throws {
        let data = try Data(contentsOf: uf2)
        do {
            try writeThrough(data, to: drive.appendingPathComponent(uf2.lastPathComponent))
        } catch where !FileManager.default.fileExists(atPath: drive.path) {
            // The board reboots as soon as the last block lands, taking the drive with it.
        }
    }

    /// Writes and forces the data out to the device (F_FULLFSYNC). macOS may otherwise keep FAT
    /// writes in its cache, and the board only reboots once it has every block.
    static func writeThrough(_ data: Data, to file: URL) throws {
        let fd = open(file.path, O_WRONLY | O_CREAT | O_TRUNC, 0o644)
        guard fd >= 0 else { throw posixError("open") }
        defer { close(fd) }
        try data.withUnsafeBytes { (buf: UnsafeRawBufferPointer) in
            var done = 0
            while done < buf.count {
                let n = write(fd, buf.baseAddress! + done, buf.count - done)
                if n < 0 {
                    if errno == EINTR { continue }
                    throw posixError("write")
                }
                done += n
            }
        }
        // The board may already be rebooting (drive gone); a failed sync then doesn't matter.
        if fcntl(fd, F_FULLFSYNC) != 0 { _ = fsync(fd) }
    }

    private static func posixError(_ what: String) -> Error {
        NSError(domain: NSPOSIXErrorDomain, code: Int(errno), userInfo: [NSLocalizedDescriptionKey: "\(what): \(String(cString: strerror(errno)))"])
    }
}

/// The steps of an RP update, injectable so the flow can be tested without a board.
public struct UpdateSteps {
    public var enterBootloader: () -> Void
    public var findBootDrive: () -> URL?
    public var copyImage: (URL, URL) throws -> Void
    /// The firmware running after the copy, or nil while the board hasn't come back.
    public var readRunningFirmware: () -> FirmwareInfo?
    /// The chip of any RP bootloader drive present, or nil: explains a board that rebooted into
    /// the other chip's bootloader.
    public var bootloaderChip: (() -> Uf2Chip?)?
    public var pollEvery: TimeInterval = 0.25
    public var bootloaderTimeout: TimeInterval = 15
    public var rebootTimeout: TimeInterval = 20

    public init(enterBootloader: @escaping () -> Void, findBootDrive: @escaping () -> URL?,
                copyImage: @escaping (URL, URL) throws -> Void, readRunningFirmware: @escaping () -> FirmwareInfo?,
                bootloaderChip: (() -> Uf2Chip?)? = nil) {
        self.enterBootloader = enterBootloader
        self.findBootDrive = findBootDrive
        self.copyImage = copyImage
        self.readRunningFirmware = readRunningFirmware
        self.bootloaderChip = bootloaderChip
    }
}

public struct UpdateResult: Equatable {
    public var ok: Bool
    public var message: String
    public var running: FirmwareInfo?
}

public enum FirmwareUpdater {
    /// Installs firmware for `model`: checks the file is for that chip and board (before touching
    /// the board), gets it into its bootloader unless already there, copies the UF2, and waits for
    /// a Touch Deck of that model to answer VER.
    public static func install(_ uf2: URL, model: BoardModel, steps: UpdateSteps,
                               progress: ((String) -> Void)? = nil) async -> UpdateResult {
        let name = uf2.lastPathComponent
        guard let data = try? Data(contentsOf: uf2) else { return UpdateResult(ok: false, message: "Can't read \(name).") }
        let info = Uf2.inspect(Array(data))
        let chipName = model.chip == .rp2350 ? "RP2350" : "RP2040"
        guard info.valid, info.chip == model.chip else { return UpdateResult(ok: false, message: "\(name) is not \(chipName) firmware.") }
        // Firmware from before the model marker was always the RP2040 1.69's.
        let legacy = info.board == nil && model.board == "rp2040-169"
        if info.board != model.board && !legacy {
            return UpdateResult(ok: false, message: "\(name) is firmware for \(info.board ?? "an unknown board"), not \(model.board).")
        }
        var drive = steps.findBootDrive()
        if drive == nil {
            progress?("Rebooting the board into its bootloader...")
            steps.enterBootloader()
            drive = await poll(steps.findBootDrive, timeout: steps.bootloaderTimeout, every: steps.pollEvery)
            if drive == nil, let seen = steps.bootloaderChip?(), seen != model.chip {
                let seenName = seen == .rp2350 ? "RP2350" : "RP2040"
                return UpdateResult(ok: false, message: "This is an \(seenName) board, not the \(model.name); it is waiting in its bootloader. " +
                                                       "Touch Deck will offer the right firmware for it.")
            }
            if drive == nil {
                let driveName = model.chip == .rp2350 ? "RP2350" : "RPI-RP2"
                return UpdateResult(ok: false, message: "The bootloader drive (\(driveName)) did not appear. Hold BOOT while plugging the board in, then try again.")
            }
        }
        progress?("Copying firmware to \(drive!.lastPathComponent)...")
        do { try steps.copyImage(uf2, drive!) } catch {
            return UpdateResult(ok: false, message: "Firmware update failed: \(error.localizedDescription)")
        }
        progress?("Waiting for the board to restart...")
        // Another RP board (also CAFE:4011) may answer first: keep waiting for this model.
        var other: FirmwareInfo?
        let running = await poll({ () -> FirmwareInfo? in
            guard let r = steps.readRunningFirmware() else { return nil }
            if r.board != model.board { other = r; return nil }
            return r
        }, timeout: steps.rebootTimeout, every: steps.pollEvery)
        if let running { return UpdateResult(ok: true, message: "Installed: \(running.board) \(running.version)", running: running) }
        if let other { return UpdateResult(ok: false, message: "Firmware copied, but only \(other.board) answered, not \(model.board).", running: other) }
        return UpdateResult(ok: false, message: "Firmware copied, but the board did not come back as a Touch Deck.")
    }

    private static func poll<T>(_ probe: () -> T?, timeout: TimeInterval, every: TimeInterval) async -> T? {
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            if let found = probe() { return found }
            if Date() >= deadline { return nil }
            try? await Task.sleep(nanoseconds: UInt64(every * 1_000_000_000))
        }
    }
}
