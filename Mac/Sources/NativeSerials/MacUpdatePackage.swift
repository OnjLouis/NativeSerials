import CryptoKit
import Darwin
import Foundation

enum MacUpdatePackage {
    static func verifyApp(_ app: URL, version: String, requireNotarization: Bool = true) throws {
        guard app.lastPathComponent == MacUpdatePolicy.appName,
              let bundle = Bundle(url: app), bundle.bundleIdentifier == MacUpdatePolicy.bundleID,
              bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == version else {
            throw MacUpdateFailure.invalid("The update has the wrong application or version.")
        }
        guard let files = FileManager.default.enumerator(at: app, includingPropertiesForKeys: [.isSymbolicLinkKey]) else {
            throw MacUpdateFailure.invalid("The update could not be inspected.")
        }
        for case let file as URL in files {
            guard try file.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == false else {
                throw MacUpdateFailure.invalid("The update contains an unexpected symbolic link.")
            }
        }
        let requirement = "anchor apple generic and certificate leaf[subject.OU] = \"\(MacUpdatePolicy.teamID)\" and identifier \"\(MacUpdatePolicy.bundleID)\""
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--test-requirement", "=\(requirement)", app.path])
        for architecture in ["arm64", "x86_64"] {
            try run("/usr/bin/lipo", ["-verify_arch", architecture, app.appendingPathComponent("Contents/MacOS/NativeSerials").path])
        }
        if requireNotarization {
            try run("/usr/bin/xcrun", ["stapler", "validate", app.path])
            try run("/usr/sbin/spctl", ["--assess", "--type", "execute", app.path])
        }
    }

    static func extract(_ archive: URL, to directory: URL, manifest: MacUpdateManifest) throws -> URL {
        try MacUpdatePolicy.verifyPackage(archive, manifest: manifest)
        try MacZipInventory.validate(archive)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        try run("/usr/bin/ditto", ["-x", "-k", archive.path, directory.path], timeout: 60)
        let entries = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        guard entries.count == 1, entries[0].lastPathComponent == MacUpdatePolicy.appName else {
            throw MacUpdateFailure.invalid("The update contains unexpected files or folders.")
        }
        try verifyInventory(entries[0], manifest: manifest)
        try verifyApp(entries[0], version: manifest.version)
        return entries[0]
    }

    static func verifyInventory(_ app: URL, manifest: MacUpdateManifest) throws {
        var expected = [String: MacUpdateFile]()
        for file in manifest.files {
            guard expected.updateValue(file, forKey: file.path) == nil else { throw MacUpdateFailure.invalid("Duplicate signed file.") }
        }
        guard let files = FileManager.default.enumerator(at: app, includingPropertiesForKeys: [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey]) else {
            throw MacUpdateFailure.invalid("The extracted files cannot be inspected.")
        }
        var seen = Set<String>()
        for case let file as URL in files {
            let values = try file.resourceValues(forKeys: [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink == false else { throw MacUpdateFailure.invalid("Links are not allowed in updates.") }
            if values.isDirectory == true { continue }
            let relative = MacUpdatePolicy.appName + "/" + String(file.standardizedFileURL.path.dropFirst(app.standardizedFileURL.path.count + 1))
            let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
            guard values.isRegularFile == true, let record = expected[relative],
                  seen.insert(relative).inserted, (attributes[.posixPermissions] as? NSNumber)?.intValue == record.mode,
                  (attributes[.size] as? NSNumber)?.int64Value == record.size,
                  try MacUpdatePolicy.hash(file) == record.sha256.lowercased() else {
                throw MacUpdateFailure.invalid("An extracted file differs from the signed inventory: \(relative).")
            }
        }
        guard seen.count == expected.count else { throw MacUpdateFailure.invalid("The update is missing signed files.") }
    }

    /// Runs on an updater worker, never the main actor or a speech/audio callback.
    static func run(_ executable: String, _ arguments: [String], timeout: TimeInterval = 30) throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("NativeSerials-command-\(UUID().uuidString).log")
        FileManager.default.createFile(atPath: file.path, contents: nil, attributes: [.posixPermissions: 0o600])
        let output = try FileHandle(forWritingTo: file)
        defer { try? output.close(); try? FileManager.default.removeItem(at: file) }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = output; process.standardError = output
        try process.run()
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        if process.isRunning {
            process.terminate()
            let limit = Date().addingTimeInterval(2)
            while process.isRunning && Date() < limit { Thread.sleep(forTimeInterval: 0.05) }
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            process.waitUntilExit()
            throw MacUpdateFailure.invalid("The update verification tool did not respond in time.")
        }
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let reader = try FileHandle(forReadingFrom: file)
            defer { try? reader.close() }
            let size = try reader.seekToEnd()
            try reader.seek(toOffset: size > 8192 ? size - 8192 : 0)
            let detail = String(data: try reader.readToEnd() ?? Data(), encoding: .utf8) ?? "Verification failed."
            throw MacUpdateFailure.invalid(detail.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }
}
