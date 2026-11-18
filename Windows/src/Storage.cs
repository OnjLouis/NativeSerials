using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NativeSerials
{
    internal sealed class StorageRow
    {
        public string Name, Company, Path, State;
        public long Bytes;
        public List<string> Locations = new List<string>();
        public string LocationSummary { get { return Locations.Count == 0 ? Path : string.Join("; ", Locations); } }
    }

    internal sealed class StorageCompany
    {
        public string Name;
        public int Count, MeasuredCount, AvailableCount;
        public long Bytes;
    }

    internal sealed class StorageResult
    {
        public List<StorageRow> Rows = new List<StorageRow>();
        public List<StorageCompany> Companies = new List<StorageCompany>();
        public long TotalBytes;
        public bool HasOverlap;
    }

    internal static class StorageScanner
    {
        private const uint OpenExisting = 3;
        private const uint ShareReadWriteDelete = 7;
        private const uint DirectoryHandle = 0x02000000;
        public static StorageResult Scan(IList<NativeProduct> products, CancellationToken cancel, Action<string> progress, StorageSearchRoots searchRoots = null)
        {
            cancel.ThrowIfCancellationRequested();
            var search = searchRoots ?? StorageSearchRoots.SystemRoots(products);
            var discovered = StorageDiscovery.Discover(products, search, cancel);
            var result = new StorageResult();
            for (int index = 0; index < products.Count; index++)
            {
                var product = products[index];
                cancel.ThrowIfCancellationRequested();
                string path = !string.IsNullOrWhiteSpace(product.ContentDir) ? StorageDiscovery.ContentLocation(product) : product.InstallDir;
                var row = new StorageRow { Name = product.Name, Company = CompanyName(product.Company),
                    Path = path ?? "", State = string.IsNullOrWhiteSpace(path) ? "No location" : "Unavailable" };
                result.Rows.Add(row);
                bool missing = false, broad = false;
                foreach (string candidate in StorageDiscovery.RegisteredRoots(product).Concat(discovered[index]))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        string resolved = CanonicalLocation(candidate);
                        if (StorageDiscovery.IsBroad(resolved, search)) { broad = true; continue; }
                        row.Locations.Add(resolved);
                        if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) row.Path = resolved;
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw;
                        missing = true;
                    }
                }
                row.Locations = row.Locations.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (row.Locations.Count > 0 && !row.Locations.Contains(row.Path, StringComparer.OrdinalIgnoreCase)) row.Path = row.Locations[0];
                row.State = row.Locations.Count == 0 ? (broad ? "Unsafe broad location" : row.State) : (missing ? "Partial" : "Measured");
            }
            var valid = result.Rows.Where(r => r.Locations.Count > 0).ToList();
            var roots = valid.SelectMany(r => r.Locations).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
            var minimal = new List<string>();
            foreach (string root in roots)
            {
                if (!minimal.Any(parent => Contains(parent, root))) minimal.Add(root);
            }
            var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in minimal)
            {
                cancel.ThrowIfCancellationRequested();
                var members = valid.Where(r => r.Locations.Any(p => Contains(root, p))).ToList();
                if (members.Count > 1) result.HasOverlap = true;
                if (File.Exists(root))
                {
                    try { Count(members, root, new FileInfo(root).Length, totals, result); }
                    catch (IOException) { Mark(members, root, "Partial"); }
                    catch (UnauthorizedAccessException) { Mark(members, root, "Partial"); }
                    continue;
                }
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    if (progress != null) progress(directory);
                    try
                    {
                        // One traversal per shared tree; each company counts a file only once.
                        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            cancel.ThrowIfCancellationRequested();
                            try
                            {
                                var attributes = File.GetAttributes(entry);
                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                { Mark(members, entry, "Links skipped"); continue; }
                                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                                Count(members, entry, new FileInfo(entry).Length, totals, result);
                            }
                            catch (IOException) { Mark(members, entry, "Partial"); }
                            catch (UnauthorizedAccessException) { Mark(members, entry, "Partial"); }
                        }
                    }
                    catch (IOException) { Mark(members, directory, "Partial"); }
                    catch (UnauthorizedAccessException) { Mark(members, directory, "Partial"); }
                }
            }
            foreach (var group in result.Rows.GroupBy(r => r.Company, StringComparer.OrdinalIgnoreCase))
                result.Companies.Add(new StorageCompany { Name = group.Key, Count = group.Count(),
                    MeasuredCount = group.Count(r => r.State == "Measured"), AvailableCount = group.Count(StorageReport.HasSize), Bytes = totals.ContainsKey(group.Key) ? totals[group.Key] : 0 });
            return result;
        }

        private static void Count(List<StorageRow> members, string path, long bytes, Dictionary<string, long> totals, StorageResult result)
        {
            var owners = members.Where(r => r.Locations.Any(p => Contains(p, path))).ToList();
            if (owners.Count == 0) return;
            foreach (var row in owners) row.Bytes = checked(row.Bytes + bytes);
            foreach (string company in owners.Select(r => r.Company).Distinct(StringComparer.OrdinalIgnoreCase))
                totals[company] = checked((totals.ContainsKey(company) ? totals[company] : 0) + bytes);
            result.TotalBytes = checked(result.TotalBytes + bytes);
        }

        internal static bool Contains(string root, string path)
        {
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static string FolderToOpen(string path)
        {
            string item = ItemToReveal(path);
            if (!string.IsNullOrEmpty(item)) path = System.IO.Path.GetDirectoryName(item);
            if (!Directory.Exists(path)) throw new IOException("This location is unavailable.");
            return path;
        }

        internal static string ItemToReveal(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("This location is unavailable.");
            string item = File.Exists(path) ? path : null;
            for (string ancestor = path; !string.IsNullOrEmpty(ancestor); ancestor = System.IO.Path.GetDirectoryName(ancestor))
                if (new[] { ".vst3", ".vst", ".aaxplugin" }.Contains(System.IO.Path.GetExtension(ancestor), StringComparer.OrdinalIgnoreCase)) item = ancestor;
            return item;
        }

        internal static string CompanyName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Unknown company";
            if (string.Equals(name, "Fracture Sounds - Blueprint", StringComparison.OrdinalIgnoreCase)) return "Fracture Sounds";
            if (string.Equals(name, "Native Instruments GmbH", StringComparison.OrdinalIgnoreCase)) return "Native Instruments";
            if (string.Equals(name, "Project Sam", StringComparison.OrdinalIgnoreCase)) return "ProjectSAM";
            return name;
        }

        private static void Mark(IEnumerable<StorageRow> rows, string path, string state)
        {
            foreach (var row in rows.Where(r => r.Locations.Any(p => Contains(p, path) || Contains(path, p))))
                if (state == "Partial" || row.State == "Measured") row.State = state;
        }

        internal static string CanonicalLocation(string path)
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!(path.StartsWith(@"\\") || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))))
                throw new ArgumentException("Registered location is not absolute");
            path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            using (var handle = CreateFile(path, 0, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, DirectoryHandle, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("Directory unavailable");
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("Directory unavailable");
                string resolved = buffer.ToString();
                if (resolved.StartsWith(@"\\?\UNC\")) resolved = @"\\" + resolved.Substring(8);
                else if (resolved.StartsWith(@"\\?\")) resolved = resolved.Substring(4);
                if (!Directory.Exists(resolved) && !File.Exists(resolved)) throw new IOException("Location unavailable");
                return resolved.TrimEnd('\\');
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    }

    internal static class StorageReport
    {
        public static string Size(long bytes)
        {
            return bytes >= 1073741824 ? (bytes / 1073741824.0).ToString("N2", CultureInfo.CurrentCulture) + " GiB" :
                (bytes / 1048576.0).ToString("N2", CultureInfo.CurrentCulture) + " MiB";
        }

        private static string Escape(string value) { return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }

        public static string Html(StorageResult result, IList<StorageRow> rows)
        {
            var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Native Serials Storage</title><style>body{font:1rem system-ui;margin:2rem}table{border-collapse:collapse}th,td{border:1px solid #777;padding:.4rem;text-align:left;overflow-wrap:anywhere}caption{text-align:left;font-weight:bold}</style><h1>Storage by Company</h1>");
            html.Append("<p>Snapshot: " + Escape(DateTime.Now.ToString("g")) + ". File sizes, not reclaimable space. Shared folders count once per company and once in the overall total; company totals may overlap. Nested links are skipped. Hard-linked files count under each filename. Missing and unreadable content is excluded or partial.</p>");
            html.Append("<p>Overall measured total: " + Size(result.TotalBytes) + ".</p>");
            foreach (var group in rows.GroupBy(r => r.Company, StringComparer.OrdinalIgnoreCase))
            {
                var company = result.Companies.First(c => string.Equals(c.Name, group.Key, StringComparison.OrdinalIgnoreCase));
                html.Append("<h2>" + Escape(group.Key) + "</h2><p>" + company.Count + " registered products; " + company.MeasuredCount + " fully measured. Company total: " + CompanySize(company) + ".</p><table><caption>" + Escape(group.Key) + "</caption><thead><tr><th scope=\"col\">Product</th><th scope=\"col\">Size</th><th scope=\"col\">Status</th><th scope=\"col\">Location</th></tr></thead><tbody>");
                foreach (var row in group)
                    html.Append("<tr><td>" + Escape(row.Name) + "</td><td>" + (HasSize(row) ? Size(row.Bytes) : "Unavailable") + "</td><td>" + Escape(row.State) + "</td><td>" + Escape(row.LocationSummary) + "</td></tr>");
                html.Append("</tbody></table>");
            }
            return html.Append("</html>").ToString();
        }

        public static bool HasSize(StorageRow row) { return row.State == "Measured" || row.State == "Partial" || row.State == "Links skipped"; }
        public static string CompanySize(StorageCompany company) { return company.AvailableCount > 0 ? Size(company.Bytes) : "Unavailable"; }
    }
}
