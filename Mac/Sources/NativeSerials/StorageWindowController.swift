import AppKit
import Foundation

private final class StorageWindow: NSWindow {
    override func cancelOperation(_ sender: Any?) { close() }
}

@MainActor
final class StorageWindowController: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate, NSWindowDelegate, NSMenuDelegate {
    private(set) var window: NSWindow!
    private var result: StorageResult?
    private var cancellation: StorageCancellation?
    private var companyRows: [StorageCompany] = []
    private var productRows: [StorageRow] = []
    private let companies = PageNavigableTable()
    private let libraries = PageNavigableTable()
    private let ranking = NSPopUpButton()
    private let ordering = NSPopUpButton()
    private let search = NSSearchField()
    private let status = NSTextView()
    private let totals = NSTextView()
    private var scanButton: NSButton!
    private var cancelButton: NSButton!
    private var embedded = false
    private var active = true
    private weak var tabHeader: NSView?

    override init() {
        super.init()
        buildWindow()
    }

    var isKey: Bool { active && window.isKeyWindow }
    var initialKeyView: NSView { ranking }
    var focusViews: [NSView] { [ranking, ordering, search, scanButton, cancelButton, companies, libraries, totals, status] }

    func embed(in host: NSWindow) -> NSView {
        let view = window.contentView!
        window.contentView = nil
        window.delegate = nil
        window.close()
        window = host
        embedded = true; active = false
        view.translatesAutoresizingMaskIntoConstraints = true
        view.autoresizingMask = [.width, .height]
        return view
    }

    func setActive(_ value: Bool) { active = value; if value { show() } }
    func connectTabHeader(_ header: NSView) { tabHeader = header }
    func stop() { cancellation?.cancel() }

    func show() {
        if !embedded { window.makeKeyAndOrderFront(nil) }
        if let header = tabHeader {
            let order: [NSView] = [ranking, ordering, search, scanButton, cancelButton, companies, libraries, totals, status, header]
            for pair in zip(order.dropLast(), order.dropFirst()) { pair.0.nextKeyView = pair.1 }
        }
        window.makeFirstResponder(companies)
        if result == nil && cancellation == nil { rescan() }
    }

