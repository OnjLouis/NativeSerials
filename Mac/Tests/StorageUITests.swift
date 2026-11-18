import AppKit
import Foundation

@main
struct StorageUITests {
    static func check(_ condition: @autoclosure () -> Bool, _ message: String = "", line: UInt = #line) {
        if !condition() { print("UI test failed at line \(line): \(message)"); exit(1) }
    }
    @MainActor
    static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        let controller = StorageWindowController()
        let window = controller.window!
        func descendants(_ view: NSView) -> [NSView] {
            view.subviews.flatMap { [$0] + descendants($0) }
        }
        let views = descendants(window.contentView!)
        check(views.compactMap { $0 as? NSTableView }.count == 2)
        check(views.compactMap { $0 as? NSTableView }.allSatisfy { $0.menu?.item(at: 0)?.title == "Copy Selected Rows" })
        check(views.compactMap { $0 as? NSTableView }.allSatisfy { $0.menu?.item(at: 0)?.keyEquivalent == "c" })
        check(views.compactMap { $0 as? NSPopUpButton }.count == 2)
        let information = views.compactMap { $0 as? NSTextView }.filter { ["Storage totals", "Scan status"].contains($0.accessibilityLabel() ?? "") }
        check(information.count == 2 && information.allSatisfy { !$0.isEditable && $0.isSelectable && $0.accessibilityRole() == .textArea }, "Totals and scan status must be selectable read-only edit areas")
        for field in information { field.string = "First line\nSecond line\nThird line\nFourth line" }
        window.setFrame(NSRect(origin: .zero, size: window.minSize), display: false)
        window.contentView!.layoutSubtreeIfNeeded()
        let layout = window.contentView as! NSStackView
        for view in layout.arrangedSubviews where !layout.bounds.contains(view.frame) {
            print("Storage content outside minimum-size bounds: \(view.frame), parent \(layout.bounds)"); exit(1)
        }
        for field in information {
            let lineHeight = field.layoutManager!.defaultLineHeight(for: field.font!)
            check(field.enclosingScrollView!.contentSize.height >= lineHeight * 4, "Four lines of summary text must fit")
        }
        for view in views where view is NSButton || view is NSSearchField || view is NSTableView || view is NSTextView {
            check(!(HelpDialog.instructions(for: view) ?? "").isEmpty)
        }
        var visited = Set<ObjectIdentifier>()
        var next = window.initialFirstResponder
        while let view = next, visited.insert(ObjectIdentifier(view)).inserted {
            next = view.nextKeyView
        }
        check(visited.count == 9, "Explicit key-view cycle must reach every control")
        window.close()
        let delegate = AppController()
        delegate.applicationDidFinishLaunching(Notification(name: NSApplication.didFinishLaunchingNotification))
        for openWindow in app.windows { openWindow.orderOut(nil) }
        let inventory = app.windows.first { $0.title == "Native Serials" }!
        let tabs = inventory.contentView as! NSTabView
        check(tabs.tabViewItems.map(\.label) == ["Inventory", "Storage by Company"])
        let inventoryViews = descendants(tabs.tabViewItem(at: 0).view!)
        let inventoryTable = inventoryViews.compactMap { $0 as? NSTableView }.first!
        guard inventory.initialFirstResponder === inventoryTable && inventory.firstResponder === inventoryTable else {
            print("Startup focus must land in the products table, not Search"); exit(1)
        }
        inventory.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        inventory.makeKeyAndOrderFront(nil)
        func focusedName() -> String {
            if let editor = inventory.firstResponder as? NSTextView, editor.isFieldEditor {
                return inventoryViews.compactMap { $0 as? NSTextField }.first { $0.currentEditor() === editor }?.accessibilityLabel() ?? "Field editor"
            }
            return (inventory.firstResponder as? NSView)?.accessibilityLabel() ?? "Unknown"
        }
        var reverse: [String] = []
        for _ in 0..<9 { inventory.selectPreviousKeyView(nil); reverse.append(focusedName()) }
        check(reverse == ["Without serials", "With serials", "All products", "Save HTML report", "Reveal serials", "Rescan", "Search products", "Views", "Product serials"], "Actual reverse focus traversal: \(reverse)")
        let filters = inventoryViews.compactMap { $0 as? NSButton }.filter { ["All products", "With serials", "Without serials"].contains($0.title) }
        check(filters.count == 3 && filters.allSatisfy {
            (NSAccessibility.unignoredDescendant(of: $0) as? NSAccessibilityProtocol)?.accessibilityRole() == .radioButton
        }, "Native radio accessibility peers must expose the radio-button role")
        let allFilter = filters.first { $0.title == "All products" }!
        let withFilter = filters.first { $0.title == "With serials" }!
        let withoutFilter = filters.first { $0.title == "Without serials" }!
        check(allFilter.state == .on && filters.filter { $0.state == .on }.count == 1)
        let allCount = inventoryTable.numberOfRows
        inventory.makeFirstResponder(withFilter); withFilter.performClick(nil)
        let withCount = inventoryTable.numberOfRows
        check(inventory.firstResponder === withFilter && filters.filter { $0.state == .on }.count == 1, "Choosing a filter must not steal focus")
        withoutFilter.performClick(nil)
        check(withCount + inventoryTable.numberOfRows == allCount, "Serial filter choices must partition the inventory")
        allFilter.performClick(nil)
        check(inventoryTable.numberOfRows == allCount)
        check(inventoryTable.menu?.item(at: 0)?.title == "Copy Selected Rows")
        check(inventoryTable.tableColumns.suffix(2).map(\.identifier.rawValue) == ["path", "upid"],
                     "Path must precede UPID, with UPID last")
        let search = inventoryViews.compactMap { $0 as? NSSearchField }.first!
        inventory.makeFirstResponder(search)
        check(HelpDialog.contextText(in: inventory, fallback: "Missing").hasPrefix("Type a product name"),
                     "F1 must resolve the search field editor to its full instructions, not its shortcut hint")
        check(!inventoryViews.compactMap { $0 as? NSButton }.contains { $0.title == "Storage by Company" }, "The native tab replaces the redundant navigation button")
        let viewMenu = app.mainMenu!.item(withTitle: "View")!.submenu!
        check(viewMenu.item(withTitle: "Storage by Company")?.keyEquivalent == "2")
        let helpMenu = app.mainMenu!.item(withTitle: "Help")!.submenu!
        check(helpMenu.item(withTitle: "Context Help")?.keyEquivalent == "\u{F704}")
        check(helpMenu.item(withTitle: "Open Manual")?.keyEquivalent == "")
        check(helpMenu.item(withTitle: "Donate")?.action != nil)
        let diagnostics = helpMenu.item(withTitle: "Diagnostics...")!
        check(diagnostics.keyEquivalent == "\u{F704}" && diagnostics.keyEquivalentModifierMask == [.option])
        check(app.mainMenu!.items[0].submenu!.item(withTitle: "Diagnostics...") == nil)
        visited.removeAll()
        next = inventory.initialFirstResponder
        var keyNames: [String] = []
        while let view = next, visited.insert(ObjectIdentifier(view)).inserted {
            keyNames.append("\(type(of: view)): \(view.accessibilityLabel() ?? "")")
            next = view.nextKeyView
        }
        check(visited.count == 9 && visited.contains(ObjectIdentifier(tabs)) && visited.contains(ObjectIdentifier(search)) && visited.contains(ObjectIdentifier(inventoryTable)), "Inventory key-view cycle: \(keyNames)")
        let storageCommand = viewMenu.item(withTitle: "Storage by Company")!
        app.sendAction(storageCommand.action!, to: storageCommand.target, from: nil)
        check(tabs.selectedTabViewItem?.identifier as? String == "storage")
        let storageViews = descendants(tabs.tabViewItem(at: 1).view!)
        let cancel = storageViews.compactMap { $0 as? NSButton }.first { $0.title == "Cancel" }!
        let rescan = storageViews.compactMap { $0 as? NSButton }.first { $0.title == "Scan" }!
        func control(_ label: String) -> NSView { storageViews.first { $0.accessibilityLabel() == label }! }
        let order = [control("Company order"), control("Product order"), control("Filter products"), rescan, cancel,
                     control("Companies"), control("Company products"), control("Storage totals"), control("Scan status"), tabs]
        check(order.count == 10 && order.last === tabs)
        for view in order.compactMap({ $0 as? NSButton }) { view.isEnabled = true }
        func ownsFocus(_ view: NSView) -> Bool {
            inventory.firstResponder === view || (view as? NSTextField)?.currentEditor() === inventory.firstResponder
        }
        func pressTab(_ backwards: Bool) {
            let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: backwards ? [.shift] : [], timestamp: 0,
                windowNumber: inventory.windowNumber, context: nil, characters: "\t", charactersIgnoringModifiers: "\t", isARepeat: false, keyCode: 48)!
            inventory.sendEvent(event)
        }
        inventory.makeFirstResponder(tabs)
        for _ in 0..<2 {
            for view in order {
                pressTab(false)
                let actual = order.first { ownsFocus($0) }
                check(ownsFocus(view), "Forward Tab expected \(view.accessibilityLabel() ?? String(describing: type(of: view)))), actual \(actual?.accessibilityLabel() ?? String(describing: inventory.firstResponder))")
            }
            for view in order.dropLast().reversed() + [tabs] { pressTab(true); check(ownsFocus(view), "Reverse Tab: \(view.accessibilityLabel() ?? String(describing: type(of: view)))") }
        }
        cancel.isEnabled = false
        inventory.makeFirstResponder(rescan)
        pressTab(false)
        check(ownsFocus(order[5]), "Disabled Cancel must be skipped")
        cancel.isEnabled = true; rescan.isEnabled = false
        inventory.makeFirstResponder(cancel)
        pressTab(true)
        check(ownsFocus(order[2]), "Disabled Rescan must be skipped")
        let inventoryCommand = viewMenu.item(withTitle: "Inventory")!
        app.sendAction(inventoryCommand.action!, to: inventoryCommand.target, from: nil)
        check(tabs.selectedTabViewItem?.identifier as? String == "inventory" && inventory.firstResponder === inventoryTable)
        inventory.close()
        let help = HelpDialog.makePanel(title: "Help fixture", text: "Fixture help")
        check(help.editor.string == "Fixture help" && !help.editor.isEditable && help.editor.isSelectable)
        check(help.initialFirstResponder === help.editor)
        check(help.editor.nextKeyView === help.manualButton)
        check(help.manualButton.nextKeyView === help.closeButton)
        check(help.closeButton.nextKeyView === help.editor)
        check(help.closeButton.keyEquivalent == "\u{1b}")
        help.makeKeyAndOrderFront(nil)
        help.makeFirstResponder(help.editor)
        check(help.firstResponder === help.editor)
        let tab = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                  windowNumber: help.windowNumber, context: nil, characters: "\t",
                                  charactersIgnoringModifiers: "\t", isARepeat: false, keyCode: 48)!
        help.sendEvent(tab)
        check(help.firstResponder === help.manualButton, "Tab must leave help text for Open Manual")
        let backTab = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [.shift], timestamp: 0,
                                      windowNumber: help.windowNumber, context: nil, characters: "\t",
                                      charactersIgnoringModifiers: "\t", isARepeat: false, keyCode: 48)!
        help.sendEvent(backTab)
        check(help.firstResponder === help.editor, "Shift-Tab must return to the help text")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            let escape = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [],
                                          timestamp: 0, windowNumber: help.windowNumber, context: nil,
                                          characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53)!
            _ = help.performKeyEquivalent(with: escape)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
            if app.modalWindow === help { app.abortModal() }
        }
        check(app.runModal(for: help) == .OK, "Escape must dismiss the actual Mac help dialog")
        help.orderOut(nil)
        let diagnosticsPanel = HelpDialog.makePanel(title: "Diagnostics fixture", text: "Fixture details", copyable: true)
        check(descendants(diagnosticsPanel.contentView!).compactMap { $0 as? NSButton }.count == 3)
        diagnosticsPanel.close()
        checkPaging()
        print("Native storage UI structure and key-view tests passed.")
    }

    @MainActor static func checkPaging() {
        final class Rows: NSObject, NSTableViewDataSource {
            var count = 100
            func numberOfRows(in tableView: NSTableView) -> Int { count }
            func tableView(_ tableView: NSTableView, objectValueFor tableColumn: NSTableColumn?, row: Int) -> Any? { "Fixture \(row)" }
        }
        let source = Rows()
        let table = PageNavigableTable(frame: NSRect(x: 0, y: 0, width: 500, height: 2000))
        table.addTableColumn(NSTableColumn(identifier: NSUserInterfaceItemIdentifier("fixture")))
        table.dataSource = source
        table.allowsMultipleSelection = true
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 500, height: 160))
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        let window = NSWindow(contentRect: scroll.frame, styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = scroll
        window.layoutIfNeeded()
        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: 40), byExtendingSelection: false)
        table.scrollRowToVisible(40)
        func page(_ down: Bool, shift: Bool = false) {
            let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: shift ? [.shift] : [], timestamp: 0,
                windowNumber: window.windowNumber, context: nil, characters: down ? "\u{F72D}" : "\u{F72C}",
                charactersIgnoringModifiers: down ? "\u{F72D}" : "\u{F72C}", isARepeat: false, keyCode: down ? 121 : 116)!
            table.keyDown(with: event)
        }
        page(true)
        check(table.selectedRow > 40 && table.selectedRow < 60, "Page Down must move a visible page, not jump to the final product")
        let downRow = table.selectedRow
        page(false)
        check(table.selectedRow < downRow && table.selectedRow > 20, "Page Up must move a visible page, not jump to the first product")
        page(true, shift: true)
        check(table.selectedRowIndexes.count > 1, "Shift+Page Down must extend the selection")
        table.selectRowIndexes(IndexSet(integer: 99), byExtendingSelection: false)
        page(true)
        check(table.selectedRow == 99, "Paging must clamp at the last row")
        source.count = 0; table.reloadData(); page(false)
        check(table.selectedRow == -1)
        window.close()
    }
}
