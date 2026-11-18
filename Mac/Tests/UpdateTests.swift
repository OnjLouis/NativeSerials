import AppKit
import CryptoKit
import Foundation

@main
struct UpdateTests {
    static func check(_ value: @autoclosure () -> Bool, _ message: String) throws {
        if !value() { throw MacUpdateFailure.invalid("Test failed: " + message) }
    }
    static func reject(_ action: () throws -> Void) throws {
        do { try action() } catch { return }
        throw MacUpdateFailure.invalid("Unsafe update accepted by test.")
    }

    @MainActor
    static func main() async {
        do { try run(); print("Mac signed updater tests passed.") }
        catch { print(error.localizedDescription); exit(1) }
    }

    @MainActor
    static func run() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent("NativeSerials-update-tests-\(UUID())")
        try fm.createDirectory(at: base, withIntermediateDirectories: false)
        defer { try? fm.removeItem(at: base) }
        let key = Curve25519.Signing.PrivateKey()
        let hash = String(repeating: "a", count: 64)
        let names = ["Contents/Info.plist", "Contents/MacOS/NativeSerials", "Contents/Resources/Manual.html", "Contents/_CodeSignature/CodeResources"]
        let files = names.map { MacUpdateFile(path: MacUpdatePolicy.appName + "/" + $0, size: 1, sha256: hash, mode: $0.contains("MacOS/") ? 0o755 : 0o644) }
        func manifest(product: String = "NativeSerials", platform: String = "macos-universal", version: String = "1.2", files: [MacUpdateFile]? = nil) -> MacUpdateManifest {
            MacUpdateManifest(schema: 1, product: product, platform: platform, version: version,
                              package: "NativeSerialsMac-\(version).zip", size: 1, sha256: hash, files: files ?? [])
        }
        func verify(_ value: MacUpdateManifest) throws {
            let data = try JSONEncoder().encode(value)
            _ = try MacUpdatePolicy.verify(data, signature: Data(try key.signature(for: data).base64EncodedString().utf8), expectedVersion: "1.2", key: key.publicKey.rawRepresentation)
        }
        let valid = manifest(files: files)
        let bytes = try JSONEncoder().encode(valid)
        let signature = Data(try key.signature(for: bytes).base64EncodedString().utf8)
        try verify(valid)
        try reject { _ = try MacUpdatePolicy.verify(bytes, signature: Data(), expectedVersion: "1.2", key: key.publicKey.rawRepresentation) }
        try reject { _ = try MacUpdatePolicy.verify(bytes + Data([32]), signature: signature, expectedVersion: "1.2", key: key.publicKey.rawRepresentation) }
        try reject { _ = try MacUpdatePolicy.verify(bytes, signature: signature, expectedVersion: "1.2") }
        try reject { try verify(manifest(product: "Other", files: files)) }
        try reject { try verify(manifest(platform: "other", files: files)) }
        try reject { try verify(manifest(version: "1.3", files: files)) }
        try reject { try verify(manifest(files: files + files)) }
        try reject { try verify(manifest(files: files + [MacUpdateFile(path: MacUpdatePolicy.appName + "/../escape", size: 0, sha256: hash, mode: 0o644)])) }
        try check(MacUpdatePolicy.newer("1.10", than: "1.9") && !MacUpdatePolicy.newer("1.1.0", than: "1.1"), "Numeric versions")
        for address in ["http://github.com/OnjLouis/NativeSerials/releases", "https://example.invalid/update.zip", "https://github.com/Other/NativeSerials/releases/download/v1.2/NativeSerialsMac-1.2.zip", "https://user@api.github.com/repos/OnjLouis/NativeSerials/releases"] {
            try check(!MacUpdatePolicy.allows(URL(string: address)!), "Untrusted address")
        }
        let service = UpdateService()
        func release(_ version: String, draft: Bool = false, prerelease: Bool = false, signed: Bool = true) -> UpdateService.GitHubRelease {
            let name = "NativeSerialsMac-\(version).zip"
            return UpdateService.GitHubRelease(tag_name: "v" + version, html_url: "https://github.com/OnjLouis/NativeSerials/releases/tag/v" + version,
                draft: draft, prerelease: prerelease, assets: (signed ? ["", ".json", ".json.sig"] : [""]).map {
                    UpdateService.GitHubAsset(name: name + $0, browser_download_url: "https://github.com/OnjLouis/NativeSerials/releases/download/v\(version)/" + name + $0, size: 1)
                })
        }
        try check(service.candidate(from: [release("1.2"), release("1.3", draft: true), release("1.4", prerelease: true), release("1.5", signed: false)], currentVersion: "1.1")?.version == "1.2", "Stable signed release selection")
        try check(service.candidate(from: [release("1.1")], currentVersion: "1.1") == nil, "No equal-version update")