    private func buildWindow() {
        window = StorageWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 760),
                               styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Storage by Company - Native Serials"
        window.minSize = NSSize(width: 900, height: 760)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        let root = NSStackView()
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 8
        root.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        root.translatesAutoresizingMaskIntoConstraints = false
        let controls = NSStackView()
        controls.spacing = 8
        ranking.addItems(withTitles: ["Largest first", "Most products", "Company name"])
        ordering.addItems(withTitles: ["Largest first", "Smallest first", "Product name"])
        ranking.target = self; ranking.action = #selector(rankChanged)
        ordering.target = self; ordering.action = #selector(orderChanged)
        ranking.setAccessibilityLabel("Company order")
        ranking.setAccessibilityHelp("Rank companies by total size, product count or name.")
        ordering.setAccessibilityLabel("Product order")
        ordering.setAccessibilityHelp("Sort product sizes numerically or sort by name.")
        search.placeholderString = "Filter products"
        search.delegate = self
        search.setAccessibilityLabel("Filter products")
        search.setAccessibilityHelp("Filter the selected company's products by name or folder. Command+F.")
        search.widthAnchor.constraint(greaterThanOrEqualToConstant: 160).isActive = true
        scanButton = NSButton(title: "Scan", target: self, action: #selector(scanAction))
        scanButton.setAccessibilityHelp("Refresh storage sizes in the background. Command+R.")
        cancelButton = NSButton(title: "Cancel", target: self, action: #selector(cancelAction))
        cancelButton.setAccessibilityHelp("Cancel the current scan and keep previous completed results.")
        cancelButton.isEnabled = false
        for control in [NSTextField(labelWithString: "Rank:"), ranking, NSTextField(labelWithString: "Sort by:"), ordering,
                        search, scanButton!, cancelButton!] as [NSView] { controls.addArrangedSubview(control) }
        root.addArrangedSubview(controls)
        root.addArrangedSubview(NSTextField(labelWithString: "Companies"))
        configure(companies, name: "Companies", help: "Select a company to see its products and total. Page Up and Page Down move by a visible page of rows.",
                  columns: [("company", "Company", 310), ("count", "Products", 120), ("bytes", "Size", 160), ("measured", "Fully measured", 160)])
        let companyScroll = scroll(companies)
        root.addArrangedSubview(companyScroll)
        root.addArrangedSubview(NSTextField(labelWithString: "Products"))
        configure(libraries, name: "Company products", help: "Product sizes, scan status and folder locations. Command+O opens the selected folder. Page Up and Page Down move by a visible page of rows.",
                  columns: [("name", "Product", 280), ("bytes", "Size", 130), ("state", "Status", 150), ("path", "Location", 430)])
        let productScroll = scroll(libraries)
        for (index, table) in [companies, libraries].enumerated() {
            let menu = NSMenu()
            let copy = NSMenuItem(title: "Copy Selected Rows", action: #selector(copyContextRows(_:)), keyEquivalent: "c")
            copy.target = self; copy.tag = index
            menu.addItem(copy)
            table.menu = menu
            if table === libraries { menu.delegate = self }
        }
        root.addArrangedSubview(productScroll)
        let totalsScroll = summary(totals, name: "Storage totals", text: "No completed scan.", help: "Company totals include all its products, even when the product filter is active. Shared locations count once per company and once overall; company totals may overlap. Sizes are logical file sizes, not reclaimable space.")
        let statusScroll = summary(status, name: "Scan status", text: "Ready", help: "Current scan progress or completion state.")
        root.addArrangedSubview(totalsScroll); root.addArrangedSubview(statusScroll)
        window.contentView = root
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor),
            root.topAnchor.constraint(equalTo: window.contentView!.topAnchor),
            root.bottomAnchor.constraint(equalTo: window.contentView!.bottomAnchor),
            companyScroll.heightAnchor.constraint(equalTo: productScroll.heightAnchor, multiplier: 0.65)
        ])
        for view in [controls, companyScroll, productScroll, totalsScroll, statusScroll] {
            view.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -24).isActive = true
        }
        companyScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 140).isActive = true
        productScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 220).isActive = true
        let order: [NSView] = [ranking, ordering, search, scanButton, cancelButton, companies, libraries, totals, status]
        for pair in zip(order, order.dropFirst() + [order[0]]) { pair.0.nextKeyView = pair.1 }
        window.initialFirstResponder = companies
        HelpDialog.prepare(root)
    }

    private func summary(_ text: NSTextView, name: String, text value: String, help: String) -> NSScrollView {
        text.isEditable = false
        text.isSelectable = true
        text.isRichText = false
        text.font = .systemFont(ofSize: NSFont.systemFontSize)
        text.string = value
        text.setAccessibilityLabel(name)
        text.setAccessibilityHelp(help)
        text.isVerticallyResizable = true
        text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainer?.widthTracksTextView = true
        text.textContainerInset = NSSize(width: 3, height: 3)
        let scroll = NSScrollView()
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        scroll.heightAnchor.constraint(equalToConstant: 80).isActive = true
        return scroll
    }

    private func configure(_ table: NSTableView, name: String, help: String, columns: [(String, String, CGFloat)]) {
        table.dataSource = self; table.delegate = self
        table.usesAlternatingRowBackgroundColors = true
        table.setAccessibilityLabel(name); table.setAccessibilityHelp(help)
        for (id, title, width) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title; column.width = width
            table.addTableColumn(column)
        }
    }

    private func scroll(_ table: NSTableView) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true
        return scroll
    }

    @objc private func scanAction() { rescan() }
    @objc private func cancelAction() { cancellation?.cancel() }
    @objc private func rankChanged() { showCompanies() }
    @objc private func orderChanged() { showProducts() }

    func rescan() {
        guard cancellation == nil else { return }
        let control = StorageCancellation()
        cancellation = control
        scanButton.isEnabled = false; cancelButton.isEnabled = true
        status.string = "Scanning storage..."
        let progress: @Sendable (String) -> Void = { [weak self] path in
            Task { @MainActor [weak self] in
                if self?.cancellation === control { self?.status.string = "Scanning: \(path)" }
            }
        }
        Task { [weak self] in
            let task = Task.detached(priority: .utility) {
                let products = NativeScanner().scan().products
                return try StorageScanner.scan(products, cancellation: control, progress: progress)
            }
            do {
                let next = try await task.value
                guard let self else { return }
                result = next
                showCompanies()
                status.string = "Scan complete.\nProducts: \(next.rows.count).\nCompanies: \(next.companies.count).\nNeed attention: \(next.rows.filter { $0.state != "Measured" }.count)."
                NSAccessibility.post(element: status, notification: .announcementRequested, userInfo: [
                    .announcement: "Storage scan complete.", .priority: NSAccessibilityPriorityLevel.high.rawValue
                ])
            } catch is CancellationError { self?.status.string = "Scan cancelled." }
            catch { self?.status.string = "Scan failed: \(error.localizedDescription)" }
            self?.cancellation = nil
            self?.scanButton.isEnabled = true; self?.cancelButton.isEnabled = false
        }
    }

    func windowWillClose(_ notification: Notification) { cancellation?.cancel() }
    func selectedFolderActions() -> [FolderAction] {
        guard productRows.indices.contains(libraries.selectedRow) else { return [] }
        let row = productRows[libraries.selectedRow]
        return FolderActions.forPaths(row.locations, content: row.path)
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        while menu.numberOfItems > 1 { menu.removeItem(at: 1) }
        menu.addItem(.separator())
        FolderActions.populate(menu, actions: selectedFolderActions(), target: self, selector: #selector(openFolderAction(_:)))
    }
    @objc private func openFolderAction(_ sender: NSMenuItem) {
        guard let action = sender.representedObject as? FolderAction else { return }
        openProductFolder(action)
    }
    func openProductFolder(_ action: FolderAction) {
        if !FolderActions.open(action) { status.string = "This folder is unavailable." }
    }
    func focusSearch() { window.makeFirstResponder(search) }
    func controlTextDidChange(_ obj: Notification) { showProducts() }

    private func showCompanies() {
        guard let result else { return }
        let selected = companies.selectedRow >= 0 && companies.selectedRow < companyRows.count ? companyRows[companies.selectedRow].name : nil
        companyRows = result.companies.sorted {
            if ranking.indexOfSelectedItem == 0 && $0.bytes != $1.bytes { return $0.bytes > $1.bytes }
            if ranking.indexOfSelectedItem == 1 && $0.count != $1.count { return $0.count > $1.count }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        companies.reloadData()
        if !companyRows.isEmpty {
            companies.selectRowIndexes(IndexSet(integer: companyRows.firstIndex { $0.name == selected } ?? 0), byExtendingSelection: false)
        }
        showProducts()
    }

    private func showProducts() {
        guard let result, companies.selectedRow >= 0, companies.selectedRow < companyRows.count else { return }
        let company = companyRows[companies.selectedRow]
        let selected = libraries.selectedRow >= 0 && libraries.selectedRow < productRows.count ? productRows[libraries.selectedRow].name : nil
        let query = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        productRows = result.rows.filter {
            $0.company == company.name && (query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.locationSummary.localizedCaseInsensitiveContains(query))
        }.sorted {
            if ordering.indexOfSelectedItem != 2 {
                if $0.hasSize != $1.hasSize { return $0.hasSize }
                if $0.bytes != $1.bytes { return ordering.indexOfSelectedItem == 0 ? $0.bytes > $1.bytes : $0.bytes < $1.bytes }
            }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        libraries.reloadData()
        if let index = productRows.firstIndex(where: { $0.name == selected }) { libraries.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false) }
        totals.string = "\(company.name): \(StorageReport.companySize(company)).\nProducts: \(company.count).\nShown: \(productRows.count).\nAll companies: \(StorageReport.size(result.totalBytes))."
        libraries.setAccessibilityLabel("\(company.name) products")
    }

    func numberOfRows(in tableView: NSTableView) -> Int { tableView === companies ? companyRows.count : productRows.count }
    func tableViewSelectionDidChange(_ notification: Notification) { if (notification.object as? NSTableView) === companies { showProducts() } }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let column = tableColumn else { return nil }
        let value: String
        if tableView === companies {
            guard companyRows.indices.contains(row) else { return nil }
            let company = companyRows[row]
            switch column.identifier.rawValue {
            case "company": value = company.name
            case "count": value = String(company.count)
            case "bytes": value = StorageReport.companySize(company)
            default: value = "\(company.measuredCount) of \(company.count)"
            }
        } else {
            guard productRows.indices.contains(row) else { return nil }
            let product = productRows[row]
            switch column.identifier.rawValue {
            case "name": value = product.name
            case "bytes": value = product.hasSize ? StorageReport.size(product.bytes) : "Unavailable"
            case "state": value = product.state
            default: value = product.locationSummary
            }
        }
        let cell = tableView.makeView(withIdentifier: column.identifier, owner: self) as? NSTableCellView ?? NSTableCellView()
        cell.identifier = column.identifier
        let field = cell.textField ?? NSTextField(labelWithString: "")
        field.stringValue = value; field.lineBreakMode = .byTruncatingTail
        field.setAccessibilityLabel("\(column.title): \(value)")
        if cell.textField == nil {
            field.translatesAutoresizingMaskIntoConstraints = false
            cell.addSubview(field); cell.textField = field
            NSLayoutConstraint.activate([
                field.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 4),
                field.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
                field.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
            ])
        }
        return cell
    }

    func openSelectedFolder() {
        guard productRows.indices.contains(libraries.selectedRow) else { return }
        let row = productRows[libraries.selectedRow]
        guard row.hasSize else { status.string = "This location is unavailable."; return }
        if !FolderActions.open(row.path) { status.string = "This location is unavailable." }
    }

    func copySelection() {
        if let text = window.firstResponder as? NSTextView, text === totals || text === status { text.copy(nil); return }
        copyRows(from: window.firstResponder === companies ? companies : libraries)
    }

    @objc private func copyContextRows(_ sender: NSMenuItem) {
        copyRows(from: sender.tag == 0 ? companies : libraries)
    }

    private func copyRows(from table: NSTableView) {
        let text: String
        if table === companies, companyRows.indices.contains(companies.selectedRow) {
            let row = companyRows[companies.selectedRow]
            text = "\(row.name)\t\(row.count) products\t\(StorageReport.companySize(row))"
        } else if table === libraries, productRows.indices.contains(libraries.selectedRow) {
            let row = productRows[libraries.selectedRow]
            text = "\(row.name)\t\(row.hasSize ? StorageReport.size(row.bytes) : "Unavailable")\t\(row.state)\t\(row.locationSummary)"
        } else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
    }

    func exportHTML() {
        guard let result else { status.string = "Complete a scan before saving a report."; return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "NativeSerialsStorage.html"; panel.allowedContentTypes = [.html]
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            do { try StorageReport.html(result).write(to: url, atomically: true, encoding: .utf8); self?.status.string = "Saved storage report." }
            catch { self?.status.string = "Export failed: \(error.localizedDescription)" }
        }
    }

    func showHelp() {
        let fallback = "Storage scans registered content and installation locations, plus verified product plug-ins, applications and data. The Location column lists counted paths. Shared folders count once per company and once overall; company totals may overlap. Sizes are file sizes, not reclaimable space. Command+R scans; Command+F filters; Command+S saves a report without serials. Escape closes this help dialog. Open Manual opens the full guide."
        HelpDialog.showContext(in: window, fallback: fallback)
    }

    func showDiagnostics() {
        var text = "Native Serials 1.1\nStorage scan: \(cancellation == nil ? "idle" : "running")\n"
        if let result {
            text += "\(result.rows.count) registered products; \(result.companies.count) companies.\nOverall measured size: \(StorageReport.size(result.totalBytes))\n"
            for (state, rows) in Dictionary(grouping: result.rows, by: \.state).sorted(by: { $0.key < $1.key }) {
                text += "\(state): \(rows.count)\n"
            }
            text += "Shared locations: \(result.hasOverlap ? "yes" : "no")"
        } else { text += "No completed storage scan." }
        HelpDialog.show(title: "Storage Diagnostics", text: text, copyable: true)
    }
}
