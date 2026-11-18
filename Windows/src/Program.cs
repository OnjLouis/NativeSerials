using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

namespace NativeSerials
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            if (UpdateService.TryApplyUpdate(Environment.GetCommandLineArgs()))
            {
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly NativeScanner scanner = new NativeScanner();
        private readonly ListView productsList;
        private readonly TextBox searchBox;
        private readonly CheckBox revealSerials;
        private readonly RadioButton allProducts, withSerials, withoutSerials;
        private readonly StatusStrip status;
        private readonly ToolStripStatusLabel statusText;
        private List<NativeProduct> products = new List<NativeProduct>();
        private List<NativeProduct> filteredProducts = new List<NativeProduct>();
        private ScanSummary summary;
        private StorageForm storageWindow;
        private readonly PageTabs pages = new PageTabs { Dock = DockStyle.Fill, AccessibleName = "Views", AccessibleDescription = "Use Left and Right to select a tab, then Tab to enter its controls. Control+1 opens Inventory; Control+2 opens Storage by Company." };
        private readonly TabPage inventoryPage = new TabPage("Inventory");
        private readonly TabPage storagePage = new TabPage("Storage by Company");
        private bool StorageActive { get { return pages.SelectedTab == storagePage && storageWindow != null; } }
        private Dictionary<NativeProduct, List<string>> folderLocations = new Dictionary<NativeProduct, List<string>>();
        private CancellationTokenSource folderScan;

        public MainForm()
        {
            Text = "Native Serials";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1120;
            Height = 700;
            MinimumSize = new Size(820, 720);
            KeyPreview = true;

            var menu = BuildMenu();
            MainMenuStrip = menu;
            Controls.Add(menu);

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 42),
                Padding = new Padding(8, 8, 8, 0),
                WrapContents = true
            };

            var searchLabel = new Label
            {
                Text = "Search:",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 5, 0, 0)
            };
            searchBox = new ShortcutTextBox
            {
                ShortcutText = "Ctrl+F",
                Width = 360,
                AccessibleName = "Search products",
                AccessibleDescription = "Type a product name, company, serial, path, UPID, or SNPID to filter the list."
            };
            searchBox.TextChanged += delegate { ApplyFilter(); };

            var rescanButton = new ShortcutButton
            {
                Text = "Rescan", ShortcutText = "Ctrl+R",
                AutoSize = true,
                Width = 90,
                AccessibleName = "Rescan",
                AccessibleDescription = "Scan local Native Instruments metadata again."
            };
            rescanButton.Click += delegate { Rescan(); };

            revealSerials = new CheckBox
            {
                Text = "Re&veal serials",
                AutoSize = true,
                AccessibleName = "Reveal serials",
                AccessibleDescription = "When checked, serial numbers are shown in the table and included when copying rows."
            };
            revealSerials.CheckedChanged += delegate { PopulateList(); UpdateStatus(); };

            allProducts = new RadioButton { Text = "All products", AutoSize = true, Checked = true, AccessibleName = "All products", AccessibleDescription = "Show every registered product matching Search. Use Left and Right to change the Products shown selection. Alt+O focuses this group." };
            withSerials = new RadioButton { Text = "With serials", AutoSize = true, AccessibleName = "With serials", AccessibleDescription = "Show products with a recovered serial number, whether serials are revealed or hidden. Search still applies." };
            withoutSerials = new RadioButton { Text = "Without serials", AutoSize = true, AccessibleName = "Without serials", AccessibleDescription = "Show products without a recovered serial number. Search still applies; a missing serial does not prove that a product is unlicensed." };
            EventHandler filterChanged = delegate(object sender, EventArgs args) { if (((RadioButton)sender).Checked) ApplyFilter(); };
            foreach (var choice in new[] { allProducts, withSerials, withoutSerials }) choice.CheckedChanged += filterChanged;

            var exportButton = new ShortcutButton
            {
                Text = "Save HTML Report", ShortcutText = "Ctrl+S",
                AutoSize = true,
                Width = 140,
                AccessibleName = "Save HTML report",
                AccessibleDescription = "Save the current filtered product list as an HTML report."
            };
            exportButton.Click += delegate { ExportHtml(); };

            topPanel.Controls.Add(searchLabel);
            topPanel.Controls.Add(searchBox);
            topPanel.Controls.Add(rescanButton);
            topPanel.Controls.Add(revealSerials);
            topPanel.Controls.Add(exportButton);
            Controls.Add(topPanel);
            var serialGroup = new GroupBox { Text = "Products sh&own", AccessibleName = "Products shown", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), TabStop = false };
            var serialChoices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, TabStop = false };
            serialChoices.Controls.AddRange(new Control[] { allProducts, withSerials, withoutSerials });
            serialGroup.Controls.Add(serialChoices);
            Controls.Add(serialGroup);

            productsList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                AccessibleName = "Product serials",
                AccessibleDescription = "Installed products, libraries, and local Native Access license tokens. Serials are hidden until Reveal serials is checked."
            };
            productsList.Columns.Add("Product", 240);
            productsList.Columns.Add("Serial", 210);
            productsList.Columns.Add("Version", 90);
            productsList.Columns.Add("Company", 140);
            productsList.Columns.Add("Type", 100);
            productsList.Columns.Add("Path", 360);
            productsList.Columns.Add("UPID", 260);
            productsList.DoubleClick += delegate { CopySelectedRows(); };
            productsList.KeyDown += ProductsListKeyDown;
            MenuCommand.AddCopyMenu(productsList, delegate { CopySelectedRows(); });
            FolderActions.AddToTable(productsList, SelectedFolders, OpenProductFolder);
            Controls.Add(productsList);
            var productsLabel = new Label { Text = "&Products", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 4, 8, 4) };
            Controls.Add(productsLabel);
            topPanel.TabIndex = 0; serialGroup.TabIndex = 1; productsLabel.TabIndex = 2; productsList.TabIndex = 3;

            status = new StatusStrip();
            statusText = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            status.Items.Add(statusText);
            Controls.Add(status);
            Controls.SetChildIndex(productsList, 0);
            Controls.SetChildIndex(productsLabel, 1);
            Controls.SetChildIndex(serialGroup, 2);
            Controls.SetChildIndex(topPanel, 3);
            Controls.SetChildIndex(status, 4);
            Controls.SetChildIndex(menu, 5);
            foreach (Control control in new Control[] { productsList, productsLabel, serialGroup, topPanel, status }) {
                Controls.Remove(control); inventoryPage.Controls.Add(control);
            }
            inventoryPage.Controls.SetChildIndex(productsList, 0);
            inventoryPage.Controls.SetChildIndex(productsLabel, 1);
            inventoryPage.Controls.SetChildIndex(serialGroup, 2);
            inventoryPage.Controls.SetChildIndex(topPanel, 3);
            inventoryPage.Controls.SetChildIndex(status, 4);
            pages.TabPages.Add(inventoryPage); pages.TabPages.Add(storagePage);
            pages.EnterPage = forward => StorageActive ? storageWindow.FocusEdge(forward) : (forward ? searchBox.Focus() : productsList.Focus());
            Controls.Add(pages); Controls.SetChildIndex(pages, 0);
            pages.SelectedIndexChanged += delegate {
                if (pages.SelectedTab == storagePage) EnsureStorage();
            };
            HelpDialog.Prepare(this);
            HelpDialog.Attach(this, OpenManual);
            FormClosed += delegate { if (folderScan != null) folderScan.Cancel(); };

            Shown += delegate
            {
                Rescan();
                productsList.Focus();
                UpdateService.CheckForUpdatesAutomatic(this, AppVersion(), delegate { Close(); });
            };
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(MenuCommand.Create("&Rescan", delegate { Rescan(); }, Keys.Control | Keys.R));
            var saveHtmlItem = MenuCommand.Create("&Save HTML Report...", delegate { ExportHtml(); }, Keys.Control | Keys.S);
            file.DropDownItems.Add(saveHtmlItem);
            file.DropDownItems.Add(MenuCommand.Create("&Copy Selected Rows", delegate { CopySelectedRows(); }, Keys.Control | Keys.C));
            file.DropDownItems.Add(MenuCommand.Create("Open Selected &Folder", delegate { var folders = SelectedFolders(); if (folders.Count > 0) OpenProductFolder(folders[0]); }, Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(MenuCommand.Create("E&xit", delegate { Close(); }, Keys.None, "Alt+F4"));
            menu.Items.Add(file);

            var views = new ToolStripMenuItem("V&iew");
            views.DropDownItems.Add(MenuCommand.Create("&Inventory", delegate { ShowInventory(); }, Keys.Control | Keys.D1));
            views.DropDownItems.Add(MenuCommand.Create("&Storage by Company...", delegate { ShowStorage(); }, Keys.Control | Keys.D2));
            views.DropDownItems.Add(MenuCommand.Create("&Find Products", delegate { FocusSearch(); }, Keys.Control | Keys.F));
            menu.Items.Add(views);
            var actions = new ToolStripMenuItem("&Actions");
            actions.DropDownOpening += delegate {
                FolderActions.Clear(actions.DropDownItems);
                FolderActions.Populate(actions.DropDownItems, SelectedFolders(), OpenProductFolder);
            };
            menu.Items.Add(actions);

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(MenuCommand.Create("Context &Help", delegate { ShowContextHelp(); }, Keys.F1));
            var manualItem = new ToolStripMenuItem("Open &Manual", null, delegate { OpenManual(); });
            help.DropDownItems.Add(manualItem);
            help.DropDownItems.Add(MenuCommand.Create("D&iagnostics...", delegate { ShowDiagnostics(); }, Keys.Alt | Keys.F1));
            var updatesItem = MenuCommand.Create("Check for &Updates...", delegate { UpdateService.CheckForUpdates(this, AppVersion(), delegate { Close(); }); }, Keys.Shift | Keys.F1);
            help.DropDownItems.Add(updatesItem);
            help.DropDownItems.Add("&Version History...", null, delegate { UpdateService.ShowVersionHistory(this, AppVersion()); });
            var projectItem = MenuCommand.Create("&Project Page on GitHub", delegate { UpdateService.OpenProjectPage(); }, Keys.Control | Keys.F1);
            help.DropDownItems.Add(projectItem);
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add("&Contact", null, delegate { UpdateService.OpenContactPage(); });
            help.DropDownItems.Add("&Donate", null, delegate { UpdateService.OpenDonatePage(); });
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add("&About Native Serials", null, delegate { ShowAbout(); });
            menu.Items.Add(help);
            return menu;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (StorageActive && keyData == Keys.Escape) { ShowInventory(); return true; }
            if (keyData == (Keys.Alt | Keys.P)) { if (StorageActive) storageWindow.FocusProducts(); else productsList.Focus(); return true; }
            if (!StorageActive && keyData == (Keys.Alt | Keys.O)) { (withSerials.Checked ? withSerials : withoutSerials.Checked ? withoutSerials : allProducts).Focus(); return true; }
            if (keyData == (Keys.Alt | Keys.F1)) { ShowDiagnostics(); return true; }
            if (keyData == (Keys.Control | Keys.D2)) { ShowStorage(); return true; }
            if (keyData == (Keys.Control | Keys.D1)) { ShowInventory(); return true; }
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)) {
                if (pages.SelectedTab == inventoryPage) ShowStorage(); else ShowInventory(); return true;
            }
            if (keyData == (Keys.Control | Keys.R)) { Rescan(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { FocusSearch(); return true; }
            if (keyData == (Keys.Control | Keys.C) && searchBox.Focused) { searchBox.Copy(); return true; }
            if (keyData == Keys.F1)
            {
                ShowContextHelp();
                return true;
            }
            if (keyData == (Keys.Shift | Keys.F1))
            {
                UpdateService.CheckForUpdates(this, AppVersion(), delegate { Close(); });
                return true;
            }
            if (keyData == (Keys.Control | Keys.F1))
            {
                UpdateService.OpenProjectPage();
                return true;
            }
            if (keyData == (Keys.Control | Keys.S))
            {
                ExportHtml();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowStorage()
        {
            pages.SelectedTab = storagePage;
            EnsureStorage();
            storageWindow.FocusProducts();
        }

        private void ShowInventory() { pages.SelectedTab = inventoryPage; productsList.Focus(); }
        private void FocusSearch() { if (StorageActive) storageWindow.FocusSearch(); else { searchBox.Focus(); searchBox.SelectAll(); } }
        private void EnsureStorage()
        {
            if (storageWindow == null || storageWindow.IsDisposed) {
                storageWindow = new StorageForm { TopLevel = false, TabStop = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill, ReturnToInventory = ShowInventory };
                storageWindow.MinimumSize = Size.Empty;
                storageWindow.MainMenuStrip.Visible = false;
                storagePage.Controls.Add(storageWindow);
                storageWindow.Show();
            }
        }

        private void ProductsListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedRows();
                e.Handled = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.F)
            {
                searchBox.Focus();
                searchBox.SelectAll();
                e.Handled = true;
                return;
            }
        }

        private void Rescan()
        {
            if (StorageActive) { storageWindow.RefreshStorage(); return; }
            try
            {
                var result = scanner.Scan();
                products = result.Products;
                summary = result.Summary;
                ApplyFilter();
                LoadFolderLocations(products.ToList());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Native Serials scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void LoadFolderLocations(List<NativeProduct> snapshot)
        {
            if (folderScan != null) folderScan.Cancel();
            var current = new CancellationTokenSource();
            folderScan = current;
            folderLocations.Clear();
            try {
                var found = await Task.Run(() => FolderActions.Discover(snapshot, current.Token));
                if (!IsDisposed && folderScan == current && !current.IsCancellationRequested) folderLocations = found;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Registered locations remain available if additional discovery fails. */ }
            finally { if (folderScan == current) folderScan = null; current.Dispose(); }
        }

        private IList<FolderAction> SelectedFolders()
        {
            if (StorageActive) return storageWindow.SelectedFolders();
            if (productsList.SelectedItems.Count != 1) return new List<FolderAction>();
            var product = productsList.SelectedItems[0].Tag as NativeProduct;
            if (product == null) return new List<FolderAction>();
            List<string> paths;
            return FolderActions.ForPaths(folderLocations.TryGetValue(product, out paths) ? paths : FolderActions.Registered(product), StorageDiscovery.ContentLocation(product));
        }

        private void OpenProductFolder(FolderAction action)
        {
            if (StorageActive) { storageWindow.OpenProductFolder(action); return; }
            try { FolderActions.Open(action); }
            catch (Exception ex) { statusText.Text = "Could not open folder: " + ex.Message; }
        }

        private void ApplyFilter()
        {
            var query = (searchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            filteredProducts = products.Where(delegate(NativeProduct product)
            {
                bool hasSerial = !string.IsNullOrEmpty(product.Serial);
                if ((withSerials.Checked && !hasSerial) || (withoutSerials.Checked && hasSerial)) return false;
                if (query.Length == 0) return true;
                var haystack = string.Join(" ", new[]
                {
                    product.Name,
                    product.Company,
                    product.ProductType,
                    product.Upid,
                    product.Snpid,
                    product.Serial,
                    product.ContentDir,
                    product.InstallDir
                }).ToLowerInvariant();
                return haystack.Contains(query);
            }).ToList();
            PopulateList();
            UpdateStatus();
        }

        private void PopulateList()
        {
            productsList.BeginUpdate();
            try
            {
                productsList.Items.Clear();
                foreach (var product in filteredProducts)
                {
                    var path = FirstNonEmpty(product.ContentDir, product.InstallDir, product.Sources.Count > 0 ? product.Sources[0] : string.Empty);
                    var item = new ListViewItem(product.Name);
                    item.SubItems.Add(DisplaySerial(product.Serial));
                    item.SubItems.Add(product.ContentVersion);
                    item.SubItems.Add(product.Company);
                    item.SubItems.Add(product.ProductType);
                    item.SubItems.Add(path);
                    item.SubItems.Add(product.Upid);
                    item.Tag = product;
                    productsList.Items.Add(item);
                }
            }
            finally
            {
                productsList.EndUpdate();
            }
        }

        private void UpdateStatus()
        {
            if (summary == null)
            {
                statusText.Text = "Ready";
                return;
            }
            statusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} products. {2} serials found. {3} RAS3 tokens. Serials {4}.",
                filteredProducts.Count,
                summary.ProductCount,
                summary.SerialCount,
                summary.Ras3TokenCount,
                revealSerials.Checked ? "shown" : "hidden");
        }

        private string DisplaySerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return string.Empty;
            if (revealSerials.Checked) return serial;
            var suffix = serial.Length <= 4 ? serial : serial.Substring(serial.Length - 4);
            return "hidden, ending " + suffix;
        }

        private void CopySelectedRows()
        {
            if (StorageActive) { storageWindow.CopySelection(); return; }
            var lines = new List<string>();
            foreach (ListViewItem item in productsList.SelectedItems)
            {
                var product = item.Tag as NativeProduct;
                if (product != null)
                {
                    lines.Add(RowText(product, revealSerials.Checked));
                }
            }
            if (lines.Count == 0) return;
            Clipboard.SetText(string.Join(Environment.NewLine, lines), TextDataFormat.UnicodeText);
            statusText.Text = "Copied " + lines.Count.ToString(CultureInfo.InvariantCulture) + " row(s) to the clipboard.";
        }

        private void ExportHtml()
        {
            if (StorageActive) { storageWindow.SaveReport(); return; }
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save Native Serials HTML Report";
                dialog.Filter = "HTML report|*.html";
                dialog.FileName = "NativeSerialsReport.html";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, HtmlReport(revealSerials.Checked), Encoding.UTF8);
                statusText.Text = "Exported HTML report to " + dialog.FileName;
            }
        }

        private void ShowDiagnostics()
        {
            if (StorageActive) { storageWindow.Diagnostics(); return; }
            var text = summary == null ? "No scan has run." : summary.ToDiagnosticsText();
            using (var viewer = new TextViewerForm("Native Serials Diagnostics", text, "Native Serials diagnostics", "Review registration sources and product counts. Use Copy All to copy the complete report without serial numbers.", true, OpenManual))
            {
                viewer.ShowDialog(this);
            }
        }

        private void ShowContextHelp()
        {
            HelpDialog.ShowContext(this,
                "Inventory lists locally registered products. Serials are hidden until Reveal serials is checked. Products shown selects All products, With serials or Without serials; Search narrows that choice. Storage by Company opens folder sizes. Control+2 opens storage; Control+S saves the current inventory. Escape closes this help dialog. Open Manual opens the full guide.",
                OpenManual);
        }

        private void OpenManual()
        {
            var manual = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Manual.html");
            if (!File.Exists(manual))
            {
                MessageBox.Show(this, "Manual.html was not found beside the app.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = manual, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not open manual", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                this,
                "Native Serials " + AppVersion() + Environment.NewLine + Environment.NewLine +
                "Accessible local Native Instruments product and serial reporter." + Environment.NewLine + Environment.NewLine +
                "Created by Andre Louis with Codex." + Environment.NewLine + Environment.NewLine +
                "Project page:" + Environment.NewLine + UpdateService.ProjectUrl,
                "About Native Serials",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static string AppVersion()
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion)) return info.InformationalVersion;
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "1.0" : version.ToString(3);
        }

        private string RowText(NativeProduct product, bool includeSerial)
        {
            return string.Join("\t", new[]
            {
                "Product: " + product.Name,
                "Serial: " + (includeSerial ? product.Serial : DisplaySerial(product.Serial)),
                "Version: " + product.ContentVersion,
                "Company: " + product.Company,
                "Type: " + product.ProductType,
                "SNPID: " + product.Snpid,
                "Content: " + product.ContentDir,
                "Install: " + product.InstallDir,
                "UPID: " + product.Upid
            });
        }

        private string HtmlReport(bool includeSerials)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Native Serials Report</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;line-height:1.4;margin:2rem}table{border-collapse:collapse;width:100%}th,td{border:1px solid #777;padding:.35rem;text-align:left;vertical-align:top}th{background:#eee}</style>");
            sb.AppendLine("</head><body><h1>Native Serials Report</h1>");
            sb.AppendLine("<p>Generated " + Escape(DateTime.Now.ToString("u", CultureInfo.InvariantCulture)) + ". Serials " + (includeSerials ? "included" : "masked") + ".</p>");
            sb.AppendLine("<table><thead><tr><th>Product</th><th>Serial</th><th>Version</th><th>Company</th><th>Type</th><th>Path</th><th>UPID</th></tr></thead><tbody>");
            foreach (var product in filteredProducts)
            {
                sb.AppendLine("<tr><td>" + Escape(product.Name) + "</td><td>" + Escape(includeSerials ? product.Serial : DisplaySerial(product.Serial)) + "</td><td>" + Escape(product.ContentVersion) + "</td><td>" + Escape(product.Company) + "</td><td>" + Escape(product.ProductType) + "</td><td>" + Escape(FirstNonEmpty(product.ContentDir, product.InstallDir)) + "</td><td>" + Escape(product.Upid) + "</td></tr>");
            }
            sb.AppendLine("</tbody></table></body></html>");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return string.Empty;
        }
    }

    internal sealed class NativeScanner
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public ScanResult Scan()
        {
            var roots = NativePaths.ForCurrentMachine();
            var byKey = new Dictionary<string, NativeProduct>(StringComparer.OrdinalIgnoreCase);
            var installed = ScanInstalledProducts(roots.InstalledProducts);
            var service = ScanServiceCenter(roots.ServiceCenter);
            var tokens = ScanRas3Tokens(roots.Ras3);

            foreach (var product in installed) byKey[product.Key] = product;
            foreach (var product in service) MergeInto(byKey, product);
            foreach (var product in tokens.Products) MergeInto(byKey, product);

            var products = DedupeByName(EnrichCompanies(byKey.Values.Where(p => p.IsOwnedCandidate), service)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new ScanResult
            {
                Products = products,
                Summary = new ScanSummary
                {
                    ProductCount = products.Count,
                    InstalledJsonCount = installed.Count,
                    ServiceXmlCount = service.Count,
                    Ras3TokenCount = tokens.FileCount,
                    DecodedTokenCount = tokens.DecodedCount,
                    SerialCount = products.Count(p => !string.IsNullOrEmpty(p.Serial)),
                    SourceRoots = roots.AllRoots()
                }
            };
        }

        private List<NativeProduct> ScanInstalledProducts(string folder)
        {
            var products = new List<NativeProduct>();
            if (!Directory.Exists(folder)) return products;
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    var map = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    var name = Path.GetFileNameWithoutExtension(file);
                    products.Add(new NativeProduct
                    {
                        Key = "installed:" + name,
                        Name = name,
                        ContentVersion = Value(map, "ContentVersion"),
                        ContentDir = Value(map, "ContentDir"),
                        InstallDir = Value(map, "InstallDir"),
                        IsOwnedCandidate = true,
                        Sources = new List<string> { file }
                    });
                }
                catch
                {
                }
            }
            return products;
        }

        private List<NativeProduct> ScanServiceCenter(string folder)
        {
            var products = new List<NativeProduct>();
            if (!Directory.Exists(folder)) return products;
            foreach (var file in Directory.GetFiles(folder, "*.xml"))
            {
                try
                {
                    var doc = XDocument.Load(file);
                    var productElements = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "Product", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (productElements.Count == 0)
                    {
                        var upid = FirstElementValue(doc, "UPID");
                        var name = FirstElementValue(doc, "Name");
                        if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(file);
                        products.Add(ServiceProduct(file, name, upid, FirstElementValue(doc, "SNPID"), FirstElementValue(doc, "Company"), FirstElementValue(doc, "Type"), FirstElementValue(doc, "Serial"), FirstElementValue(doc, "RegKey")));
                        continue;
                    }

                    foreach (var element in productElements)
                    {
                        var upid = FirstChildElementValue(element, "UPID");
                        var name = FirstChildElementValue(element, "Name");
                        if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(file);
                        products.Add(ServiceProduct(file, name, upid, FirstChildElementValue(element, "SNPID"), FirstChildElementValue(element, "Company"), FirstChildElementValue(element, "Type"), FirstChildElementValue(element, "Serial"), FirstChildElementValue(element, "RegKey")));
                    }
                }
                catch
                {
                }
            }
            return products;
        }

        private static NativeProduct ServiceProduct(string source, string name, string upid, string snpid, string company, string productType, string serial, string registryName)
        {
            return new NativeProduct
            {
                Key = string.IsNullOrEmpty(upid) ? "service:" + name : "upid:" + upid,
                Name = name,
                Upid = upid,
                Snpid = snpid,
                Company = company,
                ProductType = productType,
                Serial = serial,
                RegistryName = registryName,
                Sources = new List<string> { source }
            };
        }

        internal List<NativeProduct> EnrichCompanies(IEnumerable<NativeProduct> candidates, IEnumerable<NativeProduct> metadata)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in metadata.Where(p => !string.IsNullOrWhiteSpace(p.Company)))
                foreach (var alias in new[] { item.Name, item.RegistryName }.Where(n => !string.IsNullOrWhiteSpace(n)))
                    byName[alias.Trim()] = item.Company;
            var result = candidates.ToList();
            foreach (var item in result)
                if (string.IsNullOrWhiteSpace(item.Company) && byName.ContainsKey(item.Name.Trim()))
                    item.Company = byName[item.Name.Trim()];
            return result;
        }

        private TokenScan ScanRas3Tokens(string folder)
        {
            var scan = new TokenScan();
            if (!Directory.Exists(folder)) return scan;
            foreach (var file in Directory.GetFiles(folder, "*.jwt"))
            {
                scan.FileCount++;
                try
                {
                    var payload = DecodeJwtPayload(File.ReadAllText(file));
                    if (payload == null) continue;
                    scan.DecodedCount++;
                    var upid = Path.GetFileNameWithoutExtension(file);
                    scan.Products.Add(new NativeProduct
                    {
                        Key = "upid:" + upid,
                        Name = "License token " + upid.Substring(0, Math.Min(8, upid.Length)),
                        Upid = upid,
                        Serial = Value(payload, "serial"),
                        HasRas3Token = true,
                        IsOwnedCandidate = true,
                        Sources = new List<string> { file }
                    });
                }
                catch
                {
                }
            }
            return scan;
        }

        private Dictionary<string, object> DecodeJwtPayload(string token)
        {
            var parts = (token ?? string.Empty).Trim().Split('.');
            if (parts.Length < 2) return null;
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            while (segment.Length % 4 != 0) segment += "=";
            var bytes = Convert.FromBase64String(segment);
            return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
        }

        private void MergeInto(Dictionary<string, NativeProduct> byKey, NativeProduct product)
        {
            var key = product.Key;
            if (byKey.ContainsKey(key))
            {
                var existing = byKey[key];
                Merge(existing, product);
            }
            else
            {
                byKey[key] = product;
            }
        }

        private static void Merge(NativeProduct existing, NativeProduct other)
        {
            if (existing.Name.StartsWith("License token ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(other.Name)) existing.Name = other.Name;
            if (string.IsNullOrEmpty(existing.Upid)) existing.Upid = other.Upid;
            if (string.IsNullOrEmpty(existing.Snpid)) existing.Snpid = other.Snpid;
            if (string.IsNullOrEmpty(existing.Company)) existing.Company = other.Company;
            if (string.IsNullOrEmpty(existing.ProductType)) existing.ProductType = other.ProductType;
            if (string.IsNullOrEmpty(existing.ContentVersion)) existing.ContentVersion = other.ContentVersion;
            if (string.IsNullOrEmpty(existing.ContentDir)) existing.ContentDir = other.ContentDir;
            if (string.IsNullOrEmpty(existing.InstallDir)) existing.InstallDir = other.InstallDir;
            if (string.IsNullOrEmpty(existing.Serial)) existing.Serial = other.Serial;
            existing.HasRas3Token = existing.HasRas3Token || other.HasRas3Token;
            existing.IsOwnedCandidate = existing.IsOwnedCandidate || other.IsOwnedCandidate;
            foreach (var source in other.Sources)
            {
                if (!existing.Sources.Contains(source)) existing.Sources.Add(source);
            }
        }

        private IEnumerable<NativeProduct> DedupeByName(IEnumerable<NativeProduct> products)
        {
            var grouped = new Dictionary<string, NativeProduct>(StringComparer.CurrentCultureIgnoreCase);
            var order = new List<string>();
            foreach (var product in products)
            {
                var key = NormalizeName(product.Name);
                if (key.Length == 0) continue;
                NativeProduct existing;
                if (grouped.TryGetValue(key, out existing))
                {
                    Merge(existing, product);
                }
                else
                {
                    grouped[key] = product;
                    order.Add(key);
                }
            }
            foreach (var key in order)
            {
                yield return grouped[key];
            }
        }

        private static string NormalizeName(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ").ToUpperInvariant();
        }

        private static string FirstElementValue(XDocument doc, string name)
        {
            var element = doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return element == null ? string.Empty : (element.Value ?? string.Empty).Trim();
        }

        private static string FirstChildElementValue(XElement parent, string name)
        {
            var element = parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return element == null ? string.Empty : (element.Value ?? string.Empty).Trim();
        }

        private static string Value(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    internal sealed class NativePaths
    {
        public string SharedRoot;
        public string InstalledProducts;
        public string Ras3;
        public string ServiceCenter;
        public string Logs;

        public static NativePaths ForCurrentMachine()
        {
            var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrEmpty(publicDocuments))
            {
                publicDocuments = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "Documents");
            }
            var commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            if (string.IsNullOrEmpty(commonFiles))
            {
                commonFiles = Environment.GetEnvironmentVariable("CommonProgramFiles") ?? @"C:\Program Files\Common Files";
            }

            var sharedRoot = Path.Combine(publicDocuments, "Native Instruments");
            return new NativePaths
            {
                SharedRoot = sharedRoot,
                InstalledProducts = Path.Combine(sharedRoot, "installed_products"),
                Ras3 = Path.Combine(sharedRoot, "Native Access", "ras3"),
                ServiceCenter = Path.Combine(commonFiles, "Native Instruments", "Service Center"),
                Logs = Path.Combine(sharedRoot, "Logs", "NTK")
            };
        }

        public List<string> AllRoots()
        {
            return new List<string> { InstalledProducts, Ras3, ServiceCenter, Logs };
        }
    }

    internal sealed class ScanResult
    {
        public List<NativeProduct> Products;
        public ScanSummary Summary;
    }

    internal sealed class ScanSummary
    {
        public int ProductCount;
        public int InstalledJsonCount;
        public int ServiceXmlCount;
        public int Ras3TokenCount;
        public int DecodedTokenCount;
        public int SerialCount;
        public List<string> SourceRoots = new List<string>();

        public string ToDiagnosticsText()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "Native Serials " + Assembly.GetExecutingAssembly().GetName().Version,
                "Products: " + ProductCount.ToString(CultureInfo.InvariantCulture),
                "Installed product JSON files: " + InstalledJsonCount.ToString(CultureInfo.InvariantCulture),
                "Service Center XML files: " + ServiceXmlCount.ToString(CultureInfo.InvariantCulture),
                "RAS3 token files: " + Ras3TokenCount.ToString(CultureInfo.InvariantCulture),
                "Decoded RAS3 tokens: " + DecodedTokenCount.ToString(CultureInfo.InvariantCulture),
                "Products with serials: " + SerialCount.ToString(CultureInfo.InvariantCulture),
                "",
                "Source roots:",
                string.Join(Environment.NewLine, SourceRoots.ToArray())
            });
        }
    }

    internal sealed class NativeProduct
    {
        public string RegistryName = string.Empty;
        public string Key = string.Empty;
        public string Name = string.Empty;
        public string Upid = string.Empty;
        public string Snpid = string.Empty;
        public string Company = string.Empty;
        public string ProductType = string.Empty;
        public string ContentVersion = string.Empty;
        public string ContentDir = string.Empty;
        public string InstallDir = string.Empty;
        public string Serial = string.Empty;
        public bool HasRas3Token;
        public bool IsOwnedCandidate;
        public List<string> Sources = new List<string>();
    }

    internal sealed class TokenScan
    {
        public int FileCount;
        public int DecodedCount;
        public readonly List<NativeProduct> Products = new List<NativeProduct>();
    }

    internal sealed class TextViewerForm : Form
    {
        private readonly TextBox textBox;
        internal bool IsContextHelp { get; set; }

        public TextViewerForm(string title, string text, string accessibleName, string accessibleDescription, bool showCopyButton, Action openManual = null)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Width = 850;
            Height = 600;
            MinimumSize = new Size(480, 320);
            KeyPreview = true;

            textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Text = text ?? string.Empty,
                AccessibleName = accessibleName,
                AccessibleDescription = accessibleDescription,
                TabIndex = 0
            };
            Controls.Add(textBox);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(4)
            };
            var close = new Button { Text = "&Close", AccessibleName = "Close", AutoSize = true, MinimumSize = new Size(90, 28), DialogResult = DialogResult.OK, TabIndex = 2 };
            buttons.Controls.Add(close);
            if (openManual != null)
            {
                var manual = new Button { Text = "Open &Manual", AccessibleName = "Open Manual", AutoSize = true, MinimumSize = new Size(130, 28), TabIndex = 1 };
                manual.Click += delegate { openManual(); };
                buttons.Controls.Add(manual);
            }
            if (showCopyButton)
            {
                var copy = new Button { Text = "Cop&y All", AccessibleName = "Copy all", AutoSize = true, MinimumSize = new Size(90, 28), TabIndex = 0 };
                copy.Click += delegate { Clipboard.SetText(textBox.Text ?? string.Empty, TextDataFormat.UnicodeText); };
                buttons.Controls.Add(copy);
            }
            Controls.Add(buttons);
            Controls.SetChildIndex(textBox, 0);
            Controls.SetChildIndex(buttons, 1);
            HelpDialog.Prepare(this);

            AcceptButton = close;
            CancelButton = close;
            Shown += delegate
            {
                textBox.Focus();
                textBox.Select(0, 0);
            };
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                textBox.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
