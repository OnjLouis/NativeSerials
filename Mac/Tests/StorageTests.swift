import Foundation

@main
struct StorageTests {
    static func check(_ condition: @autoclosure () -> Bool, _ message: String = "", line: UInt = #line) {
        if !condition() { print("Storage test failed at line \(line): \(message)"); fflush(stdout); exit(1) }
    }
    static func product(_ name: String, _ company: String, _ path: String) -> NativeProduct {
        NativeProduct(key: name, name: name, upid: "", snpid: "", company: company, productType: "", contentVersion: "",
                      contentDir: path, installDir: "", serial: "", hasRas3Token: false, isOwnedCandidate: true, sources: [])
    }
    static func scan(_ products: [NativeProduct], cancellation: StorageCancellation = StorageCancellation()) throws -> StorageResult {
        try StorageScanner.scan(products, cancellation: cancellation, searchRoots: StorageSearchRoots())
    }
    static func main() {
        do { try run() }
        catch { fputs("\(error)\n", stderr); exit(1) }
    }
    static func run() throws {
        check(ProductSerialFilter.all.includes("") && ProductSerialFilter.all.includes("fixture"))
        check(ProductSerialFilter.withSerials.includes("fixture") && !ProductSerialFilter.withSerials.includes(""))
        check(ProductSerialFilter.withoutSerials.includes("") && !ProductSerialFilter.withoutSerials.includes("fixture"))
        let player = product("SynthMaster Player", "", "")
        let playerOwner = NativeScanner().enrichCompanies([player], metadata: [product("SynthMaster 2", "KV331 Audio", "")])
        check(playerOwner[0].company == "KV331 Audio", "Exact legacy product attribution needs supporting manufacturer metadata")
        let unknownPlayer = NativeScanner().enrichCompanies([player], metadata: [product("Different product", "Other", "")])
        check(unknownPlayer[0].company.isEmpty, "Do not attribute an unsupported name from a broad family guess")
        let candidate = product("Maker-Synth", "", "")
        var metadata = product("Synth", "Maker", "")
        metadata.registryName = "Maker-Synth"
        let enriched = NativeScanner().enrichCompanies([candidate], metadata: [metadata, product("Catalog only", "Unused", "")])
        check(enriched.count == 1 && enriched[0].company == "Maker")
        check(StorageScanner.companyName("Fracture Sounds - Blueprint") == "Fracture Sounds")
        let fm = FileManager.default
        let root = fm.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent("NativeSerials-storage-\(UUID())")
        try fm.createDirectory(at: root.appendingPathComponent("Nested"), withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: root) }
        try Data(repeating: 0, count: 10).write(to: root.appendingPathComponent("base.bin"))
        try Data(repeating: 0, count: 20).write(to: root.appendingPathComponent("Nested/sample.bin"))
        try fm.createDirectory(at: root.appendingPathComponent("Empty"), withIntermediateDirectories: true)
        let products = [product("Parent", "Alpha", root.path), product("Child", "Alpha", root.appendingPathComponent("Nested").path),
                        product("Shared", "Beta", root.path), product("Offline", "Beta", root.appendingPathComponent("Absent").path),
                        product("No location", "", ""), product("Empty", "Beta", root.appendingPathComponent("Empty").path)]
        let result = try scan(products)
        if result.totalBytes != 30 {
            print("Unexpected fixture total: \(result.totalBytes); \(result.rows)")
            fflush(stdout)
        }
        check(result.totalBytes == 30)
        check(result.rows[0].bytes == 30 && result.rows[1].bytes == 20)
        check(result.companies.first { $0.name == "Alpha" }?.bytes == 30)
        check(result.companies.first { $0.name == "Beta" }?.bytes == 30)
        check(result.rows[3].state == "Unavailable" && result.rows[4].state == "No location")
        check(result.rows[5].bytes == 0 && result.rows[5].state == "Measured")
        check(result.hasOverlap)
        let content = root.appendingPathComponent("MultiContent")
        let install = root.appendingPathComponent("MultiInstall")
        try fm.createDirectory(at: content, withIntermediateDirectories: true)
        try fm.createDirectory(at: install, withIntermediateDirectories: true)
        try Data(repeating: 0, count: 11).write(to: content.appendingPathComponent("content.bin"))
        try Data(repeating: 0, count: 17).write(to: install.appendingPathComponent("application.bin"))
        var multi = product("Multi", "Maker", content.path)
        multi.installDir = install.path
        let combined = try scan([multi])
        guard combined.totalBytes == 28 else { throw NSError(domain: "StorageTests", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "Content and installation must both count; got \(combined.totalBytes), expected 28"] ) }
        try fm.removeItem(at: content)
        try fm.removeItem(at: install)
        let html = StorageReport.html(result)
        check(html.contains("<caption>Alpha</caption>") && html.contains("scope=\"col\"") && !html.contains("Serial:"))
        let cancellation = StorageCancellation()
        cancellation.cancel()
        do { _ = try scan(products, cancellation: cancellation); preconditionFailure("Cancellation ignored") }
        catch is CancellationError { }
        try fm.createSymbolicLink(at: root.appendingPathComponent("Alias"), withDestinationURL: root)
        let linked = try scan(products)
        check(linked.totalBytes == 30 && linked.rows[0].state == "Links skipped")
        let alias = try scan([product("Alias", "Gamma", root.appendingPathComponent("Alias").path)],
                                           cancellation: StorageCancellation())
        check(alias.totalBytes == 30)
        let unsafe = try scan([product("Root", "A&B", "/"), product("Relative", "A&B", "relative"),
                                             product("Escaped <name>", "A&B", "")], cancellation: StorageCancellation())
        check(unsafe.rows[0].state == "Unsafe broad location" && unsafe.rows[1].state == "Unavailable")
        check(StorageReport.html(unsafe).contains("Escaped &lt;name&gt;"))
        let homeRoot = try scan([product("Home", "Maker", fm.homeDirectoryForCurrentUser.path)])
        check(homeRoot.rows[0].state == "Unsafe broad location" && homeRoot.totalBytes == 0)
        try discoveryFixtures(root.appendingPathComponent("Discovery"))
        try vendorFixtures(root.appendingPathComponent("Vendors"))
        print("Storage tests passed.")
    }

    static func discoveryFixtures(_ root: URL) throws {
        let fm = FileManager.default
        func write(_ path: String, _ size: Int) throws -> String {
            let file = root.appendingPathComponent(path)
            try fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(repeating: 0, count: size).write(to: file)
            return file.deletingLastPathComponent().path
        }
        func bundle(_ path: String, _ publisher: String) throws -> Int64 {
            let info: [String: String] = ["CFBundleIdentifier": "com.\(publisher).synth", "CFBundleName": "Synth"]
            let data = try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            _ = try write(path + "/Contents/payload.bin", 100)
            try data.write(to: root.appendingPathComponent(path + "/Contents/Info.plist"))
            return Int64(data.count + 100)
        }
        let content = try write("Content/content.bin", 11)
        let au = try bundle("Plugins/Synth.component", "maker")
        let vst = try bundle("Plugins/Maker/Synth.vst3", "maker")
        let app = try bundle("Apps/Maker/Synth.app", "maker")
        let aax = try bundle("Plugins/Synth.aaxplugin", "maker")
        _ = try bundle("Plugins/Other/Synth.vst3", "notmaker")
        _ = try write("Support/Maker/Synth/data.bin", 37)
        _ = try write("Support/Other/Synth/data.bin", 71)
        _ = try write("Support/com.maker.synth/cache.bin", 13)
        _ = try write("Support/com.notmaker.synth/cache.bin", 91)
        _ = try write("Support/Maker/Synth Extra/data.bin", 97)
        _ = try write("Support/com.maker.synth.plist", 19)
        var synth = product("Synth", "Maker", content)
        synth.installDir = root.appendingPathComponent("Plugins").path
        let roots = StorageSearchRoots(plugins: [synth.installDir], applications: [root.appendingPathComponent("Apps").path],
                                       data: [root.appendingPathComponent("Support").path])
        let measured = try StorageScanner.scan([synth, product("Shared", "Maker", content)],
                                               cancellation: StorageCancellation(), searchRoots: roots)
        let expected = 11 + au + vst + app + aax + 37 + 13 + 19
        check(measured.totalBytes == expected && measured.rows[0].bytes == expected)
        check(measured.companies[0].bytes == expected, "Shared content must count once for a company")
        check(measured.rows[0].locations.count == 8 && !measured.rows[0].locationSummary.contains("Other") && !measured.rows[0].locationSummary.contains("Extra"))
        let actions = FolderActions.forPaths(measured.rows[0].locations, content: StorageDiscovery.canonical(content))
        for name in ["Open AU Folder", "Open VST3 Folder", "Open AAX Folder", "Open Application Folder", "Open Content Folder"] {
            check(actions.contains { $0.title == name }, "Missing folder action: \(name)")
        }
        check(actions.allSatisfy { FileManager.default.fileExists(atPath: $0.folder) && !$0.folder.hasSuffix(".app") && !$0.folder.hasSuffix(".aaxplugin") })
        check(actions.first { $0.title == "Open AU Folder" }?.item == StorageDiscovery.canonical(root.appendingPathComponent("Plugins/Synth.component").path))
        check(actions.first { $0.title == "Open Content Folder" }?.item == "")
        let auPath = root.appendingPathComponent("Plugins/Synth.component").path
        check(StorageScanner.itemToReveal(auPath + "/Contents/payload.bin")?.path == auPath)
        let auActions = FolderActions.forPaths([auPath, auPath, root.appendingPathComponent("Plugins/Maker/Synth.vst3").path])
        check(auActions.count == 2 && auActions.allSatisfy { !$0.item.isEmpty })
        check(StorageReport.html(measured).contains("Synth.app"), "All counted locations must be reportable")
        let generic = try StorageScanner.scan([product("No matching product", "Maker", synth.installDir)],
                                              cancellation: StorageCancellation(), searchRoots: roots)
        check(generic.totalBytes == 0 && generic.rows[0].state == "Unsafe broad location")
        var nested = product("Nested", "Maker", content)
        nested.installDir = content + "/content.bin"
        let nestedResult = try scan([nested])
        check(nestedResult.totalBytes == 11, "A file inside a counted folder must not count twice")
        check(StorageScanner.folderToOpen(nested.installDir)?.path == content)
        let appPath = root.appendingPathComponent("Apps/Maker/Synth.app").path
        check(StorageScanner.folderToOpen(appPath)?.path == root.appendingPathComponent("Apps/Maker").path)
        nested.contentDir = root.appendingPathComponent("Offline").path
        let offlineResult = try scan([nested])
        check(offlineResult.rows[0].state == "Partial")
        try fm.createDirectory(atPath: nested.contentDir, withIntermediateDirectories: true)
        let reconnectedResult = try scan([nested])
        check(reconnectedResult.rows[0].state == "Measured", "Reconnected folders must recover on rescan")
        for _ in 0..<20 {
            let repeatResult = try StorageScanner.scan([synth], cancellation: StorageCancellation(), searchRoots: roots)
            check(repeatResult.totalBytes == expected && repeatResult.rows[0].locations.count == 8,
                         "Repeated scans must not retain old locations or accumulate counts")
        }
    }

    static func vendorFixtures(_ root: URL) throws {
        let fm = FileManager.default
        func file(_ path: String, _ bytes: Int) throws -> String {
            let url = root.appendingPathComponent(path)
            try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(repeating: 0, count: bytes).write(to: url)
            return url.deletingLastPathComponent().path
        }
        func bundle(_ path: String, _ id: String) throws -> String {
            _ = try file(path + "/Contents/payload", 17)
            let data = try PropertyListSerialization.data(fromPropertyList: ["CFBundleIdentifier": id], format: .xml, options: 0)
            try data.write(to: root.appendingPathComponent(path + "/Contents/Info.plist"))
            return root.appendingPathComponent(path).path
        }
        let nks = try file("Libraries/Tape Percussion/NKS/preset", 3)
        let library = try file("Libraries/Tape Percussion/Samples/sample", 29)
        let tape = product("Tape Percussion", "SA Recordings", nks)
        check(StorageDiscovery.contentLocation(tape) == URL(fileURLWithPath: library).deletingLastPathComponent().path)
        let tapeResult = try scan([tape])
        check(tapeResult.totalBytes == 32, "NKS registration must include its verified product library")
        let generic = product("Different product", "SA Recordings", nks)
        check(StorageDiscovery.registeredRoots(generic) == [nks], "Never expand an unrelated NKS parent")
        let packageNKS = try file("Support/Universal Audio/Plug-Ins/uaudio_ravel.lunacomponent/extras/nks/preset", 5)
        _ = try file("Support/Universal Audio/Plug-Ins/uaudio_ravel.lunacomponent/gui.bundle/resource", 31)
        let externalPackage = try file("External/Universal Audio/Plug-Ins/uaudio_ravel.lunacomponent/Samples/sample", 101)
        let ua = product("Ravel Grand Piano", "Universal Audio (UADx)", packageNKS)
        let uaPlugin = try bundle("Plugins/Ravel.vst3", "com.uaudio.instrument.ravel")
        let bxPlugin = try bundle("Plugins/bx_clipper.component", "com.plugin-alliance.bxclipper")
        let wrong = try bundle("Plugins/Other/bx_clipper.vst3", "com.notpluginalliance.bxclipper")
        let bx = product("Brainworx-bx_clipper", "Brainworx", "")
        let waveBundle = try bundle("Apps/Waves/Plug-Ins V13/C1.bundle", "com.WavesAudio.C1.13.0.0")
        let xmlPath = root.appendingPathComponent("Apps/Waves/Plug-Ins V13/C1.bundle/Contents/Resources/ProcessXML/1001.xml")
        try fm.createDirectory(at: xmlPath.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("<ComponentDefinitions><SubComponentType Name=\"C1 comp-gate\">'COMM'</SubComponentType></ComponentDefinitions>".utf8).write(to: xmlPath)
        let shell = try bundle("Plugins/WaveShell.vst3", "com.WavesAudio.WaveShell1-VST3.14.0")
        let waves = product("Waves-C1 comp-gate Stereo", "Waves", "")
        let piano = product("Waves-Electric Grand 80 Stereo", "Waves", "")
        let samples = try file("External/Waves/Waves Sample Libraries/ElectricG80 HD/sample", 211)
        let otherSamples = try file("External/Other/Waves Sample Libraries/ElectricG80 HD/sample", 307)
        let roots = StorageSearchRoots(plugins: [root.appendingPathComponent("Plugins").path], applications: [root.appendingPathComponent("Apps").path], data: [root.appendingPathComponent("Support").path, root.appendingPathComponent("External").path])
        let found = try StorageDiscovery.discover([ua, bx, waves, piano], roots: roots, cancellation: StorageCancellation())
        func has(_ index: Int, _ path: String) -> Bool { found[index].map(StorageDiscovery.canonical).contains(StorageDiscovery.canonical(path)) }
        check(has(0, uaPlugin) && has(0, URL(fileURLWithPath: externalPackage).deletingLastPathComponent().path), "UAD package and plug-in locations")
        check(has(1, bxPlugin) && !has(1, wrong))
        check(has(2, waveBundle) && has(2, shell), "Waves metadata must match channel variants and shared shells")
        check(has(3, samples) && !has(3, otherSamples), "Known-vendor sample tiers need exact ownership")
        let repeated = try StorageScanner.scan([waves, waves], cancellation: StorageCancellation(), searchRoots: roots)
        check(repeated.totalBytes == repeated.rows[0].bytes && repeated.companies[0].bytes == repeated.totalBytes, "Shared wrappers must count once")
        let unknown = product("Brainworx-bx_clipper", "", "")
        var metadata = bx
        metadata.name = "Different product"
        metadata.registryName = "Brainworx-bx_other"
        let enriched = NativeScanner().enrichCompanies([unknown], metadata: [metadata])
        check(enriched[0].company == "Brainworx", "Authoritative registration prefix must enrich missing company")
        metadata.company = "Other"
        metadata.name = unknown.name
        let ambiguous = NativeScanner().enrichCompanies([unknown], metadata: [bx, metadata])
        check(ambiguous[0].company.isEmpty, "Conflicting manufacturer evidence must stay unknown")
        let auInfo: [String: Any] = ["AudioComponents": [["name": "Declared Publisher: Example Synth"], ["name": "Unqualified name"]]]
        let auRoot = root.appendingPathComponent("AU Metadata")
        let plist = auRoot.appendingPathComponent("UnrelatedFilename.component/Contents/Info.plist")
        try fm.createDirectory(at: plist.deletingLastPathComponent(), withIntermediateDirectories: true)
        try PropertyListSerialization.data(fromPropertyList: auInfo, format: .xml, options: 0).write(to: plist)
        let declarations = NativeScanner().audioUnitPublisherMetadata(in: [auRoot])
        check(declarations.count == 1 && declarations[0].company == "Declared Publisher")
        let declared = NativeScanner().enrichCompanies([product("Declared Publisher-Example Synth", "", "")], metadata: declarations)
        check(declared.count == 1 && declared[0].company == "Declared Publisher", "AU metadata enriches a registration without adding products or inspecting folder names")
        let unrelated = NativeScanner().enrichCompanies([product("UnrelatedFilename", "", "")], metadata: declarations)
        check(unrelated[0].company.isEmpty, "Bundle filename alone is not publisher evidence")
    }
}
