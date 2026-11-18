import Foundation
import Darwin

private final class WavesComponentNames: NSObject, XMLParserDelegate {
    var names: [String] = []
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes: [String: String]) {
        if elementName == "SubComponentType", let name = attributes["Name"], !name.isEmpty { names.append(name) }
    }
    static func read(_ bundle: URL) -> [String] {
        let directory = bundle.appendingPathComponent("Contents/Resources/ProcessXML")
        let files = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        var result: [String] = []
        // Read only small vendor descriptors, without loading plug-in executable code.
        for file in files.filter({ $0.pathExtension.lowercased() == "xml" }).prefix(32) {
            guard let size = try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize, size <= 1_048_576,
                  let data = try? Data(contentsOf: file) else { continue }
            let delegate = WavesComponentNames()
            let parser = XMLParser(data: data)
            parser.shouldResolveExternalEntities = false
            parser.delegate = delegate
            if parser.parse() { result += delegate.names }
        }
        return result
    }
}

struct StorageSearchRoots: Sendable {
    var plugins: [String] = []
    var applications: [String] = []
    var data: [String] = []

    static func system(for products: [NativeProduct]) -> StorageSearchRoots {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        var roots = StorageSearchRoots(
            plugins: ["/Library/Audio/Plug-Ins/Components", "/Library/Audio/Plug-Ins/VST3", "/Library/Audio/Plug-Ins/VST",
                      "/Library/Application Support/Avid/Audio/Plug-Ins", home + "/Library/Application Support/Avid/Audio/Plug-Ins",
                      home + "/Library/Audio/Plug-Ins/Components", home + "/Library/Audio/Plug-Ins/VST3", home + "/Library/Audio/Plug-Ins/VST"],
            applications: ["/Applications", home + "/Applications"],
            data: ["/Library/Application Support", home + "/Library/Application Support", "/Users/Shared",
                   home + "/Library/Caches", home + "/Library/Preferences"])
        roots.plugins += products.map(\.installDir).filter { StorageDiscovery.isPluginRoot($0) }
        roots.plugins = Array(Set(roots.plugins))
        let externalVolumes = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: [.volumeIsLocalKey], options: []) ?? []
        roots.data += externalVolumes.filter { $0.path.hasPrefix("/Volumes/") && (try? $0.resourceValues(forKeys: [.volumeIsLocalKey]).volumeIsLocal) == true }.map(\.path)
        return roots
    }
}

enum StorageDiscovery {
    private static let maximumDepth = 3
    private static let bundleExtensions: Set<String> = ["app", "component", "vst3", "vst", "aaxplugin", "bundle", "lunacomponent"]
    private static let legalSuffixes: Set<String> = ["inc", "llc", "ltd", "limited", "gmbh", "srl"]
    private static let pluginRootNames: Set<String> = ["vst", "vst2", "vst3", "vstplugins", "components", "plugins"]

    static func normalize(_ value: String) -> String {
        String(value.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
            .filter { $0.isLetter || $0.isNumber })
    }

    static func publisherLabels(_ company: String) -> [String] {
        var labels = [company]
        if let bracket = company.firstIndex(of: "(") { labels.append(String(company[..<bracket]).trimmingCharacters(in: .whitespaces)) }
        // Manufacturer names and their distribution/bundle namespaces can differ.
        switch normalize(company) {
        case "brainworx": labels.append("Plugin Alliance")
        case "waves": labels.append("Waves Audio")
        case "universalaudio", "universalaudiouadx": labels += ["Universal Audio", "uaudio", "UADx"]
        default: break
        }
        return labels
    }

    static func registeredRoots(_ product: NativeProduct) -> [String] {
        var roots = [product.contentDir, product.installDir].filter { !$0.isEmpty }
        let content = URL(fileURLWithPath: product.contentDir)
        guard product.contentDir.hasPrefix("/"), content.lastPathComponent.lowercased() == "nks" else { return roots }
        let parent = content.deletingLastPathComponent()
        let package = parent.deletingLastPathComponent()
        if parent.lastPathComponent.lowercased() == "extras" && package.pathExtension.lowercased() == "lunacomponent" {
            roots.append(package.path)
        } else {
            let name = normalize(parent.lastPathComponent)
            if name == normalize(product.name) || (!product.registryName.isEmpty && name == normalize(product.registryName)) {
                if name != normalize(product.company) { roots.append(parent.path) }
            }
        }
        return roots
    }

    static func contentLocation(_ product: NativeProduct) -> String {
        registeredRoots(product).first { $0 != product.contentDir && $0 != product.installDir } ?? product.contentDir
    }

    static func isPluginRoot(_ path: String) -> Bool {
        path.hasPrefix("/") && pluginRootNames.contains(normalize(URL(fileURLWithPath: path).lastPathComponent))
    }

    static func isBroad(_ path: String, search: StorageSearchRoots) -> Bool {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        if ["/", "/Volumes", "/Users", "/Applications", "/Library", "/Library/Audio", "/Library/Audio/Plug-Ins",
            home, home + "/Library", home + "/Library/Audio", home + "/Library/Audio/Plug-Ins"].contains(path) { return true }
        if path.hasPrefix("/Volumes/") && path.split(separator: "/").count == 2 { return true }
        if isPluginRoot(path) { return true }
        return (search.plugins + search.applications + search.data).contains { canonical($0) == path }
    }

    static func canonical(_ path: String) -> String {
        let expanded = (path as NSString).expandingTildeInPath
        guard expanded.hasPrefix("/"), let resolved = realpath(expanded, nil) else { return expanded }
        defer { free(resolved) }
        return String(cString: resolved)
    }

