using System;
using System.Windows.Forms;

namespace NativeSerials
{
    internal sealed class PageTabs : TabControl
    {
        internal Func<bool, bool> EnterPage;
        private bool keepSelectorFocus;
        protected override void OnDeselecting(TabControlCancelEventArgs e)
        {
            // Remember header focus before WinForms restores the new page's child focus.
            keepSelectorFocus = Focused;
            base.OnDeselecting(e);
        }
        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            if (keepSelectorFocus) Focus();
            keepSelectorFocus = false;
        }
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (Focused && EnterPage != null && (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)))
                return EnterPage(keyData == Keys.Tab);
            return base.ProcessDialogKey(keyData);
        }
    }

    internal sealed class ShortcutButton : Button
    {
        internal string ShortcutText { get; set; }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ShortcutAccessibleObject(this); }
        private sealed class ShortcutAccessibleObject : ControlAccessibleObject
        {
            private readonly ShortcutButton owner;
            internal ShortcutAccessibleObject(ShortcutButton owner) : base(owner) { this.owner = owner; }
            public override string KeyboardShortcut { get { return owner.ShortcutText; } }
        }
    }

    internal sealed class ShortcutTextBox : TextBox
    {
        internal string ShortcutText { get; set; }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ShortcutAccessibleObject(this); }
        private sealed class ShortcutAccessibleObject : ControlAccessibleObject
        {
            private readonly ShortcutTextBox owner;
            internal ShortcutAccessibleObject(ShortcutTextBox owner) : base(owner) { this.owner = owner; }
            public override string KeyboardShortcut { get { return owner.ShortcutText; } }
        }
    }

    internal static class MenuCommand
    {
        internal static ToolStripMenuItem Create(string text, EventHandler action, Keys shortcut = Keys.None, string display = null)
        {
            var item = new ToolStripMenuItem(text, null, action) { ShortcutKeys = shortcut, ShowShortcutKeys = true };
            if (display == null && shortcut != Keys.None) display = new KeysConverter().ConvertToString(shortcut);
            if (!string.IsNullOrEmpty(display))
            {
                item.ShortcutKeyDisplayString = display;
                // WinForms does not reliably include the displayed shortcut in the spoken menu name.
                item.AccessibleName = text.Replace("&", "") + ", " + display;
            }
            return item;
        }

        internal static void AddCopyMenu(ListView table, EventHandler copy)
        {
            var menu = new ContextMenuStrip();
            var item = Create("&Copy Selected Rows", copy, Keys.Control | Keys.C);
            menu.Items.Add(item);
            menu.Opening += delegate { item.Enabled = table.SelectedItems.Count > 0; };
            table.ContextMenuStrip = menu;
            table.Disposed += delegate { menu.Dispose(); };
        }
    }
}
