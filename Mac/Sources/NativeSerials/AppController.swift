import AppKit
import Foundation

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    private let scanner = NativeScanner()
    private let updates = UpdateService()
    private var window: NSWindow!
    private var table: NSTableView!
    private var statusLabel: NSTextField!
    private var searchField: NSSearchField!
    private var revealButton: NSButton!
    private var serialsOnlyButton: NSButton!
    private var products: [NativeProduct] = []
    private var filteredProducts: [NativeProduct] = []
    private var summary: ScanSummary?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        buildWindow()
        rescan(nil)
        updates.check(currentVersion: appVersion(), manual: false, installSilently: false)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    private func buildMenu() {
        let mainMenu = NSMenu(title: "Native Serials")
        let appItem = NSMenuItem(title: "Native Serials", action: nil, keyEquivalent: "")
        let appMenu = NSMenu(title: "Native Serials")
        appMenu.addItem(NSMenuItem(title: "Rescan", action: #selector(rescan(_:)), keyEquivalent: "r"))
        appMenu.addItem(NSMenuItem(title: "Save HTML Report...", action: #selector(exportHTML(_:)), keyEquivalent: "s"))
        appMenu.addItem(NSMenuItem(title: "Copy Selected Row", action: #selector(copySelectedRow(_:)), keyEquivalent: "c"))
        appMenu.addItem(NSMenuItem(title: "Diagnostics...", action: #selector(showDiagnostics(_:)), keyEquivalent: "d"))
        appMenu.addItem(.separator())
        let manualItem = NSMenuItem(title: "Manual", action: #selector(openManual(_:)), keyEquivalent: "\u{F704}")
        manualItem.keyEquivalentModifierMask = []
        appMenu.addItem(manualItem)
        let updatesItem = NSMenuItem(title: "Check for Updates...", action: #selector(checkForUpdates(_:)), keyEquivalent: "\u{F704}")
        updatesItem.keyEquivalentModifierMask = [.shift]
        appMenu.addItem(updatesItem)
        appMenu.addItem(NSMenuItem(title: "Version History...", action: #selector(openVersionHistory(_:)), keyEquivalent: ""))
        let projectItem = NSMenuItem(title: "Project Page on GitHub", action: #selector(openProjectPage(_:)), keyEquivalent: "\u{F704}")
        projectItem.keyEquivalentModifierMask = [.command]
        appMenu.addItem(projectItem)
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Contact", action: #selector(openContactPage(_:)), keyEquivalent: ""))
        appMenu.addItem(NSMenuItem(title: "Donate", action: #selector(openDonatePage(_:)), keyEquivalent: ""))
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Quit Native Serials", action: #selector(quit(_:)), keyEquivalent: "q"))
        for item in appMenu.items { item.target = self }
        appItem.submenu = appMenu
        mainMenu.addItem(appItem)
        NSApp.mainMenu = mainMenu
    }

    private func buildWindow() {
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1100, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Native Serials"
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
        searchField.setAccessibilityHelp("Type a product name, company, serial, path, UPID, or SNPID to filter the list.")

        let rescanButton = NSButton(title: "Rescan", target: self, action: #selector(rescan(_:)))
        rescanButton.setAccessibilityLabel("Rescan")

        revealButton = NSButton(checkboxWithTitle: "Reveal serials", target: self, action: #selector(toggleReveal(_:)))
        revealButton.setAccessibilityLabel("Reveal serials")
        revealButton.setAccessibilityHelp("When checked, serial numbers are shown in the table and included in copied rows.")

        serialsOnlyButton = NSButton(checkboxWithTitle: "Serials only", target: self, action: #selector(toggleSerialsOnly(_:)))
        serialsOnlyButton.setAccessibilityLabel("Serials only")
        serialsOnlyButton.setAccessibilityHelp("When checked, the product list only shows rows with a recovered serial number.")

        let exportButton = NSButton(title: "Save HTML Report", target: self, action: #selector(exportHTML(_:)))
        exportButton.setAccessibilityLabel("Save HTML report")
        exportButton.setAccessibilityHelp("Save the current filtered product list as an HTML report.")

        controls.addArrangedSubview(searchField)
        controls.addArrangedSubview(rescanButton)
        controls.addArrangedSubview(revealButton)
        controls.addArrangedSubview(serialsOnlyButton)
        controls.addArrangedSubview(exportButton)
        searchField.widthAnchor.constraint(greaterThanOrEqualToConstant: 360).isActive = true

        table = NSTableView()
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.dataSource = self
        table.delegate = self
        table.setAccessibilityLabel("Product serials")
        table.setAccessibilityHelp("Installed products, libraries, and local Native Access license tokens. Serials are hidden until Reveal serials is checked.")
        addColumn("name", "Product", 240)
        addColumn("serial", "Serial", 210)
        addColumn("version", "Version", 90)
        addColumn("company", "Company", 140)
        addColumn("type", "Type", 100)
        addColumn("upid", "UPID", 260)
        addColumn("path", "Path", 360)

        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true

        statusLabel = NSTextField(labelWithString: "Ready")
        statusLabel.setAccessibilityLabel("Status")

        root.addArrangedSubview(controls)
        root.addArrangedSubview(scroll)
        root.addArrangedSubview(statusLabel)
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 420).isActive = true

        window.contentView = root
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor),
            root.topAnchor.constraint(equalTo: window.contentView!.topAnchor),
            root.bottomAnchor.constraint(equalTo: window.contentView!.bottomAnchor)
        ])
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
        let result = scanner.scan()
        products = result.products
        summary = result.summary
        applyFilter()
        updateStatus()
    }

    @objc private func toggleReveal(_ sender: Any?) {
        table.reloadData()
        updateStatus()
    }

    @objc private func toggleSerialsOnly(_ sender: Any?) {
        applyFilter()
    }

    func controlTextDidChange(_ obj: Notification) {
        applyFilter()
    }

    private func applyFilter() {
        let query = searchField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let serialsOnly = serialsOnlyButton.state == .on
        filteredProducts = products.filter { product in
            if serialsOnly && product.serial.isEmpty { return false }
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
        let text = diagnosticsText()
        let alert = NSAlert()
        alert.messageText = "Native Serials Diagnostics"
        alert.informativeText = text
        alert.addButton(withTitle: "Copy")
        alert.addButton(withTitle: "Close")
        if alert.runModal() == .alertFirstButtonReturn {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
    }

    @objc private func checkForUpdates(_ sender: Any?) {
        updates.check(currentVersion: appVersion(), manual: true, installSilently: false)
    }

    @objc private func openManual(_ sender: Any?) {
        guard let url = Bundle.main.url(forResource: "Manual", withExtension: "html") else {
            showError("Manual not found", "Manual.html was not found inside the app bundle.")
            return
        }
        NSWorkspace.shared.open(url)
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
            "UPID: \(product.upid)",
            "SNPID: \(product.snpid)",
            "Content: \(product.contentDir)",
            "Install: \(product.installDir)"
        ].joined(separator: "\t")
    }

    private func htmlReport(includeSerials: Bool) -> String {
        let rows = filteredProducts.map { product in
            "<tr><td>\(escape(product.name))</td><td>\(escape(includeSerials ? product.serial : displaySerial(product.serial)))</td><td>\(escape(product.contentVersion))</td><td>\(escape(product.company))</td><td>\(escape(product.productType))</td><td>\(escape(product.upid))</td><td>\(escape(product.contentDir.nilIfEmpty ?? product.installDir))</td></tr>"
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
        <thead><tr><th>Product</th><th>Serial</th><th>Version</th><th>Company</th><th>Type</th><th>UPID</th><th>Path</th></tr></thead>
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
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
    }

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
