import AppKit

final class PageNavigableTable: NSTableView {
    private static let pageUp: UInt16 = 116
    private static let pageDown: UInt16 = 121

    override func keyDown(with event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        guard (event.keyCode == Self.pageUp || event.keyCode == Self.pageDown),
              modifiers.isEmpty || modifiers == .shift else { super.keyDown(with: event); return }
        guard numberOfRows > 0 else { return }
        let down = event.keyCode == Self.pageDown
        let visible = rows(in: visibleRect)
        let step = max(1, visible.length - 1)
        let current = down ? selectedRowIndexes.last : selectedRowIndexes.first
        let start = current ?? (visible.location == NSNotFound ? 0 : visible.location)
        let next = min(numberOfRows - 1, max(0, start + (down ? step : -step)))
        let selection = modifiers == .shift && allowsMultipleSelection
            ? IndexSet(integersIn: min(start, next)...max(start, next)) : IndexSet(integer: next)
        selectRowIndexes(selection, byExtendingSelection: modifiers == .shift && allowsMultipleSelection)
        scrollRowToVisible(next)
    }
}
