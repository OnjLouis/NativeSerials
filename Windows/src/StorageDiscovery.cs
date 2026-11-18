using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Threading;

namespace NativeSerials
{
    internal sealed class StorageSearchRoots
    {
        public string[] Plugins = new string[0], Applications = new string[0], Data = new string[0];

        public static StorageSearchRoots SystemRoots(IList<NativeProduct> products)
        {
            var programs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct().ToArray();
            return new StorageSearchRoots {
                Plugins = programs.SelectMany(p => new[] { Path.Combine(p, "Common Files", "VST3"), Path.Combine(p, "VstPlugins"),
                    Path.Combine(p, "Common Files", "Avid", "Audio", "Plug-Ins"), Path.Combine(p, "Steinberg", "VstPlugins") }).Concat(products.Select(p => p.InstallDir).Where(StorageDiscovery.IsPluginRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Applications = programs,
                Data = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
                    .Where(p => p.Length > 0).ToArray()
            };
        }
    }

    internal static class StorageDiscovery
    {
        private const int MaximumDepth = 3;
        private static readonly HashSet<string> PluginRoots = new HashSet<string> { "vst", "vst2", "vst3", "vstplugins", "plugins", "commonfiles" };
        private static readonly HashSet<string> LegalSuffixes = new HashSet<string> { "inc", "llc", "ltd", "limited", "gmbh", "srl" };

        internal static string Normalize(string value)
        {
            return new string((value ?? "").Normalize(NormalizationForm.FormD).Where(c => char.IsLetterOrDigit(c) &&
                CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
        }

        internal static bool IsPluginRoot(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && PluginRoots.Contains(Normalize(Path.GetFileName(path.TrimEnd('\\', '/'))));
        }

        private static IEnumerable<string> PublisherNames(string company)
        {
            company = company ?? "";
            string trimmed = string.Join("", company.ToLowerInvariant().Split(new[] { ' ', '.', ',', '-' }, StringSplitOptions.RemoveEmptyEntries).Where(w => !LegalSuffixes.Contains(w)));
            return new[] { Normalize(company), Normalize(trimmed) }.Where(v => v.Length > 0);
        }

        internal static IEnumerable<string> RegisteredRoots(NativeProduct product)
        {
            var paths = new List<string>(new[] { product.ContentDir, product.InstallDir }.Where(p => !string.IsNullOrWhiteSpace(p)));
            try {
                if (!string.IsNullOrWhiteSpace(product.ContentDir)) {
                    string content = product.ContentDir.TrimEnd('\\', '/');
                    string parent = Path.GetDirectoryName(content);
                    if (!string.IsNullOrEmpty(parent) && string.Equals(Path.GetFileName(content), "NKS", StringComparison.OrdinalIgnoreCase)) {
                        string name = Normalize(Path.GetFileName(parent));
                        if (name != Normalize(product.Company) && (name == Normalize(product.Name) || (!string.IsNullOrEmpty(product.RegistryName) && name == Normalize(product.RegistryName)))) paths.Add(parent);
                    }
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return paths;
        }

        internal static string ContentLocation(NativeProduct product)
        {
            return RegisteredRoots(product).FirstOrDefault(p => p != product.ContentDir && p != product.InstallDir) ?? product.ContentDir;
        }

        internal static bool IsBroad(string path, StorageSearchRoots search)
        {
            if (string.Equals(path, Path.GetPathRoot(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsPluginRoot(path)) return true;
            var generic = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            return generic.Concat(search.Plugins).Concat(search.Applications).Concat(search.Data).Where(p => !string.IsNullOrEmpty(p)).Any(p => {
                try { return string.Equals(StorageScanner.CanonicalLocation(p), path, StringComparison.OrdinalIgnoreCase); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                catch (ArgumentException) { return false; }
                catch (NotSupportedException) { return false; }
            });
        }

        internal static List<string>[] Discover(IList<NativeProduct> products, StorageSearchRoots roots, CancellationToken cancel)
        {
            var locations = products.Select(p => new List<string>()).ToArray();
            var aliases = new Dictionary<string, HashSet<int>>();
            var publishers = new List<HashSet<string>>();
            for (int i = 0; i < products.Count; i++)
            {
                string company = StorageScanner.CompanyName(products[i].Company);
                var vendors = new HashSet<string>(PublisherNames(company).Where(v => company != "Unknown company"));
                publishers.Add(vendors);
                foreach (string name in new[] { products[i].Name, products[i].RegistryName })
                {
                    string key = Normalize(name);
                    var names = new[] { key }.Concat(vendors.Where(v => key.StartsWith(v) && key.Length > v.Length).Select(v => key.Substring(v.Length)));
                    foreach (string alias in names.Where(n => n.Length > 0))
                    {
                        if (!aliases.ContainsKey(alias)) aliases.Add(alias, new HashSet<int>());
                        aliases[alias].Add(i);
                    }
                }
            }
            var knownPublishers = new HashSet<string>(publishers.SelectMany(p => p));
            foreach (bool dataOnly in new[] { false, true })
            foreach (string root in (dataOnly ? roots.Data : roots.Plugins.Concat(roots.Applications)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool applicationRoot = !dataOnly && !roots.Plugins.Contains(root, StringComparer.OrdinalIgnoreCase);
                // Bounded discovery reads metadata only; it never loads a plug-in or follows a junction.
                var pending = new Stack<Tuple<string, int, HashSet<string>>>();
                pending.Push(Tuple.Create(root, 0, new HashSet<string>()));
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    var current = pending.Pop();
                    string[] entries;
                    try { entries = Directory.GetFileSystemEntries(current.Item1); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    foreach (string entry in entries)
                    {
                        cancel.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            bool directory = (attributes & FileAttributes.Directory) != 0;
                            string ext = Path.GetExtension(entry).ToLowerInvariant();
                            bool binary = ext == ".dll" || ext == ".vst3" || ext == ".exe" || ext == ".aaxplugin";
                            string name = directory && !binary ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry);
                            var evidence = new HashSet<string>(current.Item3);
                            evidence.UnionWith(PublisherNames(Path.GetFileName(entry)));
                            var names = new HashSet<string> { Normalize(name) };
                            if (!directory && binary && !dataOnly)
                            {
                                var info = FileVersionInfo.GetVersionInfo(entry);
                                evidence.UnionWith(PublisherNames(info.CompanyName));
                                names.Add(Normalize(info.ProductName)); names.Add(Normalize(info.FileDescription));
                            }
                            if (directory && binary && !dataOnly) {
                                foreach (string architecture in new[] { "x86_64-win", "x64", "Win32" }) {
                                    string payload = Path.Combine(entry, "Contents", architecture, Path.GetFileName(entry));
                                    if (!File.Exists(payload)) continue;
                                    var info = FileVersionInfo.GetVersionInfo(payload);
                                    evidence.UnionWith(PublisherNames(info.CompanyName));
                                    names.Add(Normalize(info.ProductName)); names.Add(Normalize(info.FileDescription));
                                }
                            }
                            var owners = new HashSet<int>();
                            if (dataOnly || binary)
                            foreach (string key in names)
                            {
                                HashSet<int> candidates;
                                if (!aliases.TryGetValue(key, out candidates)) continue;
                                foreach (int index in candidates)
                                    if (publishers[index].Overlaps(current.Item3) || (!dataOnly && publishers[index].Overlaps(evidence))) owners.Add(index);
                            }
                            foreach (int index in owners) locations[index].Add(entry);
                            if (owners.Count > 0 && directory) continue;
                            if (!directory || binary || current.Item2 + 1 >= MaximumDepth) continue;
                            if (dataOnly && current.Item2 == 0 && !knownPublishers.Overlaps(evidence)) continue;
                            if (applicationRoot && current.Item2 == 0 && !knownPublishers.Overlaps(evidence) && !aliases.ContainsKey(Normalize(name))) continue;
                            pending.Push(Tuple.Create(entry, current.Item2 + 1, evidence));
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            return locations;
        }
    }
}
