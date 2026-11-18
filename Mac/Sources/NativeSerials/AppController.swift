import AppKit
import Foundation

private final class InventoryWindow: NSWindow {
    var leaveStorage: (() -> Void)?
    var keyboardOrder: (() -> [NSView])?
    override func cancelOperation(_ sender: Any?) { leaveStorage?() }
    override func selectNextKeyView(_ sender: Any?) { moveFocus(forward: true) }
    override func selectPreviousKeyView(_ sender: Any?) { moveFocus(forward: false) }
    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown && event.keyCode == 48 && event.modifierFlags.intersection([.command, .control, .option]).isEmpty {
            moveFocus(forward: !event.modifierFlags.contains(.shift))
            return
        }
        super.sendEvent(event)
    }
    private func moveFocus(forward: Bool) {
        guard let order = keyboardOrder?(), !order.isEmpty else { return }
        let current = order.firstIndex { view in
            view === firstResponder || (view as? NSTextField)?.currentEditor() === firstResponder
        } ?? (forward ? order.count - 1 : 0)
        for distance in 1...order.count {
            let index = (current + (forward ? distance : -distance) + order.count) % order.count
            let view = order[index]
            if view.isHiddenOrHasHiddenAncestor || (view as? NSControl)?.isEnabled == false { continue }
            if makeFirstResponder(view) { return }
        }
    }
}

