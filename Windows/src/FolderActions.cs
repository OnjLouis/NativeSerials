using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NativeSerials
{
    internal sealed class FolderAction
    {
        internal string Label, Folder, Item;
    }

    internal static class FolderActions
    {
        internal static List<string> Registered(NativeProduct product)
        {
            var roots = StorageSearchRoots.SystemRoots(new[] { product });
            var paths = new List<string>();
            foreach (string candidate in StorageDiscovery.RegisteredRoots(product))
                try {
                    string path = StorageScanner.CanonicalLocation(candidate);
                    if (!StorageDiscovery.IsBroad(path, roots)) paths.Add(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            return paths;
        }

        internal static Dictionary<NativeProduct, List<string>> Discover(IList<NativeProduct> products, CancellationToken cancel)
        {
            var found = StorageDiscovery.Discover(products, StorageSearchRoots.SystemRoots(products), cancel);
            var result = new Dictionary<NativeProduct, List<string>>();
            for (int i = 0; i < products.Count; i++) {
                cancel.ThrowIfCancellationRequested();
                result[products[i]] = Registered(products[i]).Concat(found[i]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            return result;
        }

        internal static List<FolderAction> ForPaths(IEnumerable<string> paths, string content = "")
        {
            var actions = new List<FolderAction>();
            foreach (string path in paths) {
                try {
                    string folder = StorageScanner.FolderToOpen(path);
                    string lower = path.ToLowerInvariant();
                    string[] segments = lower.Split('\\', '/');
                    string label = segments.Any(s => s.EndsWith(".vst3")) ? "Open VST&3 Folder" :
                        segments.Any(s => s.EndsWith(".aaxplugin")) ? "Open AA&X Folder" :
                        (lower.EndsWith(".dll") && segments.Any(s => s.Contains("vst") || s == "plugins")) || lower.EndsWith(".vst") ? "Open VST&2 Folder" :
                        string.Equals(path, content, StringComparison.OrdinalIgnoreCase) ? "Open Content &Folder" :
                        lower.EndsWith(".exe") || lower.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant() + "\\") ? "Open App&lication Folder" : "Open &Support Folder";
                    string item = StorageScanner.ItemToReveal(path);
                    if (string.IsNullOrEmpty(item) && actions.Any(a => a.Label == label && string.Equals(a.Folder, folder, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.IsNullOrEmpty(item)) actions.RemoveAll(a => a.Label == label && string.Equals(a.Folder, folder, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(a.Item));
                    if (!actions.Any(a => a.Label == label && string.Equals(a.Folder, folder, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item, item, StringComparison.OrdinalIgnoreCase)))
                        actions.Add(new FolderAction { Label = label, Folder = folder, Item = item });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            return actions.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Folder, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static void Open(FolderAction action)
        {
            string item = action.Item;
            if (!string.IsNullOrEmpty(item) && (File.Exists(item) || Directory.Exists(item)))
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + item + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(StorageScanner.FolderToOpen(action.Folder)) { UseShellExecute = true });
        }

        internal static void Open(string path)
        {
            Open(new FolderAction { Folder = StorageScanner.FolderToOpen(path), Item = StorageScanner.ItemToReveal(path) });
        }

        internal static void Populate(ToolStripItemCollection items, IList<FolderAction> actions, Action<FolderAction> open)
        {
            foreach (var group in actions.GroupBy(a => a.Label)) {
                var choices = group.ToList();
                if (choices.Count == 1) items.Add(MenuCommand.Create(group.Key, delegate { open(choices[0]); }));
                else {
                    var parent = new ToolStripMenuItem(group.Key);
                    PopulateChoices(parent.DropDownItems, choices, open);
                    items.Add(parent);
                }
            }
            if (actions.Count == 0) items.Add(new ToolStripMenuItem("No product folders available") { Enabled = false });
        }

        private const int ChoicesPerMenu = 9;
        private static void PopulateChoices(ToolStripItemCollection items, IList<FolderAction> choices, Action<FolderAction> open)
        {
            for (int index = 0; index < Math.Min(ChoicesPerMenu, choices.Count); index++) {
                var captured = choices[index];
                items.Add(new ToolStripMenuItem("&" + (index + 1) + " " + (string.IsNullOrEmpty(captured.Item) ? captured.Folder : captured.Item).Replace("&", "&&"), null, delegate { open(captured); }));
            }
            if (choices.Count > ChoicesPerMenu) {
                var more = new ToolStripMenuItem("&More Locations");
                PopulateChoices(more.DropDownItems, choices.Skip(ChoicesPerMenu).ToList(), open);
                items.Add(more);
            }
        }

        internal static void Clear(ToolStripItemCollection items, int keep = 0)
        {
            while (items.Count > keep) { var item = items[keep]; items.RemoveAt(keep); item.Dispose(); }
        }

        internal static void AddToTable(ListView table, Func<IList<FolderAction>> actions, Action<FolderAction> open)
        {
            table.ContextMenuStrip.Opening += delegate {
                var items = table.ContextMenuStrip.Items;
                Clear(items, 1);
                items.Add(new ToolStripSeparator());
                Populate(items, actions(), open);
            };
        }
    }
}
