using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NativeSerials
{
    internal sealed class StorageForm : Form
    {
        private readonly ListView companies = Table("Companies", "Select a company to show its products and total.", "Company", "Products", "Size", "Fully measured");
        private readonly ListView libraries = Table("Company products", "Product sizes, scan status and folder locations.", "Product", "Size", "Status", "Location");
        private readonly ComboBox ranking = Choice("Company order", "Rank companies by total size, product count or name.", "Largest first", "Most products", "Company name");
        private readonly ComboBox ordering = Choice("Product order", "Sort product sizes numerically or sort by name.", "Largest first", "Smallest first", "Product name");
        private readonly TextBox filter = new ShortcutTextBox { Width = 180, ShortcutText = "Ctrl+F", AccessibleName = "Filter products", AccessibleDescription = "Filter the selected company's products by name or folder." };
        private readonly Button scan = new ShortcutButton { Text = "Scan", ShortcutText = "Ctrl+R", AutoSize = true, AccessibleName = "Scan storage", AccessibleDescription = "Refresh storage sizes in the background." };
        private readonly Button cancel = new Button { Text = "Ca&ncel", AutoSize = true, Enabled = false, AccessibleName = "Cancel scan", AccessibleDescription = "Cancel the current scan and keep previous completed results." };
        internal Action ReturnToInventory;
        private readonly TextBox total = new ShortcutTextBox { ShortcutText = "Alt+T", ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Storage totals", AccessibleDescription = "Company totals include all its products, even when the product filter is active. Shared locations count once per company and once overall; company totals may overlap. Sizes are logical file sizes, not reclaimable space.", Multiline = true, TabStop = true, WordWrap = true, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox status = new ShortcutTextBox { ShortcutText = "Alt+S", ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Scan status", AccessibleDescription = "Current scan progress or completion state. Products needing attention have missing, unavailable or partly measured locations.", Multiline = true, TabStop = true, WordWrap = true, ScrollBars = ScrollBars.Vertical };
        private StorageResult result;
        private List<StorageRow> visible = new List<StorageRow>();
        private CancellationTokenSource cancellation;
        private ListView copyTable;
        private readonly ToolStripMenuItem export = MenuCommand.Create("&Save HTML Report...", null, Keys.Control | Keys.S);

        public StorageForm()
        {
            Text = "Storage by Company - Native Serials";
            Width = 1150; Height = 760; MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            companies.MultiSelect = false;
            copyTable = companies;
            companies.Enter += delegate { copyTable = companies; };
            libraries.Enter += delegate { copyTable = libraries; };
            MenuCommand.AddCopyMenu(companies, delegate { CopyRows(companies); });
            MenuCommand.AddCopyMenu(libraries, delegate { CopyRows(libraries); });
            FolderActions.AddToTable(libraries, SelectedFolders, OpenProductFolder);
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            export.ShortcutKeys = Keys.Control | Keys.S;
            export.Click += delegate { Export(); };
            export.Enabled = false;
            var rescan = MenuCommand.Create("&Rescan", delegate { StartScan(); }, Keys.Control | Keys.R);
            file.DropDownItems.Add(rescan);
            file.DropDownItems.Add(export);
            file.DropDownItems.Add(MenuCommand.Create("&Open Selected Folder", delegate { OpenFolder(); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(MenuCommand.Create("&Copy Selected Rows", delegate { Copy(); }, Keys.Control | Keys.C));
            file.DropDownItems.Add(MenuCommand.Create("&Find Products", delegate { filter.Focus(); filter.SelectAll(); }, Keys.Control | Keys.F));
            file.DropDownItems.Add(MenuCommand.Create("C&lose", delegate { LeaveStorage(); }, Keys.None, "Esc"));
            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(MenuCommand.Create("Context &Help", delegate { ContextHelp(); }, Keys.F1));
            help.DropDownItems.Add(new ToolStripMenuItem("Open &Manual", null, delegate { OpenManual(); }));
            help.DropDownItems.Add(MenuCommand.Create("D&iagnostics...", delegate { ShowDiagnostics(); }, Keys.Alt | Keys.F1));
            help.DropDownItems.Add(MenuCommand.Create("Check for &Updates", delegate { UpdateService.CheckForUpdates(this, "1.1", delegate { Application.Exit(); }); }, Keys.Shift | Keys.F1));
            help.DropDownItems.Add(MenuCommand.Create("&Project Page", delegate { UpdateService.OpenProjectPage(); }, Keys.Control | Keys.F1));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add(new ToolStripMenuItem("&Donate", null, delegate { UpdateService.OpenDonatePage(); }));
            var actions = new ToolStripMenuItem("&Actions");
            actions.DropDownOpening += delegate {
                FolderActions.Clear(actions.DropDownItems);
                FolderActions.Populate(actions.DropDownItems, SelectedFolders(), OpenProductFolder);
            };
            menu.Items.Add(file); menu.Items.Add(actions); menu.Items.Add(help); MainMenuStrip = menu;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            Add(controls, "&Rank:", ranking); Add(controls, "Product &order:", ordering); Add(controls, "Filter:", filter);
            controls.Controls.Add(scan); controls.Controls.Add(cancel);
            layout.Controls.Add(controls, 0, 0);
            layout.Controls.Add(new Label { Text = "&Companies", AutoSize = true }, 0, 1);
            layout.Controls.Add(companies, 0, 2);
            layout.Controls.Add(new Label { Text = "&Products", AutoSize = true }, 0, 3);
            layout.Controls.Add(libraries, 0, 4);
            layout.Controls.Add(Information("Storage &totals:", total), 0, 5);
            layout.Controls.Add(Information("Scan &status:", status), 0, 6);
            Controls.Add(layout); Controls.Add(menu);
            HelpDialog.Prepare(this);
            companies.SelectedIndexChanged += delegate { ShowProducts(); };
            ranking.SelectedIndexChanged += delegate { ShowCompanies(); };
            ordering.SelectedIndexChanged += delegate { ShowProducts(); };
            filter.TextChanged += delegate { ShowProducts(); };
            scan.Click += delegate { StartScan(); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            libraries.DoubleClick += delegate { OpenFolder(); };
            Shown += delegate { if (TopLevel) companies.Focus(); StartScan(); };
            FormClosing += delegate { if (cancellation != null) cancellation.Cancel(); };
        }

        internal void FocusProducts() { libraries.Focus(); }
        internal bool FocusEdge(bool forward) { return forward ? ranking.Focus() : status.Focus(); }
        internal void FocusSearch() { filter.Focus(); filter.SelectAll(); }
        internal void RefreshStorage() { StartScan(); }
        internal void SaveReport() { Export(); }
        internal void CopySelection() { Copy(); }
        internal void Diagnostics() { ShowDiagnostics(); }
        private void LeaveStorage() { if (ReturnToInventory != null) ReturnToInventory(); else Close(); }

        private static Control Information(string name, TextBox text)
        {
            var group = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            group.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left }, 0, 0);
            group.Controls.Add(text, 1, 0);
            Action resize = delegate {
                text.MinimumSize = new Size(0, text.Font.Height * 4 + 12);
                group.MinimumSize = new Size(0, text.MinimumSize.Height + text.Margin.Vertical);
            };
            text.FontChanged += delegate { resize(); };
            resize();
            return group;
        }

        private static void Add(FlowLayoutPanel panel, string name, Control control)
        {
            var group = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left };
            group.Controls.Add(label, 0, 0);
            group.Controls.Add(control, 1, 0);
            int minimumWidth = control.Width;
            // Native combo height can change before the table's preferred row height.
            group.Layout += delegate {
                var choices = control as ComboBox;
                if (choices != null)
                    choices.Width = Math.Max(minimumWidth, choices.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), choices.Font).Width) +
                        SystemInformation.VerticalScrollBarWidth + 2 * SystemInformation.Border3DSize.Width);
                group.MinimumSize = new Size(0, Math.Max(control.PreferredSize.Height + control.Margin.Vertical,
                    label.PreferredSize.Height + label.Margin.Vertical));
            };
            panel.Controls.Add(group);
        }

        private static ComboBox Choice(string name, string help, params string[] values)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, AccessibleName = name, AccessibleDescription = help };
            box.Items.AddRange(values); box.SelectedIndex = 0; return box;
        }

        private static ListView Table(string name, string help, params string[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, AccessibleName = name, AccessibleDescription = help };
            foreach (string column in columns) list.Columns.Add(column, column == "Location" ? 420 : column == "Company" || column == "Product" ? 290 : 140);
            return list;
        }

        private async void StartScan()
        {
            if (cancellation != null) return;
            var current = new CancellationTokenSource();
            cancellation = current; scan.Enabled = false; cancel.Enabled = true;
            status.Text = "Scanning storage...";
            var timer = Stopwatch.StartNew();
            var progress = new Progress<string>(path => { if (!IsDisposed && cancellation == current) status.Text = "Scanning: " + path; });
            try
            {
                var next = await Task.Run(() => StorageScanner.Scan(new NativeScanner().Scan().Products, current.Token, path => {
                    if (timer.ElapsedMilliseconds < 1000) return;
                    timer.Restart(); ((IProgress<string>)progress).Report(path);
                }));
                if (IsDisposed) return;
                result = next; export.Enabled = true;
                ShowCompanies();
                status.Text = "Scan complete." + Environment.NewLine + "Products: " + result.Rows.Count + "." + Environment.NewLine + "Companies: " + result.Companies.Count + "." + Environment.NewLine + "Need attention: " +
                    result.Rows.Count(r => r.State != "Measured") + ".";
                AccessibilityNotifyClients(AccessibleEvents.SystemAlert, -1);
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Scan cancelled."; }
            catch (Exception ex) { if (!IsDisposed) status.Text = "Scan failed: " + ex.Message; }
            finally
            {
                cancellation = null; current.Dispose();
                if (!IsDisposed) { scan.Enabled = true; cancel.Enabled = false; }
            }
        }

        private void ShowCompanies()
        {
            if (result == null) return;
            string selected = companies.SelectedItems.Count > 0 ? companies.SelectedItems[0].Text : null;
            IEnumerable<StorageCompany> rows = result.Companies.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
            if (ranking.SelectedIndex == 0) rows = rows.OrderByDescending(c => c.Bytes);
            if (ranking.SelectedIndex == 1) rows = rows.OrderByDescending(c => c.Count);
            companies.BeginUpdate();
            companies.Items.Clear();
            foreach (var company in rows)
            {
                var item = new ListViewItem(new[] { company.Name, company.Count.ToString(), StorageReport.CompanySize(company), company.MeasuredCount + " of " + company.Count });
                item.Tag = company; companies.Items.Add(item);
                if (company.Name == selected) item.Selected = true;
            }
            if (companies.SelectedItems.Count == 0 && companies.Items.Count > 0) companies.Items[0].Selected = true;
            companies.EndUpdate();
            ShowProducts();
        }

        private void ShowProducts()
        {
            if (result == null || companies.SelectedItems.Count == 0) return;
            var company = (StorageCompany)companies.SelectedItems[0].Tag;
            string selected = libraries.SelectedItems.Count > 0 ? libraries.SelectedItems[0].Text : null;
            string query = filter.Text.Trim();
            IEnumerable<StorageRow> rows = result.Rows.Where(r => string.Equals(r.Company, company.Name, StringComparison.OrdinalIgnoreCase) &&
                (r.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || r.LocationSummary.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
            if (ordering.SelectedIndex == 0) rows = rows.OrderBy(r => !StorageReport.HasSize(r)).ThenByDescending(r => r.Bytes);
            if (ordering.SelectedIndex == 1) rows = rows.OrderBy(r => !StorageReport.HasSize(r)).ThenBy(r => r.Bytes);
            visible = rows.ToList();
            libraries.BeginUpdate(); libraries.Items.Clear();
            foreach (var row in visible)
            {
                var item = new ListViewItem(new[] { row.Name, StorageReport.HasSize(row) ? StorageReport.Size(row.Bytes) : "Unavailable", row.State, row.LocationSummary });
                item.Tag = row; libraries.Items.Add(item);
                if (row.Name == selected) item.Selected = true;
            }
            libraries.EndUpdate();
            total.Text = company.Name + ": " + StorageReport.CompanySize(company) + "." + Environment.NewLine + "Products: " + company.Count + "." + Environment.NewLine + "Shown: " + visible.Count + "." + Environment.NewLine +
                "All companies: " + StorageReport.Size(result.TotalBytes) + ".";
        }

        private void Export()
        {
            if (result == null) return;
            using (var dialog = new SaveFileDialog { Filter = "HTML report|*.html", FileName = "NativeSerialsStorage.html" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    try { File.WriteAllText(dialog.FileName, StorageReport.Html(result, result.Rows.OrderBy(r => r.Company).ThenByDescending(r => r.Bytes).ToList()), new System.Text.UTF8Encoding(false)); status.Text = "Saved storage report."; }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }

        private void OpenFolder()
        {
            if (libraries.SelectedItems.Count == 0) return;
            var row = (StorageRow)libraries.SelectedItems[0].Tag;
            if (!StorageReport.HasSize(row)) { status.Text = "This location is unavailable."; return; }
            try { FolderActions.Open(row.Path); }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        internal IList<FolderAction> SelectedFolders()
        {
            if (libraries.SelectedItems.Count != 1) return new List<FolderAction>();
            var row = libraries.SelectedItems[0].Tag as StorageRow;
            return row == null ? new List<FolderAction>() : FolderActions.ForPaths(row.Locations, row.Path);
        }

        internal void OpenProductFolder(FolderAction action)
        {
            try { FolderActions.Open(action); }
            catch (Exception ex) { status.Text = "Could not open folder: " + ex.Message; }
        }

        private void Copy()
        {
            CopyRows(copyTable);
        }

        private static void CopyRows(ListView list)
        {
            var text = string.Join(Environment.NewLine, list.SelectedItems.Cast<ListViewItem>().Select(item => string.Join("\t", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))));
            if (text.Length > 0) Clipboard.SetText(text);
        }

        private void ContextHelp()
        {
            HelpDialog.ShowContext(this,
                "Storage scans registered content and installation locations, plus verified product plug-ins, applications and data. The Location column lists counted paths. Shared folders count once per company and once overall; company totals may overlap. Sizes are file sizes, not reclaimable space. Control+R scans; Control+F filters; Control+S saves a report without serials. Escape closes this help dialog. Open Manual opens the full guide.",
                OpenManual);
        }

        private void OpenManual()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Manual.html")) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open manual", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ShowDiagnostics()
        {
            string text = "Native Serials 1.1" + Environment.NewLine + "Storage scan: " +
                (cancellation != null ? "running" : "idle") + Environment.NewLine;
            if (result == null) text += "No completed storage scan.";
            else
            {
                text += result.Rows.Count + " registered products; " + result.Companies.Count + " companies." + Environment.NewLine +
                    "Overall measured size: " + StorageReport.Size(result.TotalBytes) + Environment.NewLine;
                foreach (var group in result.Rows.GroupBy(r => r.State).OrderBy(g => g.Key))
                    text += group.Key + ": " + group.Count() + Environment.NewLine;
                text += "Shared locations: " + (result.HasOverlap ? "yes" : "no");
            }
            using (var viewer = new TextViewerForm("Storage Diagnostics", text, "Storage diagnostics", "Review scan state, measured size and product counts by measurement status. Use Copy All to copy the complete report without serial numbers.", true, OpenManual))
                viewer.ShowDialog(HelpDialog.DialogOwner(this));
        }

        protected override bool ProcessTabKey(bool forward)
        {
            var page = Parent as TabPage;
            var tabs = page == null ? null : page.Parent as TabControl;
            if (TopLevel || tabs == null) return base.ProcessTabKey(forward);
            if (SelectNextControl(HelpDialog.FocusedControl(this), forward, true, true, false)) return true;
            return tabs.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.T)) { total.Focus(); total.SelectionStart = 0; total.SelectionLength = 0; return true; }
            if (keyData == (Keys.Alt | Keys.S)) { status.Focus(); status.SelectionStart = 0; status.SelectionLength = 0; return true; }
            if (keyData == (Keys.Alt | Keys.O)) { ordering.Focus(); return true; }
            if (keyData == (Keys.Alt | Keys.P)) { libraries.Focus(); return true; }
            if (keyData == (Keys.Alt | Keys.C)) { companies.Focus(); return true; }
            if (keyData == (Keys.Alt | Keys.R)) { ranking.Focus(); return true; }
            if (keyData == (Keys.Alt | Keys.F1)) { ShowDiagnostics(); return true; }
            if (keyData == (Keys.Control | Keys.C) && (filter.Focused || total.Focused || status.Focused))
            {
                (filter.Focused ? filter : total.Focused ? total : status).Copy();
                return true;
            }
            if (keyData == Keys.Escape) { LeaveStorage(); return true; }
            if (!TopLevel && keyData == (Keys.Alt | Keys.F4)) { ((Form)TopLevelControl).Close(); return true; }
            if (keyData == Keys.F1) { ContextHelp(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { filter.Focus(); filter.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
