import Foundation
import Darwin

final class StorageCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var stopped = false
    func cancel() { lock.lock(); stopped = true; lock.unlock() }
    func check() throws {
        lock.lock(); let cancelled = stopped; lock.unlock()
        if cancelled { throw CancellationError() }
    }
}

struct StorageRow: Sendable {
    var name: String
    var company: String
    var path: String
    var state: String
    var bytes: Int64 = 0
    var locations: [String] = []
    var locationSummary: String { locations.isEmpty ? path : locations.joined(separator: "; ") }
    var hasSize: Bool { ["Measured", "Partial", "Links skipped"].contains(state) }
}

struct StorageCompany: Sendable {
    var name: String
    var count: Int
    var measuredCount: Int
    var bytes: Int64
    var availableCount: Int = 0
}

struct StorageResult: Sendable {
    var rows: [StorageRow] = []
    var companies: [StorageCompany] = []
    var totalBytes: Int64 = 0
    var hasOverlap = false
}

enum StorageScanner {
    static func companyName(_ raw: String) -> String {
        let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        switch name.lowercased() {
        case "": return "Unknown company"
        case "fracture sounds - blueprint": return "Fracture Sounds"
        case "native instruments gmbh": return "Native Instruments"
        case "project sam": return "ProjectSAM"
        default: return name
        }
    }
    static func contains(_ root: String, _ path: String) -> Bool {
        path == root || path.hasPrefix(root + "/")
    }

    static func scan(_ products: [NativeProduct], cancellation: StorageCancellation,
                     progress: @Sendable (String) -> Void = { _ in },
                     searchRoots: StorageSearchRoots? = nil) throws -> StorageResult {
        let files = FileManager.default
        try cancellation.check()
        let search = searchRoots ?? StorageSearchRoots.system(for: products)
        let discovered = try StorageDiscovery.discover(products, roots: search, cancellation: cancellation)
        var result = StorageResult()
        for (index, product) in products.enumerated() {
            try cancellation.check()
            let rawPath = product.contentDir.isEmpty ? product.installDir : StorageDiscovery.contentLocation(product)
            var row = StorageRow(name: product.name, company: companyName(product.company),
                                 path: rawPath, state: rawPath.isEmpty ? "No location" : "Unavailable")
            var missing = false
            var broad = false
            let candidates = StorageDiscovery.registeredRoots(product) + discovered[index]
            for candidate in candidates {
                let path = StorageDiscovery.canonical(candidate)
                guard path.hasPrefix("/") else { missing = true; continue }
                if StorageDiscovery.isBroad(path, search: search) { broad = true; continue }
                if files.fileExists(atPath: path) { row.locations.append(path) }
                else { missing = true }
            }
            row.locations = Array(Set(row.locations)).sorted()
            row.path = row.locations.first(where: { $0 == StorageDiscovery.canonical(rawPath) }) ?? row.locations.first ?? rawPath
            row.state = row.locations.isEmpty ? (broad ? "Unsafe broad location" : row.state) : (missing ? "Partial" : "Measured")
            result.rows.append(row)
        }
        let valid = result.rows.indices.filter { !result.rows[$0].locations.isEmpty }
        let roots = Set(valid.flatMap { result.rows[$0].locations }).sorted { $0.count < $1.count }
        var minimal: [String] = []
        for root in roots where !minimal.contains(where: { contains($0, root) }) { minimal.append(root) }
        var totals: [String: Int64] = [:]
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey, .isRegularFileKey, .fileSizeKey]
        var lastProgress = ProcessInfo.processInfo.systemUptime
        for root in minimal {
            try cancellation.check()
            let members = valid.filter { index in result.rows[index].locations.contains { contains(root, $0) } }
            if members.count > 1 { result.hasOverlap = true }
            var pending = [URL(fileURLWithPath: root, isDirectory: true)]
            func mark(_ path: String, _ state: String) {
                for index in members where result.rows[index].locations.contains(where: { contains($0, path) || contains(path, $0) }) {
                    if state == "Partial" || result.rows[index].state == "Measured" { result.rows[index].state = state }
                }
            }
            func count(_ path: String, _ bytes: Int64) {
                let owners = members.filter { index in result.rows[index].locations.contains { contains($0, path) } }
                guard !owners.isEmpty else { return }
                for index in owners { result.rows[index].bytes += bytes }
                for company in Set(owners.map { result.rows[$0].company }) { totals[company, default: 0] += bytes }
                result.totalBytes += bytes
            }
            var rootIsDirectory: ObjCBool = false
            if files.fileExists(atPath: root, isDirectory: &rootIsDirectory), !rootIsDirectory.boolValue {
                do {
                    let attributes = try files.attributesOfItem(atPath: root)
                    if attributes[.type] as? FileAttributeType == .typeRegular {
                        count(root, (attributes[.size] as? NSNumber)?.int64Value ?? 0)
                    } else { mark(root, "Partial") }
                }
                catch { mark(root, "Partial") }
                continue
            }
            while let directory = pending.popLast() {
                try cancellation.check()
                let now = ProcessInfo.processInfo.systemUptime
                if now - lastProgress >= 1 { progress(directory.path); lastProgress = now }
                do {
                    let entries = try files.contentsOfDirectory(at: directory, includingPropertiesForKeys: Array(keys))
                    // Traverse shared trees once, without following nested links into other trees.
                    for entry in entries {
                        try cancellation.check()
                        do {
                            let values = try entry.resourceValues(forKeys: keys)
                            if values.isSymbolicLink == true { mark(entry.path, "Links skipped"); continue }
                            if values.isDirectory == true { pending.append(entry); continue }
                            guard values.isRegularFile == true, let size = values.fileSize else { continue }
                            count(entry.path, Int64(size))
                        } catch is CancellationError { throw CancellationError() }
                        catch { mark(entry.path, "Partial") }
                    }
                } catch is CancellationError { throw CancellationError() }
                catch { mark(directory.path, "Partial") }
            }
        }
        let grouped = Dictionary(grouping: result.rows, by: \.company)
        result.companies = grouped.map { name, rows in
            StorageCompany(name: name, count: rows.count, measuredCount: rows.filter { $0.state == "Measured" }.count,
                           bytes: totals[name, default: 0], availableCount: rows.filter(\.hasSize).count)
        }
        return result
    }

    static func folderToOpen(_ path: String) -> URL? {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) else { return nil }
        return itemToReveal(path)?.deletingLastPathComponent() ?? URL(fileURLWithPath: path)
    }

    static func itemToReveal(_ path: String) -> URL? {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) else { return nil }
        let url = URL(fileURLWithPath: path)
        var ancestor = url
        var package: URL?
        // Reveal the outer package, never launch it or open inside its Contents.
        while ancestor.path != "/" {
            if ["app", "component", "vst3", "vst", "aaxplugin", "bundle", "lunacomponent"].contains(ancestor.pathExtension.lowercased()) { package = ancestor }
            ancestor.deleteLastPathComponent()
        }
        return package ?? (isDirectory.boolValue ? nil : url)
    }
}