private final class NavigationTabs: NSTabView {
    override var acceptsFirstResponder: Bool { true }
}

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate, NSMenuDelegate, NSTabViewDelegate {
    private let scanner = NativeScanner()
    private let updates = UpdateService()
    private var window: NSWindow!
    private var table: NSTableView!
    private var statusLabel: NSTextField!
    private var searchField: NSSearchField!
    private var revealButton: NSButton!
    private var serialFilterButtons: [NSButton] = []
    private var serialFilter = ProductSerialFilter.all
    private var products: [NativeProduct] = []
    private var filteredProducts: [NativeProduct] = []
    private var summary: ScanSummary?
    private var storageWindow: StorageWindowController?
    private var tabs: NSTabView!
    private var actionsMenu: NSMenu!
    private var folderLocations: [NativeProduct: [String]] = [:]
    private var folderCancellation: StorageCancellation?
    private var inventoryKeyViews: [NSView] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        buildWindow()
        rescan(nil)
        configureInventoryKeys()
        if !filteredProducts.isEmpty { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false) }
        window.makeFirstResponder(table)
        NSAccessibility.post(element: table!, notification: .focusedUIElementChanged)
        updates.check(currentVersion: appVersion(), manual: false, installSilently: false)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
    func applicationWillTerminate(_ notification: Notification) { folderCancellation?.cancel(); storageWindow?.stop() }

    private func buildMenu() {
        let mainMenu = NSMenu(title: "Native Serials")
        let appItem = NSMenuItem(title: "Native Serials", action: nil, keyEquivalent: "")
        let appMenu = NSMenu(title: "Native Serials")
        let helpItem = NSMenuItem(title: "Help", action: nil, keyEquivalent: "")
        let helpMenu = NSMenu(title: "Help")
        appMenu.addItem(NSMenuItem(title: "Rescan", action: #selector(rescan(_:)), keyEquivalent: "r"))
        appMenu.addItem(NSMenuItem(title: "Save HTML Report...", action: #selector(exportHTML(_:)), keyEquivalent: "s"))
        appMenu.addItem(NSMenuItem(title: "Copy Selected Row", action: #selector(copySelectedRow(_:)), keyEquivalent: "c"))
        appMenu.addItem(.separator())
        let hide = NSMenuItem(title: "Hide Native Serials", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        hide.target = NSApp
        appMenu.addItem(hide)
        let contextItem = NSMenuItem(title: "Context Help", action: #selector(showContextHelp(_:)), keyEquivalent: "\u{F704}")
        contextItem.keyEquivalentModifierMask = []
        helpMenu.addItem(contextItem)
        let manualItem = NSMenuItem(title: "Open Manual", action: #selector(openManual(_:)), keyEquivalent: "")
        helpMenu.addItem(manualItem)
        let diagnosticsItem = NSMenuItem(title: "Diagnostics...", action: #selector(showDiagnostics(_:)), keyEquivalent: "\u{F704}")
        diagnosticsItem.keyEquivalentModifierMask = [.option]
        helpMenu.addItem(diagnosticsItem)
        let updatesItem = NSMenuItem(title: "Check for Updates...", action: #selector(checkForUpdates(_:)), keyEquivalent: "\u{F704}")
        updatesItem.keyEquivalentModifierMask = [.shift]
        appMenu.addItem(updatesItem)
        helpMenu.addItem(NSMenuItem(title: "Version History...", action: #selector(openVersionHistory(_:)), keyEquivalent: ""))
        let projectItem = NSMenuItem(title: "Project Page on GitHub", action: #selector(openProjectPage(_:)), keyEquivalent: "\u{F704}")
        projectItem.keyEquivalentModifierMask = [.command]
        helpMenu.addItem(projectItem)
        helpMenu.addItem(.separator())
        helpMenu.addItem(NSMenuItem(title: "Contact", action: #selector(openContactPage(_:)), keyEquivalent: ""))
        helpMenu.addItem(NSMenuItem(title: "Donate", action: #selector(openDonatePage(_:)), keyEquivalent: ""))
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Quit Native Serials", action: #selector(quit(_:)), keyEquivalent: "q"))
        for item in appMenu.items where item.target == nil { item.target = self }
        appItem.submenu = appMenu
        mainMenu.addItem(appItem)
        let windowItem = NSMenuItem(title: "View", action: nil, keyEquivalent: "")
        let windowMenu = NSMenu(title: "View")
        windowMenu.addItem(NSMenuItem(title: "Inventory", action: #selector(showInventory(_:)), keyEquivalent: "1"))
        windowMenu.addItem(NSMenuItem(title: "Storage by Company", action: #selector(showStorage(_:)), keyEquivalent: "2"))
        windowMenu.addItem(NSMenuItem(title: "Find Products", action: #selector(findProducts(_:)), keyEquivalent: "f"))
        windowMenu.addItem(NSMenuItem(title: "Open Selected Folder", action: #selector(openStorageFolder(_:)), keyEquivalent: "o"))
        windowMenu.addItem(NSMenuItem(title: "Close Window", action: #selector(closeCurrentWindow(_:)), keyEquivalent: "w"))
        for item in windowMenu.items { item.target = self }
        windowItem.submenu = windowMenu
        mainMenu.addItem(windowItem)
        let actionsItem = NSMenuItem(title: "Actions", action: nil, keyEquivalent: "")
        actionsMenu = NSMenu(title: "Actions"); actionsMenu.delegate = self
        actionsItem.submenu = actionsMenu; mainMenu.addItem(actionsItem)
        for item in helpMenu.items { item.target = self }
        helpItem.submenu = helpMenu
        mainMenu.addItem(helpItem)
        NSApp.helpMenu = helpMenu
        NSApp.mainMenu = mainMenu
    }

    private func buildWindow() {
        let inventoryWindow = InventoryWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1100, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window = inventoryWindow
        window.autorecalculatesKeyViewLoop = false
        inventoryWindow.leaveStorage = { [weak self] in
            if self?.storageWindow?.isKey == true { self?.showInventory(nil) }
        }
        window.title = "Native Serials"
        window.minSize = NSSize(width: 980, height: 820)
        window.center()
        window.setAccessibilityLabel("Native Serials")

        let root = NSStackView()
        root.orientation = .vertical
        root.spacing = 8
        root.edgeInsets = NSEdgeInsets(top: 10, left: 10, bottom: 10, right: 10)
        root.translatesAutoresizingMaskIntoConstraints = false

        let controls = NSStackView()
        controls.orientation = .horizontal
        controls.spacing = 8

        searchField = NSSearchField()
        searchField.placeholderString = "Search products"
        searchField.delegate = self
        searchField.setAccessibilityLabel("Search products")
        searchField.setAccessibilityHelp("Type a product name, company, serial, path, UPID, or SNPID to filter the list. Command+F.")

        let rescanButton = NSButton(title: "Rescan", target: self, action: #selector(rescan(_:)))
        rescanButton.setAccessibilityLabel("Rescan")
        rescanButton.setAccessibilityHelp("Scan local Native Instruments metadata again. Command+R.")

        revealButton = NSButton(checkboxWithTitle: "Reveal serials", target: self, action: #selector(toggleReveal(_:)))
        revealButton.setAccessibilityLabel("Reveal serials")
        revealButton.setAccessibilityHelp("When checked, serial numbers are shown in the table and included in copied rows.")

        let serialChoices = NSStackView()
        serialChoices.orientation = .horizontal
        serialChoices.spacing = 12
        serialChoices.setAccessibilityLabel("Products shown")
        serialChoices.addArrangedSubview(NSTextField(labelWithString: "Products shown:"))
        let titles = ["All products", "With serials", "Without serials"]
        let help = ["Show every registered product matching Search.", "Show products with a recovered serial, whether serials are revealed or hidden. Search still applies.", "Show products without a recovered serial. Search still applies; a missing serial does not prove that a product is unlicensed."]
        for filter in ProductSerialFilter.allCases {
            let button = NSButton(radioButtonWithTitle: titles[filter.rawValue], target: self, action: #selector(serialFilterChanged(_:)))
            button.tag = filter.rawValue
            button.state = filter == .all ? .on : .off
            button.setAccessibilityLabel(titles[filter.rawValue])
            button.setAccessibilityHelp(help[filter.rawValue])
            serialFilterButtons.append(button)
            serialChoices.addArrangedSubview(button)
        }

        let exportButton = NSButton(title: "Save HTML Report", target: self, action: #selector(exportHTML(_:)))
        exportButton.setAccessibilityLabel("Save HTML report")
        exportButton.setAccessibilityHelp("Save the current filtered product list as an HTML report. Command+S.")

        controls.addArrangedSubview(searchField)
        controls.addArrangedSubview(rescanButton)
        controls.addArrangedSubview(revealButton)
        controls.addArrangedSubview(exportButton)
        searchField.widthAnchor.constraint(greaterThanOrEqualToConstant: 360).isActive = true

        table = PageNavigableTable()
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.dataSource = self
        table.delegate = self
        table.setAccessibilityLabel("Product serials")
        table.setAccessibilityHelp("Installed products, libraries, and local Native Access license tokens. Serials are hidden until Reveal serials is checked. Page Up and Page Down move by a visible page of rows.")
        let copyMenu = NSMenu()
        let copyRow = NSMenuItem(title: "Copy Selected Rows", action: #selector(copyInventoryRows(_:)), keyEquivalent: "c")
        copyRow.target = self
        copyMenu.addItem(copyRow)
        table.menu = copyMenu
        copyMenu.delegate = self
        addColumn("name", "Product", 240)
        addColumn("serial", "Serial", 210)
        addColumn("version", "Version", 90)
        addColumn("company", "Company", 140)
        addColumn("type", "Type", 100)
        addColumn("path", "Path", 360)
        addColumn("upid", "UPID", 260)

        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true

        statusLabel = NSTextField(labelWithString: "Ready")
        statusLabel.setAccessibilityLabel("Status")

        root.addArrangedSubview(controls)
        root.addArrangedSubview(serialChoices)
        root.addArrangedSubview(scroll)
        root.addArrangedSubview(statusLabel)
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 420).isActive = true

        root.translatesAutoresizingMaskIntoConstraints = true
        root.autoresizingMask = [.width, .height]
        tabs = NavigationTabs(frame: window.contentView!.bounds)
        tabs.autoresizingMask = [.width, .height]
        tabs.setAccessibilityLabel("Views")
        let inventoryTab = NSTabViewItem(identifier: "inventory")
        inventoryTab.label = "Inventory"; inventoryTab.view = root
        inventoryTab.initialFirstResponder = searchField
        let storage = StorageWindowController()
        let storageTab = NSTabViewItem(identifier: "storage")
        storageTab.label = "Storage by Company"; storageTab.view = storage.embed(in: window)
        storageTab.initialFirstResponder = storage.initialKeyView
        storage.connectTabHeader(tabs)
        storageWindow = storage
        tabs.addTabViewItem(inventoryTab); tabs.addTabViewItem(storageTab)
        tabs.delegate = self
        window.contentView = tabs
        inventoryKeyViews = [searchField, rescanButton, revealButton, exportButton] + serialFilterButtons + [table, tabs]
        inventoryWindow.keyboardOrder = { [weak self] in
            guard let self else { return [] }
            return self.tabs.selectedTabViewItem?.identifier as? String == "storage"
                ? (self.storageWindow?.focusViews ?? []) + [self.tabs] : self.inventoryKeyViews
        }
        tabs.setAccessibilityHelp("Choose Inventory with Command+1 or Storage by Company with Command+2.")
        window.initialFirstResponder = table
        HelpDialog.prepare(root)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func addColumn(_ id: String, _ title: String, _ width: CGFloat) {
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
        column.title = title
        column.width = width
        table.addTableColumn(column)
    }

    @objc private func rescan(_ sender: Any?) {
        if storageWindow?.isKey == true { storageWindow?.rescan(); return }
        let result = scanner.scan()
        products = result.products
        summary = result.summary
        applyFilter()
        updateStatus()
        loadFolderLocations(products)
    }

    @objc private func toggleReveal(_ sender: Any?) {
        table.reloadData()
        updateStatus()
    }

    @objc private func serialFilterChanged(_ sender: NSButton) {
        guard let filter = ProductSerialFilter(rawValue: sender.tag) else { return }
        serialFilter = filter
        for button in serialFilterButtons { button.state = button === sender ? .on : .off }
        applyFilter()
    }

    func controlTextDidChange(_ obj: Notification) {
        applyFilter()
    }

    private func applyFilter() {
        let query = searchField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        filteredProducts = products.filter { product in
            if !serialFilter.includes(product.serial) { return false }
            if query.isEmpty { return true }
            return [
                product.name,
                product.company,
                product.productType,
                product.upid,
                product.snpid,
                product.serial,
                product.contentDir,
                product.installDir
            ].joined(separator: " ").lowercased().contains(query)
        }
        table.reloadData()
        updateStatus()
    }

    private func updateStatus() {
        guard let summary else {
            statusLabel.stringValue = "Ready"
            return
        }
        let serialText = revealButton.state == .on ? "serials shown" : "serials hidden"
        statusLabel.stringValue = "\(filteredProducts.count) of \(summary.productCount) products. \(summary.serialCount) serials found. \(summary.ras3TokenCount) RAS3 tokens. \(serialText)."
    }

    func numberOfRows(in tableView: NSTableView) -> Int {
        filteredProducts.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row >= 0, row < filteredProducts.count, let tableColumn else { return nil }
        let product = filteredProducts[row]
        let value: String
        switch tableColumn.identifier.rawValue {
        case "name": value = product.name
        case "serial": value = displaySerial(product.serial)
        case "version": value = product.contentVersion
        case "company": value = product.company
        case "type": value = product.productType
        case "upid": value = product.upid
        case "path": value = product.contentDir.nilIfEmpty ?? product.installDir.nilIfEmpty ?? product.sources.first ?? ""
        default: value = ""
        }

        let cell = tableView.makeView(withIdentifier: tableColumn.identifier, owner: self) as? NSTableCellView ?? NSTableCellView()
        cell.identifier = tableColumn.identifier
        let textField = cell.textField ?? NSTextField(labelWithString: "")
        textField.lineBreakMode = .byTruncatingTail
        textField.stringValue = value
        textField.setAccessibilityLabel("\(tableColumn.title): \(value)")
        if cell.textField == nil {
            textField.translatesAutoresizingMaskIntoConstraints = false
            cell.addSubview(textField)
            cell.textField = textField
            NSLayoutConstraint.activate([
                textField.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 4),
                textField.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
                textField.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
            ])
        }
        return cell
    }

    @objc private func copySelectedRow(_ sender: Any?) {
        if let editor = NSApp.keyWindow?.firstResponder as? NSText { editor.copy(sender); return }
        if storageWindow?.isKey == true { storageWindow?.copySelection(); return }
        copyInventoryRows(sender)
    }

    @objc private func copyInventoryRows(_ sender: Any?) {
        let selected = table.selectedRowIndexes.compactMap { index -> NativeProduct? in
            index >= 0 && index < filteredProducts.count ? filteredProducts[index] : nil
        }
        guard !selected.isEmpty else { return }
        let text = selected.map { rowText($0, includeSerial: revealButton.state == .on) }.joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        statusLabel.stringValue = "Copied \(selected.count) row\(selected.count == 1 ? "" : "s") to the clipboard."
    }

    @objc private func exportHTML(_ sender: Any?) {
        if storageWindow?.isKey == true { storageWindow?.exportHTML(); return }
        let panel = NSSavePanel()
        panel.title = "Save Native Serials HTML Report"
        panel.nameFieldStringValue = "NativeSerialsReport.html"
        panel.allowedContentTypes = [.html]
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let url = panel.url, let self else { return }
            do {
                let html = self.htmlReport(includeSerials: self.revealButton.state == .on)
                try html.write(to: url, atomically: true, encoding: .utf8)
                self.statusLabel.stringValue = "Exported HTML report to \(url.path)."
            } catch {
                self.showError("Export failed", error.localizedDescription)
            }
        }
    }

    @objc private func showDiagnostics(_ sender: Any?) {
        if storageWindow?.isKey == true { storageWindow?.showDiagnostics(); return }
        let text = diagnosticsText()
        HelpDialog.show(title: "Native Serials Diagnostics", text: text, copyable: true)
    }

    @objc private func checkForUpdates(_ sender: Any?) {
        updates.check(currentVersion: appVersion(), manual: true, installSilently: false)
    }

    @objc private func openManual(_ sender: Any?) {
        HelpDialog.openManual()
    }

    @objc private func showContextHelp(_ sender: Any?) {
        guard NSApp.modalWindow == nil else { return }
        if storageWindow?.isKey == true { storageWindow?.showHelp(); return }
        let fallback = "Inventory lists locally registered products. Serials are hidden until Reveal serials is checked. Search filters the list. Storage by Company opens folder sizes. Command+2 opens storage; Command+S saves the current inventory. Escape closes this help dialog. Open Manual opens the full guide."
        HelpDialog.showContext(in: window, fallback: fallback)
    }

    @objc private func openVersionHistory(_ sender: Any?) {
        updates.openVersionHistory()
    }

    @objc private func openProjectPage(_ sender: Any?) {
        updates.openProjectPage()
    }

    @objc private func openContactPage(_ sender: Any?) {
        updates.openContactPage()
    }

    @objc private func openDonatePage(_ sender: Any?) {
        updates.openDonatePage()
    }

    @objc private func quit(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func showError(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    private func displaySerial(_ serial: String) -> String {
        guard !serial.isEmpty else { return "" }
        if revealButton.state == .on { return serial }
        let suffix = serial.suffix(4)
        return "hidden, ending \(suffix)"
    }

    private func rowText(_ product: NativeProduct, includeSerial: Bool) -> String {
        [
            "Product: \(product.name)",
            "Serial: \(includeSerial ? product.serial : displaySerial(product.serial))",
            "Version: \(product.contentVersion)",
            "Company: \(product.company)",
            "Type: \(product.productType)",
            "SNPID: \(product.snpid)",
            "Content: \(product.contentDir)",
            "Install: \(product.installDir)",
            "UPID: \(product.upid)"
        ].joined(separator: "\t")
    }

    private func htmlReport(includeSerials: Bool) -> String {
        let rows = filteredProducts.map { product in
            "<tr><td>\(escape(product.name))</td><td>\(escape(includeSerials ? product.serial : displaySerial(product.serial)))</td><td>\(escape(product.contentVersion))</td><td>\(escape(product.company))</td><td>\(escape(product.productType))</td><td>\(escape(product.contentDir.nilIfEmpty ?? product.installDir))</td><td>\(escape(product.upid))</td></tr>"
        }.joined(separator: "\n")
        return """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Native Serials Report</title>
        <style>
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; line-height: 1.4; margin: 2rem; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border: 1px solid #777; padding: .35rem; text-align: left; vertical-align: top; }
        th { background: #eee; }
        </style>
        </head>
        <body>
        <h1>Native Serials Report</h1>
        <p>Generated \(escape(Date().description)). Serials \(includeSerials ? "included" : "masked").</p>
        <table>
        <thead><tr><th>Product</th><th>Serial</th><th>Version</th><th>Company</th><th>Type</th><th>Path</th><th>UPID</th></tr></thead>
        <tbody>
        \(rows)
        </tbody>
        </table>
        </body>
        </html>
        """
    }

    private func diagnosticsText() -> String {
        guard let summary else { return "No scan has run." }
        return """
        Native Serials \(appVersion())
        Products: \(summary.productCount)
        Installed product JSON files: \(summary.installedJsonCount)
        Service Center XML files: \(summary.serviceXmlCount)
        RAS3 token files: \(summary.ras3TokenCount)
        Decoded RAS3 tokens: \(summary.decodedTokenCount)
        Products with serials: \(summary.serialCount)

        Source roots:
        \(summary.sourceRoots.joined(separator: "\n"))
        """
    }

    private func appVersion() -> String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.1"
    }

    @objc private func showInventory(_ sender: Any?) { tabs.selectTabViewItem(withIdentifier: "inventory"); window.makeFirstResponder(table) }
    @objc private func showStorage(_ sender: Any?) {
        tabs.selectTabViewItem(withIdentifier: "storage")
        storageWindow?.show()
    }
    func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        let selectorFocused = window.firstResponder === tabs
        let storage = tabViewItem?.identifier as? String == "storage"
        storageWindow?.setActive(storage)
        if !storage { configureInventoryKeys(); window.makeFirstResponder(table) }
        if selectorFocused { window.makeFirstResponder(tabs) }
    }
    private func configureInventoryKeys() {
        for pair in zip(inventoryKeyViews.dropLast(), inventoryKeyViews.dropFirst()) { pair.0.nextKeyView = pair.1 }
    }

    private func loadFolderLocations(_ snapshot: [NativeProduct]) {
        folderCancellation?.cancel()
        let control = StorageCancellation(); folderCancellation = control
        folderLocations.removeAll()
        Task { [weak self] in
            do {
                let found = try await Task.detached(priority: .utility) { try FolderActions.discover(snapshot, cancellation: control) }.value
                if self?.folderCancellation === control { self?.folderLocations = found }
            } catch { /* Registered locations remain available if additional discovery fails. */ }
            if self?.folderCancellation === control { self?.folderCancellation = nil }
        }
    }
    private func inventoryFolderActions() -> [FolderAction] {
        guard table.selectedRowIndexes.count == 1, filteredProducts.indices.contains(table.selectedRow) else { return [] }
        let product = filteredProducts[table.selectedRow]
        return FolderActions.forPaths(folderLocations[product] ?? FolderActions.registered(product), content: StorageDiscovery.canonical(StorageDiscovery.contentLocation(product)))
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu === actionsMenu { menu.removeAllItems() }
        else { while menu.numberOfItems > 1 { menu.removeItem(at: 1) }; menu.addItem(.separator()) }
        let folders = menu === actionsMenu && storageWindow?.isKey == true ? storageWindow!.selectedFolderActions() : inventoryFolderActions()
        FolderActions.populate(menu, actions: folders, target: self, selector: #selector(openFolderAction(_:)))
    }
    @objc private func openFolderAction(_ sender: NSMenuItem) {
        guard let action = sender.representedObject as? FolderAction else { return }
        if storageWindow?.isKey == true { storageWindow?.openProductFolder(action) }
        else if !FolderActions.open(action) { statusLabel.stringValue = "This folder is unavailable." }
    }
    @objc private func findProducts(_ sender: Any?) {
        if storageWindow?.isKey == true { storageWindow?.focusSearch() }
        else { window.makeFirstResponder(searchField) }
    }
    @objc private func openStorageFolder(_ sender: Any?) { if storageWindow?.isKey == true { storageWindow?.openSelectedFolder() } }
    @objc private func closeCurrentWindow(_ sender: Any?) { NSApp.keyWindow?.performClose(sender) }

    private func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }
}

private extension String {
    var nilIfEmpty: String? {
        isEmpty ? nil : self
    }
}
