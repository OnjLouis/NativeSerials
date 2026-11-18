import AppKit
import Foundation

struct FolderAction: Sendable, Equatable {
    var title: String
    var folder: String
    var item: String = ""
}

enum FolderActions {
    static func registered(_ product: NativeProduct) -> [String] {
        let roots = StorageSearchRoots.system(for: [product])
        return StorageDiscovery.registeredRoots(product).map(StorageDiscovery.canonical).filter {
            $0.hasPrefix("/") && FileManager.default.fileExists(atPath: $0) && !StorageDiscovery.isBroad($0, search: roots)
        }
    }

    static func discover(_ products: [NativeProduct], cancellation: StorageCancellation) throws -> [NativeProduct: [String]] {
        let found = try StorageDiscovery.discover(products, roots: StorageSearchRoots.system(for: products), cancellation: cancellation)
        var result: [NativeProduct: [String]] = [:]
        for (index, product) in products.enumerated() {
            try cancellation.check()
            result[product] = Array(Set(registered(product) + found[index]))
        }
        return result
    }

    static func forPaths(_ paths: [String], content: String = "") -> [FolderAction] {
        var actions: [FolderAction] = []
        for path in paths {
            guard let folder = StorageScanner.folderToOpen(path) else { continue }
            let parts = path.lowercased().split(separator: "/")
            let title: String
            if parts.contains(where: { $0.hasSuffix(".vst3") }) { title = "Open VST3 Folder" }
            else if parts.contains(where: { $0.hasSuffix(".component") }) { title = "Open AU Folder" }
            else if parts.contains(where: { $0.hasSuffix(".aaxplugin") }) { title = "Open AAX Folder" }
            else if parts.contains(where: { $0.hasSuffix(".vst") }) { title = "Open VST2 Folder" }
            else if path == content { title = "Open Content Folder" }
            else if parts.contains(where: { $0.hasSuffix(".app") }) || path.hasPrefix("/Applications/") { title = "Open Application Folder" }
            else if parts.contains("preferences") { title = "Open Preferences Folder" }
            else { title = "Open Support Folder" }
            let action = FolderAction(title: title, folder: folder.path, item: StorageScanner.itemToReveal(path)?.path ?? "")
            if action.item.isEmpty && actions.contains(where: { $0.title == title && $0.folder == folder.path }) { continue }
            if !action.item.isEmpty { actions.removeAll { $0.title == title && $0.folder == folder.path && $0.item.isEmpty } }
            if !actions.contains(action) { actions.append(action) }
        }
        return actions.sorted { $0.title == $1.title ? $0.folder < $1.folder : $0.title < $1.title }
    }

    @MainActor
    static func populate(_ menu: NSMenu, actions: [FolderAction], target: AnyObject, selector: Selector) {
        for title in Set(actions.map(\.title)).sorted() {
            let choices = actions.filter { $0.title == title }
            if choices.count == 1 {
                let item = NSMenuItem(title: title, action: selector, keyEquivalent: "")
                item.target = target; item.representedObject = choices[0]
                menu.addItem(item)
            } else {
                let parent = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                let submenu = NSMenu(title: title)
                for choice in choices {
                    let item = NSMenuItem(title: choice.item.isEmpty ? choice.folder : choice.item, action: selector, keyEquivalent: "")
                    item.target = target; item.representedObject = choice
                    submenu.addItem(item)
                }
                parent.submenu = submenu; menu.addItem(parent)
            }
        }
        if actions.isEmpty { let item = NSMenuItem(title: "No product folders available", action: nil, keyEquivalent: ""); item.isEnabled = false; menu.addItem(item) }
    }

    @MainActor
    static func open(_ path: String) -> Bool {
        guard let folder = StorageScanner.folderToOpen(path) else { return false }
        if let item = StorageScanner.itemToReveal(path) {
            NSWorkspace.shared.activateFileViewerSelecting([item])
            return true
        }
        return NSWorkspace.shared.open(folder)
    }

    @MainActor
    static func open(_ action: FolderAction) -> Bool {
        if !action.item.isEmpty && FileManager.default.fileExists(atPath: action.item) { return open(action.item) }
        return open(action.folder)
    }
}