    static func discover(_ products: [NativeProduct], roots: StorageSearchRoots,
                         cancellation: StorageCancellation) throws -> [[String]] {
        var locations = Array(repeating: [String](), count: products.count)
        var aliases: [String: Set<Int>] = [:]
        var publishers: [Set<String>] = []
        var bundleIDs: [String: Set<Int>] = [:]
        for (index, product) in products.enumerated() {
            let company = StorageScanner.companyName(product.company)
            let labels = publisherLabels(company)
            let trimmed = labels.map { label in label.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init).filter { !legalSuffixes.contains($0) }.joined() }
            let vendor = company == "Unknown company" ? Set<String>() : Set((labels + trimmed).map(normalize).filter { !$0.isEmpty })
            publishers.append(vendor)
            var productLabels = [product.name, product.registryName]
            if vendor.contains("waves") {
                productLabels += productLabels.filter { $0.lowercased().hasSuffix(" stereo") || $0.lowercased().hasSuffix(" mono") }.map {
                    String($0[..<($0.lastIndex(of: " ")!)])
                }
            }
            for label in productLabels where !label.isEmpty {
                let key = normalize(label)
                if !key.isEmpty { aliases[key, default: []].insert(index) }
                for prefix in vendor where key.hasPrefix(prefix) && key.count > prefix.count {
                    aliases[String(key.dropFirst(prefix.count)), default: []].insert(index)
                }
            }
            for root in registeredRoots(product) where URL(fileURLWithPath: root).pathExtension.lowercased() == "lunacomponent" {
                let key = normalize(URL(fileURLWithPath: root).deletingPathExtension().lastPathComponent)
                aliases[key, default: []].insert(index)
                for prefix in vendor where key.hasPrefix(prefix) && key.count > prefix.count {
                    aliases[String(key.dropFirst(prefix.count)), default: []].insert(index)
                }
            }
        }
        let knownPublishers = Set(publishers.flatMap { $0 })
        func publisherTokens(_ value: String) -> Set<String> {
            Set([normalize(value)] + value.split(separator: ".").map { normalize(String($0)) })
        }
        func publisherMatches(_ index: Int, _ evidence: Set<String>) -> Bool {
            !publishers[index].isDisjoint(with: evidence)
        }
        let fm = FileManager.default
        var sharedWavesShells: [String] = []
        func walk(_ root: String, dataOnly: Bool) throws {
            var pending: [(URL, Int, Set<String>)] = [(URL(fileURLWithPath: root), 0, [])]
            while let (directory, depth, ancestors) = pending.popLast() {
                try cancellation.check()
                guard let entries = try? fm.contentsOfDirectory(at: directory,
                    includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [.skipsHiddenFiles]) else { continue }
                for entry in entries {
                    try cancellation.check()
                    guard let values = try? entry.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { continue }
                    let ext = entry.pathExtension.lowercased()
                    let name = values.isDirectory == true && !bundleExtensions.contains(ext)
                        ? entry.lastPathComponent : entry.deletingPathExtension().lastPathComponent
                    let key = normalize(name)
                    let folderTokens = publisherTokens(entry.lastPathComponent)
                    let evidence = ancestors.union(folderTokens)
                    let isBundle = bundleExtensions.contains(ext)
                    if !dataOnly && isBundle {
                        let infoURL = entry.appendingPathComponent("Contents/Info.plist")
                        let info = (try? Data(contentsOf: infoURL)).flatMap {
                            (try? PropertyListSerialization.propertyList(from: $0, format: nil)) as? [String: Any]
                        } ?? [:]
                        let identifier = info["CFBundleIdentifier"] as? String ?? ""
                        if identifier.lowercased().hasPrefix("com.wavesaudio.waveshell") { sharedWavesShells.append(entry.path) }
                        let proof = ancestors.union(publisherTokens(identifier))
                        var names = [name, info["CFBundleName"] as? String ?? "", info["CFBundleDisplayName"] as? String ?? ""]
                        if ext == "bundle" && proof.contains("wavesaudio") { names += WavesComponentNames.read(entry) }
                        let candidates = Set(names.flatMap { aliases[normalize($0)] ?? [] })
                        for index in candidates where publisherMatches(index, proof) {
                            locations[index].append(entry.path)
                            if !identifier.isEmpty { bundleIDs[identifier.lowercased(), default: []].insert(index) }
                        }
                        continue
                    }
                    if dataOnly {
                        // Identifier-owned caches/preferences need no fuzzy filename match.
                        var owners = bundleIDs[name.lowercased()] ?? []
                        for index in aliases[key] ?? [] where publisherMatches(index, ancestors) { owners.insert(index) }
                        if ancestors.contains("waves") && directory.lastPathComponent == "Waves Sample Libraries" {
                            var sample = name
                            if sample.hasSuffix(" HD") || sample.hasSuffix(" SD") { sample = String(sample.dropLast(3)) }
                            if sample == "ElectricG80" { sample = "Electric Grand 80" }
                            for index in aliases[normalize(sample)] ?? [] where publishers[index].contains("waves") { owners.insert(index) }
                        }
                        if !owners.isEmpty {
                            for index in owners { locations[index].append(entry.path) }
                            continue
                        }
                    }
                    guard values.isDirectory == true, values.isSymbolicLink != true,
                          depth + 1 < maximumDepth, !isBundle else { continue }
                    if dataOnly && depth == 0 && evidence.isDisjoint(with: knownPublishers) { continue }
                    pending.append((entry, depth + 1, evidence))
                }
            }
        }
        for root in Set(roots.plugins + roots.applications) { try walk(root, dataOnly: false) }
        for root in Set(roots.data) { try walk(root, dataOnly: true) }
        for index in products.indices where publishers[index].contains("waves") && locations[index].contains(where: { $0.lowercased().hasSuffix(".bundle") }) {
            locations[index] += sharedWavesShells
        }
        return locations
    }
}
