import AppKit
import Foundation

@MainActor
enum HelpDialog {
    private static let descriptions = NSMapTable<NSView, NSString>.weakToStrongObjects()

    static func prepare(_ view: NSView) {
        if let text = view.accessibilityHelp(), !text.isEmpty {
            descriptions.setObject(text as NSString, forKey: view)
            let keys = ["Search products": "Command+F", "Filter products": "Command+F",
                        "Rescan": "Command+R", "Scan": "Command+R",
                        "Save HTML report": "Command+S", "Storage by Company": "Command+2"]
            let name = view.accessibilityLabel() ?? (view as? NSButton)?.title ?? ""
            view.setAccessibilityHelp(keys[name])
        }
        for child in view.subviews { prepare(child) }
    }

    static func instructions(for view: NSView) -> String? {
        descriptions.object(forKey: view) as String? ?? view.accessibilityHelp()
    }

    static func contextText(in window: NSWindow, fallback: String) -> String {
        var view = focusedView(in: window)
        while let current = view {
            if let help = instructions(for: current), !help.isEmpty {
                return help
            }
            view = current.superview
        }
        return fallback
    }

    private static func focusedView(in window: NSWindow) -> NSView? {
        if window === NSApp.keyWindow, let focused = NSApp.accessibilityFocusedUIElement as? NSView,
           focused.window === window { return focused }
        if let editor = window.firstResponder as? NSTextView, editor.isFieldEditor {
            func editingField(_ view: NSView) -> NSTextField? {
                if let field = view as? NSTextField, field.currentEditor() === editor { return field }
                for child in view.subviews { if let field = editingField(child) { return field } }
                return nil
            }
            return window.contentView.flatMap { editingField($0) } ?? editor
        }
        return window.firstResponder as? NSView
    }

    static func makePanel(title: String, text: String, copyable: Bool = false) -> HelpPanel {
        HelpPanel(title: title, text: text, copyable: copyable)
    }

    static func showContext(in window: NSWindow, fallback: String) {
        let view = focusedView(in: window)
        let name = view?.accessibilityLabel() ?? (view as? NSButton)?.title ?? "Native Serials"
        show(title: "Help: " + name, text: contextText(in: window, fallback: fallback), isContextHelp: true)
    }

    static func show(title: String, text: String, copyable: Bool = false, isContextHelp: Bool = false) {
        let owner = NSApp.keyWindow
        let restore = owner.flatMap { focusedView(in: $0) }
        let panel = makePanel(title: title, text: text, copyable: copyable)
        panel.isContextHelp = isContextHelp
        panel.center()
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(panel.editor)
        panel.editor.setSelectedRange(NSRange(location: 0, length: 0))
        NSApp.runModal(for: panel)
        panel.orderOut(nil)
        if let owner, owner.isVisible {
            owner.makeKeyAndOrderFront(nil)
            if let restore, restore.window === owner { owner.makeFirstResponder(restore) }
        }
    }

    static func openManual() {
        guard let url = Bundle.main.url(forResource: "Manual", withExtension: "html") else {
            let error = NSAlert()
            error.messageText = "Manual not found"
            error.informativeText = "Manual.html was not found inside the app bundle."
            error.addButton(withTitle: "Close").keyEquivalent = "\u{1b}"
            error.runModal()
            return
        }
        NSWorkspace.shared.open(url)
    }
}

@MainActor
final class HelpPanel: NSPanel, NSWindowDelegate {
    var isContextHelp = false
    let editor = HelpTextView(frame: NSRect(x: 0, y: 0, width: 580, height: 240))
    let manualButton = NSButton(title: "Open Manual", target: nil, action: nil)
    let closeButton = NSButton(title: "Close", target: nil, action: nil)

    init(title: String, text: String, copyable: Bool) {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 620, height: 360),
                   styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        self.title = title
        minSize = NSSize(width: 480, height: 260)
        isReleasedWhenClosed = false
        delegate = self
        editor.isEditable = false
        editor.isSelectable = true
        editor.isRichText = false
        editor.font = .systemFont(ofSize: NSFont.systemFontSize)
        editor.string = text
        editor.setAccessibilityLabel(title.replacingOccurrences(of: "Help: ", with: "", options: .anchored))
        editor.setAccessibilityHelp("Review the displayed information and select text to copy. Use Copy All for the complete report.")
        HelpDialog.prepare(editor)
        editor.isVerticallyResizable = true
        editor.isHorizontallyResizable = false
        editor.autoresizingMask = [.width]
        editor.textContainer?.widthTracksTextView = true
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.documentView = editor
        manualButton.target = self
        manualButton.action = #selector(openManual)
        manualButton.setAccessibilityHelp("Open the complete manual.")
        closeButton.target = self
        closeButton.action = #selector(closeHelp)
        closeButton.keyEquivalent = "\u{1b}"
        closeButton.setAccessibilityHelp("Close this window and return to the previous control.")
        var buttons = [manualButton, closeButton]
        if copyable {
            let copy = NSButton(title: "Copy All", target: self, action: #selector(copyText))
            copy.setAccessibilityHelp("Copy all the displayed text.")
            buttons.insert(copy, at: 0)
        }
        let bar = NSStackView(views: buttons)
        bar.spacing = 12
        let stack = NSStackView(views: [scroll, bar])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false
        contentView!.addSubview(stack)
        let root = contentView!
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -16),
            stack.topAnchor.constraint(equalTo: root.topAnchor, constant: 16),
            stack.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -16),
            scroll.widthAnchor.constraint(equalTo: stack.widthAnchor),
            scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 120)
        ])
        let cycle: [NSView] = [editor] + buttons
        for pair in zip(cycle, cycle.dropFirst() + [cycle[0]]) { pair.0.nextKeyView = pair.1 }
        initialFirstResponder = editor
        editor.tabAction = { [weak self] in self?.makeFirstResponder(buttons[0]) }
        editor.backTabAction = { [weak self] in self?.makeFirstResponder(self?.closeButton) }
    }

    @objc private func openManual() { HelpDialog.openManual() }
    @objc private func copyText() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(editor.string, forType: .string)
    }
    @objc private func closeHelp() { NSApp.stopModal(withCode: .OK) }
    override func cancelOperation(_ sender: Any?) { closeHelp() }
    override func sendEvent(_ event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if event.type == .keyDown {
            if event.keyCode == 53 && modifiers.isEmpty || event.keyCode == 13 && modifiers == .command {
                closeHelp(); return
            }
            if event.keyCode == 48 && (modifiers.isEmpty || modifiers == .shift) {
                if modifiers == .shift { selectPreviousKeyView(nil) } else { selectNextKeyView(nil) }
                return
            }
            if event.keyCode == 122 && modifiers.isEmpty {
                if !isContextHelp { HelpDialog.showContext(in: self, fallback: "Review the displayed information.") }
                return
            }
        }
        super.sendEvent(event)
    }
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if event.keyCode == 53 && modifiers.isEmpty || event.keyCode == 13 && modifiers == .command { closeHelp(); return true }
        return super.performKeyEquivalent(with: event)
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool { closeHelp(); return false }
}

final class HelpTextView: NSTextView {
    var tabAction: (() -> Void)?
    var backTabAction: (() -> Void)?
    override func insertTab(_ sender: Any?) { tabAction?() }
    override func insertBacktab(_ sender: Any?) { backTabAction?() }
}
