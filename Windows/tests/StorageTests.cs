using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

namespace NativeSerials
{
    internal static class StorageTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static StorageResult Scan(IList<NativeProduct> products, CancellationToken cancel)
        {
            return StorageScanner.Scan(products, cancel, null, new StorageSearchRoots());
        }

        [STAThread]
        public static void Main()
        {
            try { Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
        }

        private static void Run()
        {
            using (var context = HelpDialog.CreateContext("Search products", "Type a product name to filter the list.", delegate { }))
            {
                var text = Descendants(context).OfType<System.Windows.Forms.TextBox>().Single();
                Check(context.IsContextHelp && text.Text.StartsWith("Type a product"), "F1 must start directly with useful instructions");
                Check(text.AccessibleName == "Search products" && string.IsNullOrEmpty(text.AccessibleDescription), "F1 must not announce generic help metadata");
                Check(text.ReadOnly && text.Multiline && !text.AcceptsTab, "F1 must be an arrow-readable text field without a Tab trap");
                CheckLayout(context);
            }
            using (var help = new TextViewerForm("Help fixture", "Fixture help", "Context help", "Help", false, delegate { }))
            {
                Check(Descendants(help).OfType<System.Windows.Forms.Button>().Any(b => b.AccessibleName == "Open Manual"), "Each help dialog needs Open Manual");
                Check(help.CancelButton != null, "Escape needs a dialog cancel target");
                CheckLayout(help);
            }
            using (var diagnostics = new TextViewerForm("Diagnostics fixture", "Fixture diagnostics", "Diagnostics", "Diagnostics", true, delegate { }))
                CheckLayout(diagnostics);
            CheckUpdateOffer();
            CheckSerialFilter();
            CheckNavigation();
            using (var main = new MainForm())
            {
                var controls = Descendants(main).ToList();
                Check(controls.All(c => string.IsNullOrEmpty(c.AccessibleDescription)), "Normal focus must not announce F1 explanations");
                foreach (var control in controls.Where(c => c is System.Windows.Forms.TextBox || c is System.Windows.Forms.ListView || c is System.Windows.Forms.Button || c is System.Windows.Forms.CheckBox || c is System.Windows.Forms.RadioButton))
                    Check(!string.IsNullOrEmpty(HelpDialog.Description(control, "")), "Each inventory control needs F1 content: " + control.AccessibleName);
                foreach (var button in controls.OfType<ShortcutButton>())
                    Check(!button.Text.Contains("&") && button.AccessibilityObject.KeyboardShortcut.StartsWith("Ctrl+"), "One reported shortcut per command button");
                Check(!controls.OfType<System.Windows.Forms.Button>().Any(b => b.AccessibleName == "Storage by Company"),
                      "The native tabs replace the redundant navigation button");
                main.CreateControl();
                main.PerformLayout();
                CheckLayout(main);
                var productTable = controls.OfType<System.Windows.Forms.ListView>().Single();
                Check(productTable.ContextMenuStrip != null, "Inventory table needs an Application-key copy menu");
                Check(controls.OfType<System.Windows.Forms.Label>().Any(l => l.Text == "&Products"), "Inventory products needs an Alt+P label");
                Check(productTable.Columns[productTable.Columns.Count - 1].Text == "UPID" && productTable.Columns[productTable.Columns.Count - 2].Text == "Path",
                      "Path must precede UPID, with UPID last");
                var fixture = new NativeProduct { Name = "Fixture", ContentDir = "fixture-content", InstallDir = "fixture-install", Upid = "fixture-upid", Snpid = "fixture-snpid" };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var copied = (string)typeof(MainForm).GetMethod("RowText", flags).Invoke(main, new object[] { fixture, false });
                Check(copied.Split('\t').Last() == "UPID: fixture-upid", "Copied rows must put UPID last");
                fixture.Serial = "private-fixture-serial";
                var masked = (string)typeof(MainForm).GetMethod("RowText", flags).Invoke(main, new object[] { fixture, false });
                var revealed = (string)typeof(MainForm).GetMethod("RowText", flags).Invoke(main, new object[] { fixture, true });
                Check(!masked.Contains(fixture.Serial) && revealed.Contains(fixture.Serial), "Copy commands must preserve the reveal-serials choice");
                typeof(MainForm).GetField("filteredProducts", flags).SetValue(main, new List<NativeProduct> { fixture });
                var html = (string)typeof(MainForm).GetMethod("HtmlReport", flags).Invoke(main, new object[] { false });
                Check(html.Contains("<th>Path</th><th>UPID</th>") && html.Contains("<td>fixture-content</td><td>fixture-upid</td></tr>"), "Export headers and cells must agree on Path then UPID");
                var toolbar = controls.OfType<System.Windows.Forms.TextBox>().Single().Parent;
                Check(productTable.Top >= toolbar.Bottom, "Inventory table must not cover its controls");
                var rootKeys = new HashSet<char>();
                foreach (var control in controls.Where(c => c is System.Windows.Forms.Label || c is System.Windows.Forms.Button || c is System.Windows.Forms.CheckBox || c is System.Windows.Forms.RadioButton || c is System.Windows.Forms.GroupBox))
                {
                    int index = control.Text.IndexOf('&');
                    if (index >= 0 && index + 1 < control.Text.Length)
                        Check(rootKeys.Add(char.ToUpperInvariant(control.Text[index + 1])), "Unique main-window mnemonics");
                }
                var menus = controls.OfType<System.Windows.Forms.MenuStrip>().SelectMany(m => m.Items.Cast<System.Windows.Forms.ToolStripMenuItem>()).ToList();
                var helpItems = menus.Single(m => m.Text == "&Help").DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().ToList();
                Check(helpItems.Any(i => i.Text.Contains("Help") && i.ShortcutKeys == System.Windows.Forms.Keys.F1), "F1 must open context help, not the manual");
                Check(helpItems.Any(i => i.Text.Contains("Manual") && i.ShortcutKeys == System.Windows.Forms.Keys.None), "Full manual needs its own menu command");
                Check(menus.Any(m => m.Text.Replace("&", "") == "View" && m.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Any(i => i.Text.Contains("Storage"))),
                      "Storage command belongs under View");
                foreach (var menu in menus) CheckMenu(menu);
                Check(!menus.Single(m => m.Text == "&File").DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Any(i => i.Text.Contains("gnostics")),
                      "Diagnostics must not remain in File");
                Check(menus.Single(m => m.Text == "&Help").DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => i.Text.Contains("gnostics") && i.ShortcutKeys == (System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.F1)),
                      "Help Diagnostics must expose Alt+F1");
            }
            using (var view = new StorageForm())
            {
                Check(!view.ShowInTaskbar, "Storage must not create a separate Alt+Tab task");
                CheckLayout(view);
                var controls = Descendants(view).ToList();
                Check(controls.All(c => string.IsNullOrEmpty(c.AccessibleDescription)), "Storage focus must not announce F1 explanations");
                Check(controls.OfType<System.Windows.Forms.ListView>().Count() == 2, "Two native storage tables");
                Check(controls.OfType<System.Windows.Forms.ListView>().All(t => t.ContextMenuStrip != null), "Both storage tables need copy menus");
                Check(controls.OfType<System.Windows.Forms.TextBox>().Single(b => b.AccessibleName == "Storage totals").AccessibilityObject.KeyboardShortcut == "Alt+T", "Storage totals needs Alt+T");
                Check(controls.OfType<System.Windows.Forms.TextBox>().Single(b => b.AccessibleName == "Scan status").AccessibilityObject.KeyboardShortcut == "Alt+S", "Scan status needs Alt+S");
                Check(controls.OfType<System.Windows.Forms.Label>().Any(l => l.Text == "Product &order:"), "Product order needs the meaningful Alt+O mnemonic");
                foreach (var box in controls.OfType<System.Windows.Forms.TextBox>())
                    Check(!box.AcceptsTab && !string.IsNullOrEmpty(box.AccessibleName), "Text fields must have labels and not trap Tab");
                foreach (var control in controls.Where(c => c is System.Windows.Forms.TextBox || c is System.Windows.Forms.ListView || c is System.Windows.Forms.Button || c is System.Windows.Forms.ComboBox))
                    Check(!string.IsNullOrEmpty(HelpDialog.Description(control, "")), "Each storage control needs F1 content: " + control.AccessibleName);
                foreach (var box in controls.OfType<System.Windows.Forms.ComboBox>().Cast<System.Windows.Forms.Control>().Concat(controls.OfType<System.Windows.Forms.TextBox>().Where(b => !b.ReadOnly)))
                {
                    var label = box.Parent.Controls.OfType<System.Windows.Forms.Label>().Single();
                    Check(box.Parent.Controls.Count == 2 && label.Parent == box.Parent, "Toolbar labels must wrap with their fields");
                }
                var keys = new HashSet<char>();
                foreach (var control in controls.Where(c => c is System.Windows.Forms.Label || c is System.Windows.Forms.Button))
                {
                    int index = control.Text.IndexOf('&');
                    if (index < 0 || index + 1 >= control.Text.Length) continue;
                    Check(keys.Add(char.ToUpperInvariant(control.Text[index + 1])), "Unique storage mnemonics");
                }
                foreach (var menu in controls.OfType<System.Windows.Forms.MenuStrip>())
                    foreach (System.Windows.Forms.ToolStripMenuItem item in menu.Items)
                    {
                        CheckMenu(item);
                        if (item.Text == "&Help")
                            Check(item.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Any(i => i.Text == "&Donate"), "Storage Help needs Donate");
                    }
            }
            var candidate = new NativeProduct { Name = "Maker-Synth", IsOwnedCandidate = true };
            var metadata = new NativeProduct { Name = "Synth", RegistryName = "Maker-Synth", Company = "Maker" };
            var enriched = new NativeScanner().EnrichCompanies(new[] { candidate }, new[] { metadata, new NativeProduct { Name = "Catalog only", Company = "Unused" } });
            Check(enriched.Count == 1 && enriched[0].Company == "Maker", "Registry-name company enrichment without adding catalog products");
            Check(StorageScanner.CompanyName("Fracture Sounds - Blueprint") == "Fracture Sounds", "Publisher label grouping");
            candidate.Company = "Original";
            Check(new NativeScanner().EnrichCompanies(new[] { candidate }, new[] { metadata })[0].Company == "Original", "Keep existing metadata");
            string root = Path.Combine(Path.GetTempPath(), "NativeSerials-storage-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(root, "Nested"));
            try
            {
                File.WriteAllBytes(Path.Combine(root, "base.bin"), new byte[10]);
                File.WriteAllBytes(Path.Combine(root, "Nested", "sample.bin"), new byte[20]);
                var products = new List<NativeProduct> {
                    new NativeProduct { Name = "Parent", Company = "Alpha", ContentDir = root },
                    new NativeProduct { Name = "Child", Company = "Alpha", ContentDir = Path.Combine(root,"Nested") },
                    new NativeProduct { Name = "Shared", Company = "Beta", ContentDir = root },
                    new NativeProduct { Name = "Offline", Company = "Beta", ContentDir = Path.Combine(root,"Absent") },
                    new NativeProduct { Name = "No location" },
                    new NativeProduct { Name = "Empty", Company = "Beta", ContentDir = Path.Combine(root,"Empty") }
                };
                Directory.CreateDirectory(Path.Combine(root, "Empty"));
                string content = Path.Combine(root, "Content");
                string install = Path.Combine(root, "Install");
                Directory.CreateDirectory(content); Directory.CreateDirectory(install);
                File.WriteAllBytes(Path.Combine(content, "content.bin"), new byte[11]);
                File.WriteAllBytes(Path.Combine(install, "install.bin"), new byte[17]);
                var combined = Scan(new[] { new NativeProduct { Name = "Combined", ContentDir = content, InstallDir = install } }, CancellationToken.None);
                Check(combined.TotalBytes == 28, "Both content and installation must count; got " + combined.TotalBytes);
                Directory.Delete(content, true); Directory.Delete(install, true);
                var result = Scan(products, CancellationToken.None);
                Check(result.TotalBytes == 30, "Grand total must not double count nested/shared roots");
                Check(result.Rows[0].Bytes == 30 && result.Rows[1].Bytes == 20, "Product sizes");
                Check(result.Companies.Find(c => c.Name == "Alpha").Bytes == 30, "Company total must be a union");
                Check(result.Companies.Find(c => c.Name == "Beta").Bytes == 30, "Cross-company overlap");
                Check(result.Rows[3].State == "Unavailable", "Missing folder must not be zero");
                Check(result.Rows[4].State == "No location", "Unknown location");
                Check(result.Rows[5].State == "Measured" && result.Rows[5].Bytes == 0, "Valid empty folder");
                Check(result.HasOverlap, "Shared total warning");
                using (var view = new StorageForm())
                {
                    view.CreateControl();
                    foreach (var control in Descendants(view)) { var handle = control.Handle; }
                    typeof(StorageForm).GetField("result", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(view, result);
                    typeof(StorageForm).GetMethod("ShowCompanies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(view, null);
                    var totals = Descendants(view).OfType<System.Windows.Forms.TextBox>().Single(b => b.AccessibleName == "Storage totals");
                    Check(totals.Text.Contains(StorageReport.Size(result.TotalBytes)) && totals.Text.Contains("Products:") && !totals.Text.Contains("not changed") && !totals.Text.Contains("Shared locations"), "Totals should show figures, not repeated help: " + totals.Text);
                    Check(totals.Lines.Length == 4 && totals.Lines[1].StartsWith("Products:") && totals.Lines[2].StartsWith("Shown:") && totals.Lines[3].StartsWith("All companies:"), "Each storage summary figure needs its own line");
                    CheckLayout(view);
                }
                var html = StorageReport.Html(result, result.Rows);
                Check(!html.Contains("Serial:"), "Storage export must not include serials");
                Check(html.Contains("<caption>Alpha</caption>") && html.Contains("scope=\"col\""), "Accessible grouped report");
                var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                bool cancelled = false;
                try { Scan(products, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancel before scan");
                var unsafeResult = Scan(new[] {
                    new NativeProduct { Name = "Root", ContentDir = Path.GetPathRoot(root) },
                    new NativeProduct { Name = "Relative", ContentDir = "relative-folder" },
                    new NativeProduct { Name = "Escaped <name>", Company = "A&B", ContentDir = "" , Serial = "secret-serial-never-export" }
                }, CancellationToken.None);
                Check(unsafeResult.Rows[0].State == "Unsafe broad location", "Do not scan a drive root");
                Check(unsafeResult.Rows[1].State == "Unavailable", "Do not resolve relative metadata against current directory");
                string safe = StorageReport.Html(unsafeResult, unsafeResult.Rows);
                Check(safe.Contains("Escaped &lt;name&gt;") && safe.Contains("A&amp;B") && !safe.Contains("secret-serial"), "Escaped export without serials");
                DiscoveryFixtures(Path.Combine(root, "Discovery"));
                Console.WriteLine("Storage tests passed.");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void SuppressStartup(System.Windows.Forms.Form form)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var shown = typeof(System.Windows.Forms.Form).GetFields(flags).Single(f => f.Name == "EVENT_SHOWN" || f.Name == "s_shownEvent");
            var events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component).GetProperty("Events",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form, null);
            object key = shown.GetValue(null);
            events.RemoveHandler(key, events[key]);
        }

        private static void CheckSerialFilter()
        {
            using (var form = new MainForm()) {
                SuppressStartup(form);
                var controls = Descendants(form).ToList();
                var choices = controls.OfType<System.Windows.Forms.RadioButton>().ToList();
                Check(choices.Count == 3, "Inventory needs All products, With serials and Without serials radio choices");
                var all = choices.Single(c => c.AccessibleName == "All products");
                var with = choices.Single(c => c.AccessibleName == "With serials");
                var without = choices.Single(c => c.AccessibleName == "Without serials");
                Check(all.Checked && !with.Checked && !without.Checked, "Start with all products, not a serial-only view");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(MainForm).GetField("products", flags).SetValue(form, new List<NativeProduct> {
                    new NativeProduct { Name = "Alpha", Serial = "fixture-alpha" },
                    new NativeProduct { Name = "Beta", Serial = "" },
                    new NativeProduct { Name = "Gamma", Serial = "fixture-gamma" }
                });
                Action<string[]> expect = names => {
                    var rows = (List<NativeProduct>)typeof(MainForm).GetField("filteredProducts", flags).GetValue(form);
                    Check(rows.Select(p => p.Name).SequenceEqual(names), "Serial visibility must filter by recovered serial, independently of reveal");
                    Check(choices.Count(c => c.Checked) == 1, "Radio selection must stay mutually exclusive");
                };
                with.Checked = true; expect(new[] { "Alpha", "Gamma" });
                without.Checked = true; expect(new[] { "Beta" });
                all.Checked = true; expect(new[] { "Alpha", "Beta", "Gamma" });
                var search = controls.OfType<System.Windows.Forms.TextBox>().Single(c => c.AccessibleName == "Search products");
                search.Text = "Beta"; with.Checked = true; expect(new string[0]);
                without.Checked = true; expect(new[] { "Beta" });
                controls.OfType<System.Windows.Forms.CheckBox>().Single(c => c.AccessibleName == "Reveal serials").Checked = true;
                expect(new[] { "Beta" });
            }
        }

        private static void Command(System.Windows.Forms.Form form, System.Windows.Forms.Keys keys)
        {
            var method = form.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var message = System.Windows.Forms.Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero);
            Check((bool)method.Invoke(form, new object[] { message, keys }), "Command must be handled: " + keys);
            System.Windows.Forms.Application.DoEvents();
        }

        private static void Tab(System.Windows.Forms.Form form, bool forward)
        {
            var method = form.GetType().GetMethod("ProcessTabKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check((bool)method.Invoke(form, new object[] { forward }), "Tab traversal must be handled");
            System.Windows.Forms.Application.DoEvents();
        }

        private static void Tab(System.Windows.Forms.TabControl tabs, bool forward)
        {
            var method = tabs.GetType().GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check((bool)method.Invoke(tabs, new object[] { forward ? System.Windows.Forms.Keys.Tab : System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Tab }), "Tab selector must enter its page");
            System.Windows.Forms.Application.DoEvents();
        }

        private static void DialogKey(System.Windows.Forms.Control target, System.Windows.Forms.Keys key)
        {
            if (key == System.Windows.Forms.Keys.Tab || key == System.Windows.Forms.Keys.Left || key == System.Windows.Forms.Keys.Right) {
                var message = System.Windows.Forms.Message.Create(target.Handle, 0x100, new IntPtr((int)key), IntPtr.Zero);
                if (!target.PreProcessMessage(ref message)) {
                    var dispatch = target.GetType().GetMethod("WndProc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    dispatch.Invoke(target, new object[] { message });
                }
            } else {
                var method = target.GetType().GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Check((bool)method.Invoke(target, new object[] { key }), "Focused-control key must be handled: " + key);
            }
            System.Windows.Forms.Application.DoEvents();
        }

        private static void CheckFocusCycle(System.Windows.Forms.Form owner, IList<System.Windows.Forms.Control> order)
        {
            Check(order[0].Focus(), "The first cycle control must be visible and focusable: " + order[0].AccessibleName);
            for (int repeat = 0; repeat < 2; repeat++) {
                for (int index = 1; index <= order.Count; index++) {
                    var focused = HelpDialog.FocusedControl(owner);
                    DialogKey(focused, System.Windows.Forms.Keys.Tab);
                    var expected = order[index % order.Count];
                    Check(expected.Focused, "Forward Tab from " + focused.AccessibleName + " must reach " + expected.AccessibleName + "; got " + HelpDialog.FocusedControl(owner).AccessibleName + "; modifiers " + System.Windows.Forms.Control.ModifierKeys + "; cycle " + string.Join(", ", order.Select(c => c.AccessibleName)));
                }
                for (int index = order.Count - 1; index >= 0; index--) {
                    var focused = HelpDialog.FocusedControl(owner);
                    DialogKey(focused, System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Tab);
                    Check(order[index].Focused, "Reverse Tab from " + focused.AccessibleName + " must reach " + order[index].AccessibleName + "; got " + HelpDialog.FocusedControl(owner).AccessibleName);
                }
            }
        }

        private static void CheckNavigation()
        {
            using (var owner = new MainForm())
            using (var view = new StorageForm())
            {
                // Real native focus, with startup scanning/update callbacks removed from these isolated forms.
                SuppressStartup(owner); SuppressStartup(view);
                owner.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                owner.Location = new System.Drawing.Point(-20000, -20000);
                owner.ShowInTaskbar = false;
                owner.Show();
                Command(owner, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F);
                Command(owner, System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.P);
                Check(Descendants(owner).OfType<System.Windows.Forms.ListView>().Single().ContainsFocus, "Alt+P must return from search to inventory products");
                var tabs = Descendants(owner).OfType<System.Windows.Forms.TabControl>().Single();
                Check(tabs.TabPages.Count == 2 && tabs.TabPages[0].Text == "Inventory" && tabs.TabPages[1].Text == "Storage by Company", "Two real tab sheets");
                var inventory = Descendants(tabs.TabPages[0]).ToList();
                CheckFocusCycle(owner, new[] { "Search products", "Rescan", "Reveal serials", "Save HTML report", "All products", "Product serials" }
                    .Select(name => inventory.Single(c => c.AccessibleName == name)).Concat(new[] { tabs }).ToList());
                Command(owner, System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.O);
                var choices = inventory.OfType<System.Windows.Forms.RadioButton>().ToList();
                Check(choices.Single(c => c.AccessibleName == "All products").Focused, "Alt+O must focus the selected Products shown choice");
                DialogKey(HelpDialog.FocusedControl(owner), System.Windows.Forms.Keys.Right);
                Check(choices.Single(c => c.AccessibleName == "With serials").Checked && choices.Single(c => c.AccessibleName == "With serials").Focused, "Native radio arrow must select With serials");
                CheckFocusCycle(owner, new[] { "Search products", "Rescan", "Reveal serials", "Save HTML report", "With serials", "Product serials" }
                    .Select(name => inventory.Single(c => c.AccessibleName == name)).Concat(new[] { tabs }).ToList());
                choices.Single(c => c.AccessibleName == "All products").Checked = true;
                tabs.Focus();
                DialogKey(tabs, System.Windows.Forms.Keys.Right);
                Check(tabs.SelectedIndex == 1, "Native Right arrow must select Storage");
                Check(tabs.Focused, "First lazy Storage-page creation must preserve header focus");
                Command(owner, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.D2);
                Check(tabs.SelectedIndex == 1 && Descendants(tabs.TabPages[1]).OfType<StorageForm>().Any(f => !f.TopLevel), "Control+2 must select an embedded storage page");
                var embedded = Descendants(tabs.TabPages[1]).OfType<StorageForm>().Single();
                var scanField = typeof(StorageForm).GetField("cancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var startupScan = scanField.GetValue(embedded) as CancellationTokenSource;
                if (startupScan != null) startupScan.Cancel();
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (scanField.GetValue(embedded) != null && deadline.ElapsedMilliseconds < 5000) {
                    System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5);
                }
                Check(scanField.GetValue(embedded) == null, "The fixture must finish cancelling its startup scan");
                var storageControls = Descendants(embedded).ToList();
                var storageOrder = new[] { "Company order", "Product order", "Filter products", "Scan storage", "Cancel scan", "Companies", "Company products", "Storage totals", "Scan status" }
                    .Select(name => storageControls.Single(c => c.AccessibleName == name)).ToList();
                foreach (var control in storageOrder) control.Enabled = true;
                CheckFocusCycle(owner, storageOrder.Concat(new[] { tabs }).ToList());
                foreach (int disabledIndex in new[] { 3, 4 }) {
                    storageOrder[disabledIndex].Enabled = false;
                    CheckFocusCycle(owner, storageOrder.Where(c => c.Enabled).Concat(new[] { tabs }).ToList());
                    storageOrder[disabledIndex].Enabled = true;
                }
                for (int repeat = 0; repeat < 3; repeat++) {
                    tabs.Focus();
                    Check(tabs.Focused, "Native selector must accept focus before changing its page");
                    DialogKey(tabs, System.Windows.Forms.Keys.Left);
                    Check(tabs.SelectedIndex == 0, "Native Left arrow must select Inventory");
                    Check(tabs.Focused, "Changing to Inventory at the header must not steal focus into the table");
                    DialogKey(tabs, System.Windows.Forms.Keys.Right);
                    Check(tabs.SelectedIndex == 1, "Native Right arrow must select Storage");
                    Check(tabs.Focused, "Changing to Storage at the header must preserve selector focus");
                }
                Check(HelpDialog.DialogOwner(embedded) == owner, "Embedded-page dialogs must belong to the main window");
                Command(embedded, System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.S);
                Tab(embedded, true);
                Check(tabs.Focused, "Tab from the final storage field must reach the native tab selector");
                Tab(tabs, true);
                Check(Descendants(embedded).Single(c => c.AccessibleName == "Company order").ContainsFocus, "Tab from the selector must enter the first storage control");
                Command(embedded, System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.R);
                Tab(embedded, false);
                Check(tabs.Focused, "Shift+Tab from the first storage field must reach the native tab selector");
                Command(embedded, System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.O);
                Tab(embedded, true);
                Check(Descendants(embedded).Single(c => c.AccessibleName == "Filter products").ContainsFocus, "Tab within the page must still follow the normal order");
                Command(embedded, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.D1);
                Check(tabs.SelectedIndex == 0, "Control+1 must return to inventory");
                Command(owner, System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.D2);
                Command(embedded, System.Windows.Forms.Keys.Escape);
                Check(tabs.SelectedIndex == 0 && !embedded.IsDisposed, "Escape must switch tabs without destroying storage");
                Check(!Descendants(embedded).OfType<System.Windows.Forms.Button>().Any(b => b.AccessibleName == "Close"), "Tab sheets do not need a page Close button");
                view.Show(owner);
                System.Windows.Forms.Application.DoEvents();
                Check(view.Owner == owner && !view.ShowInTaskbar, "Storage must remain an owned, non-taskbar window");
                var controls = Descendants(view).ToList();
                var targets = new Dictionary<System.Windows.Forms.Keys, string> {
                    { System.Windows.Forms.Keys.T, "Storage totals" }, { System.Windows.Forms.Keys.S, "Scan status" },
                    { System.Windows.Forms.Keys.O, "Product order" }, { System.Windows.Forms.Keys.P, "Company products" },
                    { System.Windows.Forms.Keys.C, "Companies" }, { System.Windows.Forms.Keys.R, "Company order" }
                };
                foreach (var target in targets)
                {
                    Command(view, System.Windows.Forms.Keys.Alt | target.Key);
                    Check(controls.Single(c => c.AccessibleName == target.Value).ContainsFocus, "Focus target: Alt+" + target.Key);
                }
                foreach (var table in controls.OfType<System.Windows.Forms.ListView>())
                {
                    var row = table.Items.Add("Fixture row"); row.Selected = true;
                    table.ContextMenuStrip.Show(table, new System.Drawing.Point(0, 0));
                    Check(table.ContextMenuStrip.Items[0].Enabled, "Copy menu must use its own table selection");
                    table.ContextMenuStrip.Close();
                    row.Selected = false;
                    table.ContextMenuStrip.Show(table, new System.Drawing.Point(0, 0));
                    Check(!table.ContextMenuStrip.Items[0].Enabled, "Copy menu must be disabled without a selection");
                    table.ContextMenuStrip.Close();
                }
                var copyMenus = controls.OfType<System.Windows.Forms.ListView>().Select(t => t.ContextMenuStrip).ToList();
                Command(view, System.Windows.Forms.Keys.Escape);
                Check(view.IsDisposed && !owner.IsDisposed && owner.Visible, "Escape must close only storage, leaving inventory intact");
                Check(copyMenus.All(m => m.IsDisposed), "Closing storage must dispose its context menus");
                owner.Close();
            }
        }

        private static void DiscoveryFixtures(string root)
        {
            var roots = new StorageSearchRoots { Plugins = new[] { Path.Combine(root, "Plugins") }, Applications = new[] { Path.Combine(root, "Apps") }, Data = new[] { Path.Combine(root, "Data") } };
            var sizes = new Dictionary<string, int> {
                { "Content\\content.bin", 11 }, { "Plugins\\Maker\\Synth.vst3\\Contents\\payload.bin", 100 },
                { "Plugins\\Maker\\Synth.dll", 17 }, { "Apps\\Maker\\Synth.exe", 29 }, { "Data\\Maker\\Synth\\data.bin", 37 },
                { "Plugins\\Other\\Synth.dll", 71 }, { "Data\\Other\\Synth\\data.bin", 83 },
                { "Plugins\\Maker\\Synth Extra.dll", 91 }
            };
            foreach (var file in sizes) { string path = Path.Combine(root, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, new byte[file.Value]); }
            var synth = new NativeProduct { Name = "Synth", Company = "Maker", ContentDir = Path.Combine(root, "Content"), InstallDir = roots.Plugins[0] };
            var result = StorageScanner.Scan(new[] { synth, new NativeProduct { Name = "Shared", Company = "Maker", ContentDir = synth.ContentDir } }, CancellationToken.None, null, roots);
            Check(result.Rows[0].Bytes == 194 && result.TotalBytes == 194 && result.Companies[0].Bytes == 194, "Verified multiple formats and shared content union");
            Check(result.Rows[0].Locations.Count == 5 && !result.Rows[0].LocationSummary.Contains("Other") && !result.Rows[0].LocationSummary.Contains("Extra"), "Exact product and publisher matching");
            Check(StorageReport.Html(result, result.Rows).Contains("Synth.exe"), "All counted locations must be reportable");
            var broad = StorageScanner.Scan(new[] { new NativeProduct { Name = "Unmatched", Company = "Maker", ContentDir = roots.Plugins[0] } }, CancellationToken.None, null, roots);
            Check(broad.TotalBytes == 0 && broad.Rows[0].State == "Unsafe broad location", "Do not attribute a whole shared plug-in root");
            var nested = new NativeProduct { Name = "Nested", Company = "Maker", ContentDir = synth.ContentDir, InstallDir = Path.Combine(synth.ContentDir, "content.bin") };
            Check(Scan(new[] { nested }, CancellationToken.None).TotalBytes == 11, "File roots nested in directory roots count once");
            Check(StorageScanner.FolderToOpen(nested.InstallDir) == synth.ContentDir, "Opening a file location must open its folder, not execute it");
            Check(StorageScanner.FolderToOpen(Path.Combine(root, "Plugins", "Maker", "Synth.vst3")) == Path.Combine(root, "Plugins", "Maker"), "Bundle locations must open their parent folder");
            nested.ContentDir = Path.Combine(root, "Offline");
            Check(Scan(new[] { nested }, CancellationToken.None).Rows[0].State == "Partial", "Missing content plus a working binary is partial");
            Directory.CreateDirectory(nested.ContentDir);
            Check(Scan(new[] { nested }, CancellationToken.None).Rows[0].State == "Measured", "Reconnected content is measured on rescan");
            Check(Scan(new[] { new NativeProduct { Name = "Relative", ContentDir = "C:relative" } }, CancellationToken.None).Rows[0].State == "Unavailable", "Drive-relative paths are not absolute");
            string metadataFile = Path.Combine(root, "Plugins", "UnexpectedFilename.exe");
            File.Copy(typeof(StorageTests).Assembly.Location, metadataFile);
            var metadata = StorageScanner.Scan(new[] { new NativeProduct { Name = "Native Serials", Company = "Andre Louis Ltd" } }, CancellationToken.None, null, roots);
            Check(metadata.Rows[0].Bytes == new FileInfo(metadataFile).Length && metadata.Rows[0].Locations.Count == 1, "Version metadata and publisher legal suffix matching without loading an executable");
            var mismatch = StorageScanner.Scan(new[] { new NativeProduct { Name = "Native Serials", Company = "Someone Else" } }, CancellationToken.None, null, roots);
            Check(mismatch.TotalBytes == 0 && mismatch.Rows[0].State == "No location", "Metadata requires matching publisher proof");
            for (int i = 0; i < 20; i++)
                Check(StorageScanner.Scan(new[] { synth }, CancellationToken.None, null, roots).TotalBytes == 194, "Repeated scans must not retain previous locations or counts");
            string aax = Path.Combine(root, "Plugins", "Maker", "Synth.aaxplugin");
            Directory.CreateDirectory(Path.Combine(aax, "Contents", "x64"));
            File.WriteAllBytes(Path.Combine(aax, "Contents", "x64", "Synth.aaxplugin"), new byte[23]);
            var discovered = StorageDiscovery.Discover(new[] { synth }, roots, CancellationToken.None);
            Check(discovered[0].Contains(aax), "AAX bundles must be discovered with publisher evidence");
            var paths = discovered[0].Concat(new[] { synth.ContentDir, Path.Combine(root, "Missing.vst3") });
            var actions = FolderActions.ForPaths(paths, synth.ContentDir);
            foreach (string name in new[] { "Open VST3 Folder", "Open VST2 Folder", "Open AAX Folder", "Open Application Folder", "Open Content Folder" })
                Check(actions.Any(a => a.Label.Replace("&", "") == name), "Folder action: " + name);
            Check(actions.All(a => Directory.Exists(a.Folder) && !a.Folder.EndsWith(".aaxplugin")), "Actions open existing containing folders, never executables/bundles");
            Check(FolderActions.ForPaths(new[] { aax, aax }).Count == 1, "Duplicate locations must not duplicate actions");
            Check(actions.Single(a => a.Label.Replace("&", "") == "Open AAX Folder").Item == aax, "Retain the plug-in item for Explorer selection");
            Check(StorageScanner.ItemToReveal(Path.Combine(aax, "Contents", "x64", "Synth.aaxplugin")) == aax, "Reveal the outer package, never its executable");
            Check(actions.Single(a => a.Label.Replace("&", "") == "Open Content Folder").Item == null, "Ordinary content directories open normally");
            string nks = Path.Combine(root, "Libraries", "Tape Percussion", "NKS");
            Directory.CreateDirectory(nks);
            File.WriteAllBytes(Path.Combine(nks, "preset"), new byte[3]);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(nks), "sample"), new byte[31]);
            var tape = new NativeProduct { Name = "Tape Percussion", Company = "SA Recordings", ContentDir = nks };
            Check(Scan(new[] { tape }, CancellationToken.None).TotalBytes == 34, "An exact product NKS parent includes its content");
            tape.Name = "Different product";
            Check(Scan(new[] { tape }, CancellationToken.None).TotalBytes == 3, "Never include an unrelated NKS parent");
            var menu = new System.Windows.Forms.ToolStripMenuItem("&Actions");
            FolderActions.Populate(menu.DropDownItems, actions, delegate { throw new Exception("Tests must not open real folders"); });
            CheckMenu(menu);
            menu.Dispose();
            var many = Enumerable.Range(0, 12).Select(i => new FolderAction { Label = "Open VST&3", Folder = "Fixture & folder " + i }).ToList();
            using (var parent = new System.Windows.Forms.ToolStripMenuItem("&Actions")) {
                FolderActions.Populate(parent.DropDownItems, many, delegate { });
                var group = (System.Windows.Forms.ToolStripMenuItem)parent.DropDownItems[0];
                Check(group.DropDownItems.Count == 10, "Long location lists need a bounded More submenu");
                CheckMenu(group);
                CheckMenu((System.Windows.Forms.ToolStripMenuItem)group.DropDownItems[9]);
            }
        }

        private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root)
        {
            foreach (System.Windows.Forms.Control child in root.Controls)
            {
                yield return child;
                foreach (var item in Descendants(child)) yield return item;
            }
        }

        private static void CheckUpdateOffer()
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var release = typeof(UpdateService).GetMethod("ParseRelease", flags).Invoke(null, new object[] {
                "{\"tag_name\":\"v1.2\",\"html_url\":\"https://github.com/OnjLouis/NativeSerials/releases/tag/v1.2\",\"body\":\"Changes\",\"draft\":false,\"prerelease\":false,\"assets\":[" + string.Join(",", new[] { "", ".json", ".json.sig" }.Select(s => "{\"name\":\"NativeSerialsWindows-1.2.zip" + s + "\",\"size\":1,\"browser_download_url\":\"https://github.com/OnjLouis/NativeSerials/releases/download/v1.2/NativeSerialsWindows-1.2.zip" + s + "\"}")) + "]}"
            });
            var releases = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(release.GetType()));
            releases.Add(release);
            using (var dialog = (System.Windows.Forms.Form)typeof(UpdateService).GetMethod("CreateUpdateAvailableDialog", flags).Invoke(null,
                new object[] { null, release, releases, new Version(1, 1), new Version(1, 2), (Action)delegate { } }))
            {
                CheckLayout(dialog);
                var controls = Descendants(dialog).ToList();
                Check(controls.All(c => string.IsNullOrEmpty(c.AccessibleDescription)), "Update focus must not repeat F1 explanations");
                foreach (var control in controls.Where(c => c is System.Windows.Forms.TextBox || c is System.Windows.Forms.Button))
                    Check(!string.IsNullOrEmpty(HelpDialog.Description(control, "")), "Update controls need F1 content");
                var keys = new HashSet<char>();
                foreach (var button in controls.OfType<System.Windows.Forms.Button>())
                {
                    int index = button.Text.IndexOf('&');
                    Check(index >= 0 && keys.Add(char.ToUpperInvariant(button.Text[index + 1])), "Unique update-offer mnemonics");
                }
            }
        }

        private static void CheckLayout(System.Windows.Forms.Form form)
        {
            Check(form.MinimumSize.Width > 0 && form.MinimumSize.Height > 0, form.Text + " needs a safe minimum size");
            var original = form.Size;
            var originalFont = form.Font;
            form.CreateControl();
            foreach (var control in Descendants(form)) { var handle = control.Handle; }
            foreach (float factor in new[] { 1f, 1.5f, 2f })
            using (var font = new System.Drawing.Font(originalFont.FontFamily, originalFont.Size * factor))
            {
                form.Font = font;
                foreach (var size in new[] { form.MinimumSize, original, new System.Drawing.Size(1000, 700) })
                {
                    form.Size = size;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        foreach (var container in Descendants(form).Reverse()) container.PerformLayout();
                        form.PerformLayout();
                    }
                    foreach (var control in Descendants(form))
                    {
                        var page = control as System.Windows.Forms.TabPage;
                        if (page != null && ((System.Windows.Forms.TabControl)page.Parent).SelectedTab != page) continue;
                        if (control.Parent is System.Windows.Forms.ListView || control is System.Windows.Forms.MenuStrip || control is System.Windows.Forms.StatusStrip) continue;
                        Check(control.Parent.ClientRectangle.Contains(control.Bounds), form.Text + ": clipped " + (control.AccessibleName ?? control.Text) + " " + control.Bounds + " in " + control.Parent.ClientRectangle + "; preferred " + control.PreferredSize + "; minimum " + control.MinimumSize + "; font " + control.Font.Size + "; factor " + factor);
                        if (control is System.Windows.Forms.Label || control is System.Windows.Forms.Button || control is System.Windows.Forms.CheckBox)
                            Check(control.GetPreferredSize(System.Drawing.Size.Empty).Width <= control.Width && control.GetPreferredSize(System.Drawing.Size.Empty).Height <= control.Height,
                                form.Text + ": text does not fit " + control.Text + " at font factor " + factor);
                        var choices = control as System.Windows.Forms.ComboBox;
                        if (choices != null)
                            foreach (var item in choices.Items)
                                Check(System.Windows.Forms.TextRenderer.MeasureText(item.ToString(), choices.Font).Width + System.Windows.Forms.SystemInformation.VerticalScrollBarWidth <= choices.ClientSize.Width,
                                    form.Text + ": choice text clipped: " + item + " at font factor " + factor);
                        foreach (System.Windows.Forms.Control sibling in control.Parent.Controls)
                            if (!(control is System.Windows.Forms.TabPage && sibling is System.Windows.Forms.TabPage))
                            if (sibling != control && !(sibling is System.Windows.Forms.MenuStrip) && !(control is System.Windows.Forms.MenuStrip))
                                Check(!control.Bounds.IntersectsWith(sibling.Bounds), form.Text + ": overlapping " + control.GetType().Name + " and " + sibling.GetType().Name);
                    }
                }
                form.Font = originalFont;
            }
            form.Size = original;
        }

        private static void CheckMenu(System.Windows.Forms.ToolStripMenuItem root)
        {
            var used = new HashSet<char>();
            foreach (System.Windows.Forms.ToolStripItem item in root.DropDownItems)
            {
                var command = item as System.Windows.Forms.ToolStripMenuItem;
                if (command != null && command.ShortcutKeys != System.Windows.Forms.Keys.None)
                    Check(command.ShowShortcutKeys && !string.IsNullOrEmpty(command.ShortcutKeyDisplayString) &&
                        command.AccessibleName.EndsWith(command.ShortcutKeyDisplayString), "Menu shortcuts must be visible and included in the spoken name");
                int index = item.Text.IndexOf('&');
                if (command != null && command.Enabled) Check(index >= 0, "Each menu command needs a mnemonic: " + item.Text);
                if (index >= 0 && index + 1 < item.Text.Length)
                    Check(used.Add(char.ToUpperInvariant(item.Text[index + 1])), "Unique menu mnemonics");
            }
        }
    }
}