        let app = base.appendingPathComponent(MacUpdatePolicy.appName)
        var actualFiles = [MacUpdateFile]()
        for name in names {
            let file = app.appendingPathComponent(name)
            try fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data([1]).write(to: file)
            let mode = name.contains("MacOS/") ? 0o755 : 0o644
            try fm.setAttributes([.posixPermissions: mode], ofItemAtPath: file.path)
            actualFiles.append(MacUpdateFile(path: MacUpdatePolicy.appName + "/" + name, size: 1, sha256: try MacUpdatePolicy.hash(file), mode: mode))
        }
        let inventory = manifest(files: actualFiles)
        try MacUpdatePackage.verifyInventory(app, manifest: inventory)
        try Data([2]).write(to: app.appendingPathComponent(names[0]))
        try reject { try MacUpdatePackage.verifyInventory(app, manifest: inventory) }
        try reject { try MacUpdatePackage.verifyApp(app, version: "1.2") }
        let zip = base.appendingPathComponent("fixture.zip")
        func zipCheck(_ records: [(String, UInt32)], unsafe: Bool) throws {
            try makeZip(records).write(to: zip)
            if unsafe { try reject { try MacZipInventory.validate(zip) } }
            else { try MacZipInventory.validate(zip) }
        }
        let ordinary = names.map { (MacUpdatePolicy.appName + "/" + $0, UInt32(0o100644)) }
        try zipCheck(ordinary, unsafe: false)
        try zipCheck(ordinary + [(MacUpdatePolicy.appName + "/../bad", 0o100644)], unsafe: true)
        try zipCheck(ordinary + [(MacUpdatePolicy.appName + "/link", 0o120777)], unsafe: true)
        try zipCheck(ordinary + [(MacUpdatePolicy.appName + "/pipe", 0o010644)], unsafe: true)
        try zipCheck(ordinary + [(ordinary[0].0.uppercased(), 0o100644)], unsafe: true)
        try reject { try MacUpdatePolicy.verifyPackage(zip, manifest: valid) }
        let begin = Date()
        try reject { try MacUpdatePackage.run("/bin/sleep", ["10"], timeout: 0.1) }
        try check(Date().timeIntervalSince(begin) < 4, "Bounded subprocess wait")

        if CommandLine.arguments.count == 4 {
            let archive = URL(fileURLWithPath: CommandLine.arguments[1])
            let data = try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[2]))
            let sig = try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[3]))
            let version = try JSONDecoder().decode(MacUpdateManifest.self, from: data).version
            let real = try MacUpdatePolicy.verify(data, signature: sig, expectedVersion: version)
            let extracted = try MacUpdatePackage.extract(archive, to: base.appendingPathComponent("verified"), manifest: real)
            try transactions(base, app: extracted, version: version)
        }
    }

    static func transactions(_ base: URL, app: URL, version: String) throws {
        let fm = FileManager.default
        let domain = "NativeSerials.UpdaterTests." + UUID().uuidString
        defer { UserDefaults.standard.removePersistentDomain(forName: domain) }
        for fail in [true, false] {
            let trial = base.appendingPathComponent(UUID().uuidString)
            let target = trial.appendingPathComponent(MacUpdatePolicy.appName)
            let root = trial.appendingPathComponent("NativeSerialsUpdate-" + UUID().uuidString)
            let staged = root.appendingPathComponent(MacUpdatePolicy.appName)
            try fm.createDirectory(at: root, withIntermediateDirectories: true)
            try fm.copyItem(at: app, to: target)
            try fm.copyItem(at: app, to: staged)
            let before = try MacUpdatePolicy.hash(target.appendingPathComponent("Contents/MacOS/NativeSerials"))
            let installer = MacUpdateInstallation(target: target, staged: staged, stagingRoot: root, version: version,
                oldPID: ProcessInfo.processInfo.processIdentifier, oldExecutable: "/not-the-fixture", relaunch: false,
                statusDomain: domain, diagnosticLog: trial.appendingPathComponent("failure.log"))
            var script = try installer.script()
            if fail { script = script.replacingOccurrences(of: "/bin/mv \"$prepared\" \"$target\"", with: "/usr/bin/false") }
            let helper = trial.appendingPathComponent("install.zsh")
            try script.write(to: helper, atomically: true, encoding: .utf8)
            if fail { try reject { try MacUpdatePackage.run("/bin/zsh", [helper.path]) } }
            else { try MacUpdatePackage.run("/bin/zsh", [helper.path]) }
            let after = try MacUpdatePolicy.hash(target.appendingPathComponent("Contents/MacOS/NativeSerials"))
            try check(after == before, "Exact replacement/rollback contents")
            try MacUpdatePackage.verifyApp(target, version: version)
            try check(!fm.fileExists(atPath: root.path), "Transaction staging cleanup")
            let leftovers = try fm.contentsOfDirectory(atPath: trial.path)
            try check(!leftovers.contains(where: { $0.hasPrefix(".NativeSerials-") }), "No lock or previous-app leftovers")
        }
    }

    static func makeZip(_ records: [(String, UInt32)]) -> Data {
        var local = Data(), central = Data()
        for (name, mode) in records {
            let path = Data(name.utf8), offset = local.count
            local.le32(0x04034b50); local.le16(20); local.le16(0); local.le16(0)
            local.le16(0); local.le16(0); local.le32(0); local.le32(0); local.le32(0)
            local.le16(path.count); local.le16(0); local.append(path)
            central.le32(0x02014b50); central.le16(0x0314); central.le16(20); central.le16(0); central.le16(0)
            central.le16(0); central.le16(0); central.le32(0); central.le32(0); central.le32(0)
            central.le16(path.count); central.le16(0); central.le16(0); central.le16(0); central.le16(0)
            central.le32(mode << 16); central.le32(UInt32(offset)); central.append(path)
        }
        let offset = local.count
        local.append(central); local.le32(0x06054b50); local.le16(0); local.le16(0)
        local.le16(records.count); local.le16(records.count); local.le32(UInt32(central.count)); local.le32(UInt32(offset)); local.le16(0)
        return local
    }
}

private extension Data {
    mutating func le16(_ value: Int) { append(UInt8(value & 255)); append(UInt8((value >> 8) & 255)) }
    mutating func le32(_ value: UInt32) { le16(Int(value & 65535)); le16(Int(value >> 16)) }
}
