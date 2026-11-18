import AppKit
import Foundation

private final class UpdateRedirectPolicy: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(request.url.map(MacUpdatePolicy.allows) == true ? request : nil)
    }
}

@MainActor
final class UpdateService {
    struct GitHubRelease: Decodable {
        let tag_name: String
        let html_url: String
        let draft: Bool
        let prerelease: Bool
        let assets: [GitHubAsset]
    }
    struct GitHubAsset: Decodable {
        let name: String
        let browser_download_url: String
        let size: Int64
    }
    struct UpdateCandidate: Sendable {
        let version: String
        let releaseURL: URL
        let downloadURL: URL
        let manifestURL: URL
        let signatureURL: URL
        let size: Int64
    }

    static let projectURL = URL(string: "https://github.com/OnjLouis/NativeSerials")!
    private let session: URLSession
    private var work: Task<Void, Never>?

    init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 25
        config.timeoutIntervalForResource = 180
        config.httpAdditionalHeaders = ["User-Agent": "Native Serials updater"]
        session = URLSession(configuration: config, delegate: UpdateRedirectPolicy(), delegateQueue: nil)
        if Bundle.main.bundleIdentifier == MacUpdatePolicy.bundleID,
           let result = UserDefaults.standard.string(forKey: "updateInstallResult") {
            UserDefaults.standard.removeObject(forKey: "updateInstallResult")
            DispatchQueue.main.async { [weak self] in self?.message("Native Serials Update", result) }
        }
    }

    func check(currentVersion: String, manual: Bool, installSilently: Bool) {
        guard work == nil else { return }
        work = Task { [weak self] in
            guard let self else { return }
            defer { work = nil }
            do {
                let url = URL(string: "https://api.github.com/repos/OnjLouis/NativeSerials/releases")!
                let data = try await boundedData(url, limit: 1_048_576)
                let releases = try JSONDecoder().decode([GitHubRelease].self, from: data)
                guard let candidate = candidate(from: releases, currentVersion: currentVersion) else {
                    if manual { message("Native Serials Is Up to Date", "Installed version: \(currentVersion).") }
                    return
                }
                let manifestBytes = try await boundedData(candidate.manifestURL, limit: 131_072)
                let signature = try await boundedData(candidate.signatureURL, limit: 256)
                let manifest = try MacUpdatePolicy.verify(manifestBytes, signature: signature, expectedVersion: candidate.version)
                guard manifest.size == candidate.size else { throw MacUpdateFailure.invalid("The package size differs from its signed manifest.") }
                if !installSilently {
                    let alert = NSAlert()
                    alert.messageText = "Native Serials \(candidate.version) Is Available"
                    alert.informativeText = "Install the signed update and relaunch Native Serials? The previous app is kept until replacement succeeds."
                    alert.addButton(withTitle: "Install")
                    alert.addButton(withTitle: "Version History")
                    alert.addButton(withTitle: "Cancel")
                    switch alert.runModal() {
                    case .alertFirstButtonReturn: break
                    case .alertSecondButtonReturn: NSWorkspace.shared.open(candidate.releaseURL); return
                    default: return
                    }
                }
                try await install(candidate, manifest: manifest)
            } catch {
                if manual || installSilently { message("Could Not Update Native Serials", error.localizedDescription) }
            }
        }
    }

    func candidate(from releases: [GitHubRelease], currentVersion: String) -> UpdateCandidate? {
        releases.compactMap { release -> UpdateCandidate? in
            guard !release.draft, !release.prerelease, release.tag_name.hasPrefix("v") else { return nil }
            let version = String(release.tag_name.dropFirst())
            guard MacUpdatePolicy.newer(version, than: currentVersion),
                  release.html_url == Self.projectURL.absoluteString + "/releases/tag/v" + version else { return nil }
            let name = "NativeSerialsMac-\(version).zip"
            let base = Self.projectURL.absoluteString + "/releases/download/v" + version + "/"
            func asset(_ suffix: String, limit: Int64) -> GitHubAsset? {
                let matches = release.assets.filter { $0.name == name + suffix }
                guard matches.count == 1, let value = matches.first, value.size > 0, value.size <= limit,
                      value.browser_download_url == base + name + suffix else { return nil }
                return value
            }
            guard let archive = asset("", limit: MacUpdatePolicy.maximumPackageBytes),
                  let manifest = asset(".json", limit: 131_072), let signature = asset(".json.sig", limit: 256) else { return nil }
            return UpdateCandidate(version: version, releaseURL: URL(string: release.html_url)!,
                                   downloadURL: URL(string: archive.browser_download_url)!,
                                   manifestURL: URL(string: manifest.browser_download_url)!,
                                   signatureURL: URL(string: signature.browser_download_url)!, size: archive.size)
        }.max { MacUpdatePolicy.newer($1.version, than: $0.version) }
    }

    private func boundedData(_ url: URL, limit: Int) async throws -> Data {
        guard MacUpdatePolicy.allows(url) else { throw MacUpdateFailure.invalid("The update address is not trusted.") }
        let (bytes, response) = try await session.bytes(from: url)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              http.url.map(MacUpdatePolicy.allows) == true, response.expectedContentLength <= Int64(limit) else {
            bytes.task.cancel()
            throw MacUpdateFailure.invalid("The update server returned an invalid or oversized response.")
        }
        defer { bytes.task.cancel() }
        var data = Data()
        for try await byte in bytes {
            guard data.count < limit else { throw MacUpdateFailure.invalid("The update response is too large.") }
            data.append(byte)
        }
        return data
    }

    private func install(_ candidate: UpdateCandidate, manifest: MacUpdateManifest) async throws {
        let target = Bundle.main.bundleURL.standardizedFileURL
        let manager = FileManager.default
        guard target.path == "/Applications/Native Serials.app", target.resolvingSymlinksInPath() == target,
              manager.isWritableFile(atPath: target.deletingLastPathComponent().path),
              let executable = Bundle.main.executableURL else {
            throw MacUpdateFailure.invalid("Move Native Serials to Applications before installing updates.")
        }
        let root = manager.temporaryDirectory.appendingPathComponent("NativeSerialsUpdate-\(UUID().uuidString)")
        try manager.createDirectory(at: root, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        var handedOff = false
        defer { if !handedOff { try? manager.removeItem(at: root) } }
        let archive = root.appendingPathComponent(manifest.package)
        let bytes = try await boundedData(candidate.downloadURL, limit: Int(manifest.size))
        guard bytes.count == Int(manifest.size) else { throw MacUpdateFailure.invalid("The update download was incomplete.") }
        try bytes.write(to: archive, options: .atomic)
        let staged = try await Task.detached {
            try MacUpdatePackage.extract(archive, to: root.appendingPathComponent("extract"), manifest: manifest)
        }.value
        let script = try MacUpdateInstallation(target: target, staged: staged, stagingRoot: root,
                                               version: candidate.version, oldPID: ProcessInfo.processInfo.processIdentifier,
                                               oldExecutable: executable.path).script()
        let helper = root.appendingPathComponent("install.zsh")
        try script.write(to: helper, atomically: true, encoding: .utf8)
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = [helper.path]
        try process.run()
        handedOff = true
        NSApp.terminate(nil)
    }

    func openVersionHistory() { NSWorkspace.shared.open(Self.projectURL.appendingPathComponent("releases")) }
    func openProjectPage() { NSWorkspace.shared.open(Self.projectURL) }
    func openContactPage() { NSWorkspace.shared.open(URL(string: "https://onj.me/contact")!) }
    func openDonatePage() { NSWorkspace.shared.open(URL(string: "https://onj.me/donate")!) }

    private func message(_ title: String, _ text: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.runModal()
    }
}