enum StorageReport {
    static func companySize(_ company: StorageCompany) -> String {
        company.availableCount > 0 ? size(company.bytes) : "Unavailable"
    }
    static func size(_ bytes: Int64) -> String {
        let gib = bytes >= 1_073_741_824
        return String(format: "%.2f %@", Double(bytes) / (gib ? 1_073_741_824 : 1_048_576), gib ? "GiB" : "MiB")
    }
    static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }
    static func html(_ result: StorageResult) -> String {
        var html = """
        <!doctype html><html lang="en"><meta charset="utf-8"><title>Native Serials Storage</title>
        <style>body{font:1rem system-ui;margin:2rem}table{border-collapse:collapse}th,td{border:1px solid #777;padding:.4rem;text-align:left;overflow-wrap:anywhere}caption{text-align:left;font-weight:bold}</style>
        <h1>Storage by Company</h1><p>Snapshot: \(escape(Date().description)). File sizes, not reclaimable space. Shared folders count once per company and once overall; company totals may overlap. Nested links are skipped. Hard-linked files count under each filename. Missing and unreadable content is excluded or partial.</p>
        <p>Overall measured total: \(size(result.totalBytes)).</p>
        """
        for company in result.companies.sorted(by: { $0.bytes > $1.bytes }) {
            html += "<h2>\(escape(company.name))</h2><p>\(company.count) registered products; \(company.measuredCount) fully measured. Company total: \(companySize(company)).</p><table><caption>\(escape(company.name))</caption><thead><tr><th scope=\"col\">Product</th><th scope=\"col\">Size</th><th scope=\"col\">Status</th><th scope=\"col\">Location</th></tr></thead><tbody>"
            for row in result.rows.filter({ $0.company == company.name }).sorted(by: { $0.bytes > $1.bytes }) {
                html += "<tr><td>\(escape(row.name))</td><td>\(row.hasSize ? size(row.bytes) : "Unavailable")</td><td>\(escape(row.state))</td><td>\(escape(row.locationSummary))</td></tr>"
            }
            html += "</tbody></table>"
        }
        return html + "</html>"
    }
}
