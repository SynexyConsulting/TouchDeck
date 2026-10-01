import Foundation
import CoreGraphics

/// The device renderers: the firmware's own page code compiled for the Mac by hostui/build.sh
/// into libtdui_rp2040 / libtdui_esp32c3 / libtdui_rp2350.dylib (C API: hostui/tdui.h).
/// They are loaded with dlopen from the app's Frameworks folder, so a missing library only
/// turns the device view off. Output is RGB565, row by row.
public enum NativeUi {
    private typealias IntFn = @convention(c) () -> Int32
    private typealias LetterFn = @convention(c) (CChar) -> Int32
    private typealias RenderFn = @convention(c) (UnsafeRawPointer, UnsafeMutablePointer<UInt16>) -> Void
    private typealias LineFn = @convention(c) (UnsafeRawPointer, UnsafeMutablePointer<CChar>, Int32) -> Int32
    private typealias RectFn = @convention(c) (Int32, UnsafeMutablePointer<Int32>) -> Void

    private final class Lib {
        let width: IntFn, height: IntFn, stateSize: IntFn
        let letterIndex: LetterFn, render: RenderFn, stateLine: LineFn
        init?(handle: UnsafeMutableRawPointer) {
            func sym<T>(_ name: String, _: T.Type) -> T? {
                dlsym(handle, name).map { unsafeBitCast($0, to: T.self) }
            }
            guard let w = sym("tdui_width", IntFn.self), let h = sym("tdui_height", IntFn.self),
                  let s = sym("tdui_state_size", IntFn.self), let l = sym("tdui_letter_index", LetterFn.self),
                  let r = sym("tdui_render", RenderFn.self), let line = sym("tdui_state_line", LineFn.self)
            else { return nil }
            (width, height, stateSize, letterIndex, render, stateLine) = (w, h, s, l, r, line)
        }
    }

    private static let lock = NSLock()
    private static var loaded: [String: Lib] = [:]
    private static var errors: [String: String] = [:]

    /// Folders searched for the libraries: $TOUCHDECK_TDUI_DIR (tests), the app's Frameworks,
    /// then next to this framework.
    static var searchDirs: [URL] {
        var dirs: [URL] = []
        if let env = ProcessInfo.processInfo.environment["TOUCHDECK_TDUI_DIR"], !env.isEmpty {
            dirs.append(URL(fileURLWithPath: env))
        }
        if let fw = Bundle.main.privateFrameworksURL { dirs.append(fw) }
        dirs.append(Bundle(for: Lib.self).bundleURL.deletingLastPathComponent())
        return dirs
    }

    private static func lib(_ model: UiModel) -> Lib? {
        lock.withLock {
            if let l = loaded[model.library] { return l }
            if errors[model.library] != nil { return nil }
            for dir in searchDirs {
                let path = dir.appendingPathComponent("lib\(model.library).dylib").path
                guard FileManager.default.fileExists(atPath: path) else { continue }
                guard let h = dlopen(path, RTLD_NOW | RTLD_LOCAL) else {
                    errors[model.library] = String(cString: dlerror())
                    return nil
                }
                guard let l = Lib(handle: h) else {
                    errors[model.library] = "lib\(model.library).dylib lacks the tdui_* functions"
                    return nil
                }
                loaded[model.library] = l
                return l
            }
            errors[model.library] = "lib\(model.library).dylib not found"
            return nil
        }
    }

    /// nil when the board's renderer loads and agrees with `UiState`'s layout, else why not.
    public static func unavailableReason(_ model: UiModel) -> String? {
        guard let l = lib(model) else { return lock.withLock { errors[model.library] } ?? "renderer not loaded" }
        let size = Int(l.stateSize())
        if size != UiState.size { return "renderer state size \(size) != \(UiState.size)" }
        let (w, h) = (Int(l.width()), Int(l.height()))
        if w != model.size.width || h != model.size.height { return "renderer size \(w)x\(h)" }
        return nil
    }

    public static func stateSize(_ model: UiModel) -> Int? { lib(model).map { Int($0.stateSize()) } }

    /// RGB565 pixels, width*height, row by row; nil when the renderer isn't available.
    public static func render(_ model: UiModel, _ state: UiState) -> [UInt16]? {
        guard let l = lib(model) else { return nil }
        var pixels = [UInt16](repeating: 0, count: model.size.width * model.size.height)
        state.bytes.withUnsafeBytes { s in
            pixels.withUnsafeMutableBufferPointer { l.render(s.baseAddress!, $0.baseAddress!) }
        }
        return pixels
    }

    /// STATE letter= name to the renderer's letter index, or -1.
    public static func letterIndex(_ model: UiModel, _ name: Character) -> Int {
        guard let l = lib(model), let a = name.asciiValue else { return -1 }
        return Int(l.letterIndex(CChar(bitPattern: a)))
    }

    /// The STATE line the firmware would send for this state (ui_sync.c; tests).
    public static func stateLine(_ model: UiModel, _ state: UiState) -> String? {
        guard let l = lib(model) else { return nil }
        var buf = [CChar](repeating: 0, count: 512)
        let n = state.bytes.withUnsafeBytes { s in
            buf.withUnsafeMutableBufferPointer { l.stateLine(s.baseAddress!, $0.baseAddress!, Int32($0.count)) }
        }
        let len = max(0, min(Int(n), buf.count - 1))
        return String(decoding: buf[0..<len].map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }

    /// RGB565 -> a CGImage (CoreGraphics has no 565 format, so it is expanded to RGBX8888).
    public static func image(_ pixels: [UInt16], width: Int, height: Int) -> CGImage? {
        guard pixels.count >= width * height else { return nil }
        var rgba = [UInt8](repeating: 255, count: width * height * 4)
        for i in 0..<(width * height) {
            let p = pixels[i]
            let r = UInt8((p >> 11) & 0x1F), g = UInt8((p >> 5) & 0x3F), b = UInt8(p & 0x1F)
            rgba[i * 4] = (r << 3) | (r >> 2)
            rgba[i * 4 + 1] = (g << 2) | (g >> 4)
            rgba[i * 4 + 2] = (b << 3) | (b >> 2)
        }
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }
}
