import CryptoKit
import Foundation

struct MacUpdateFile: Codable, Sendable {
    let path: String
    let size: Int64
    let sha256: String
    let mode: Int
}

struct MacUpdateManifest: Codable, Sendable {
    let schema: Int
    let product: String
    let platform: String
    let version: String
    let package: String
    let size: Int64
    let sha256: String
    let files: [MacUpdateFile]
}

enum MacUpdateFailure: Error, LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case let .invalid(message) = self { return message }; return nil }
}

enum MacUpdatePolicy {
    static let appName = "Native Serials.app"
    static let bundleID = "com.andrelouis.nativeserials"
    static let teamID = "83NN3HS237"
    static let maximumPackageBytes: Int64 = 16 * 1024 * 1024
    static let maximumExtractedBytes: Int64 = 32 * 1024 * 1024
    static let publicKey = Data(base64Encoded: "i25itvzMj50otYX5MO+qc9ea0d0PwgfT7XKHD1TKQ6I=")!

    static func verify(_ bytes: Data, signature: Data, expectedVersion: String, key: Data = publicKey) throws -> MacUpdateManifest {
        guard !bytes.isEmpty, bytes.count <= 131_072, key.count == 32,
              let raw = String(data: signature, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              raw.count <= 256, let signatureBytes = Data(base64Encoded: raw), signatureBytes.count == 64,
              let verifier = try? Curve25519.Signing.PublicKey(rawRepresentation: key),
              verifier.isValidSignature(signatureBytes, for: bytes) else {
            throw MacUpdateFailure.invalid("The update signature is missing or invalid. No program files were changed.")
        }
        let manifest = try JSONDecoder().decode(MacUpdateManifest.self, from: bytes)
        guard manifest.schema == 1, manifest.product == "NativeSerials", manifest.platform == "macos-universal",
              manifest.version == expectedVersion, numericVersion(manifest.version) != nil,
              manifest.package == "NativeSerialsMac-\(expectedVersion).zip",
              manifest.size > 0, manifest.size <= maximumPackageBytes, validHash(manifest.sha256),
              !manifest.files.isEmpty, manifest.files.count <= 1024 else {
            throw MacUpdateFailure.invalid("The signed update identity, version or size is invalid.")
        }
        var seen = Set<String>(), total: Int64 = 0
        for file in manifest.files {
            let parts = file.path.split(separator: "/", omittingEmptySubsequences: false)
            guard file.path.hasPrefix(appName + "/"), !file.path.contains("\\"), !file.path.contains("\0"),
                  parts.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." }),
                  seen.insert(file.path.precomposedStringWithCanonicalMapping.lowercased()).inserted,
                  file.size >= 0, file.size <= maximumExtractedBytes, validHash(file.sha256),
                  [0o644, 0o755].contains(file.mode), total <= maximumExtractedBytes - file.size else {
                throw MacUpdateFailure.invalid("The signed file inventory is invalid.")
            }
            total += file.size
        }
        for required in ["Contents/Info.plist", "Contents/MacOS/NativeSerials", "Contents/Resources/Manual.html", "Contents/_CodeSignature/CodeResources"] {
            guard manifest.files.contains(where: { $0.path == appName + "/" + required }) else { throw MacUpdateFailure.invalid("The update is missing a required app file.") }
        }
        return manifest
    }

    static func numericVersion(_ text: String) -> [Int]? {
        guard text.range(of: "^[0-9]{1,9}(\\.[0-9]{1,9}){1,3}$", options: .regularExpression) != nil else { return nil }
        return text.split(separator: ".").compactMap { Int($0) }
    }
    static func newer(_ candidate: String, than current: String) -> Bool {
        guard let left = numericVersion(candidate), let right = numericVersion(current) else { return false }
        for index in 0..<max(left.count, right.count) {
            let a = index < left.count ? left[index] : 0, b = index < right.count ? right[index] : 0
            if a != b { return a > b }
        }
        return false
    }
    static func allows(_ url: URL) -> Bool {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false), parts.scheme == "https",
              parts.user == nil, parts.password == nil, parts.port == nil, parts.fragment == nil else { return false }
        if parts.host == "release-assets.githubusercontent.com" { return parts.path.hasPrefix("/github-production-release-asset/") }
        guard parts.query == nil else { return false }
        if parts.host == "api.github.com" { return parts.path.hasPrefix("/repos/OnjLouis/NativeSerials/releases") }
        return parts.host == "github.com" && parts.path.range(of: "^/OnjLouis/NativeSerials/releases/download/v[0-9]{1,9}(\\.[0-9]{1,9}){1,3}/NativeSerialsMac-[0-9.]+\\.zip(\\.json(\\.sig)?)?$", options: .regularExpression) != nil
    }
    static func validHash(_ text: String) -> Bool { text.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil }
    static func hash(_ url: URL) throws -> String {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var hash = SHA256()
        while let block = try file.read(upToCount: 1_048_576), !block.isEmpty { hash.update(data: block) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
    static func verifyPackage(_ url: URL, manifest: MacUpdateManifest) throws {
        guard (try url.resourceValues(forKeys: [.fileSizeKey]).fileSize) == Int(manifest.size), try hash(url) == manifest.sha256.lowercased() else {
            throw MacUpdateFailure.invalid("The package does not match its signed size and SHA-256.")
        }
    }
}
