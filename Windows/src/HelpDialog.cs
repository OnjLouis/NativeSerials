using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace NativeSerials
{
    internal static class HelpDialog
    {
        private sealed class HelpText { internal string Text; }
        private static readonly ConditionalWeakTable<Control, HelpText> descriptions = new ConditionalWeakTable<Control, HelpText>();

        internal static void Prepare(Control root)
        {
            if (!string.IsNullOrWhiteSpace(root.AccessibleDescription))
            {
                descriptions.GetValue(root, key => new HelpText()).Text = root.AccessibleDescription;
                root.AccessibleDescription = string.Empty;
            }
            foreach (Control child in root.Controls) Prepare(child);
        }

        internal static Control FocusedControl(Control root)
        {
            foreach (Control child in root.Controls)
                if (child.ContainsFocus) return FocusedControl(child);
            return root;
        }

        internal static string Description(Control control, string fallback)
        {
            for (var current = control; current != null && !(current is Form); current = current.Parent)
            {
                HelpText stored;
                if (descriptions.TryGetValue(current, out stored)) return stored.Text;
            }
            switch (ControlName(control))
            {
                case "Close": case "Later": return "Close this dialog and return to the previous control.";
                case "Open Manual": return "Open the complete manual in your browser.";
                case "Copy": case "Copy all": return "Copy all the displayed text to the clipboard.";
                case "Open release page": return "Open this version's release page in your browser.";
                case "Download and install": return "Download the update after confirmation, close Native Serials and restart with the new version.";
            }
            return fallback;
        }

        private static string ControlName(Control control)
        {
            if (!string.IsNullOrWhiteSpace(control.AccessibleName)) return control.AccessibleName;
            if (control is ButtonBase) return control.Text.Replace("&", "").Trim();
            return "Native Serials";
        }

        internal static string ContextText(Control root, string fallback)
        {
            return Description(FocusedControl(root), fallback);
        }

        internal static void ShowContext(Form owner, string fallback, Action openManual)
        {
            if (owner is TextViewerForm && ((TextViewerForm)owner).IsContextHelp) return;
            var focused = FocusedControl(owner);
            string name = ControlName(focused);
            using (var viewer = CreateContext(name, ContextText(owner, fallback), openManual))
                viewer.ShowDialog(DialogOwner(owner));
            if (!focused.IsDisposed && focused.CanFocus) focused.Focus();
        }

        internal static Form DialogOwner(Form form) { return form.TopLevel ? form : form.TopLevelControl as Form ?? form; }

        internal static TextViewerForm CreateContext(string name, string instructions, Action openManual)
        {
            return new TextViewerForm("Help: " + name, instructions, name, "", false, openManual) { IsContextHelp = true };
        }

        internal static void Attach(Form main, Action openManual)
        {
            var filter = new HelpKeyFilter(main, openManual);
            Application.AddMessageFilter(filter);
            main.Disposed += delegate { Application.RemoveMessageFilter(filter); };
        }

        private sealed class HelpKeyFilter : IMessageFilter
        {
            private readonly Form main;
            private readonly Action openManual;
            internal HelpKeyFilter(Form main, Action openManual) { this.main = main; this.openManual = openManual; }
            public bool PreFilterMessage(ref Message message)
            {
                const int keyDown = 0x0100;
                if (message.Msg != keyDown || (Keys)message.WParam.ToInt32() != Keys.F1 || Control.ModifierKeys != Keys.None) return false;
                var control = Control.FromChildHandle(message.HWnd);
                var owner = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.ContainsFocus)
                    ?? (control == null ? null : control.FindForm());
                for (var form = owner; form != null; form = form.Owner)
                    if (form == main)
                    {
                        ShowContext(owner, "Review the displayed information. Open Manual contains the complete guide.", openManual);
                        return true;
                    }
                return false;
            }
        }
    }
}
