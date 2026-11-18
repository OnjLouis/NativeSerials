using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Onj.SignedUpdater;

namespace NativeSerials
{
    internal static class SignedUpdateAdapter
    {
        internal static readonly string[] ManagedFiles = { "NativeSerials.exe", "Onj.SignedUpdater.dll", "Manual.html", "ThirdPartyNotices.txt" };
        private const SecurityProtocolType Tls12Protocol = (SecurityProtocolType)3072;
        private static readonly string WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeSerials", "Updates");

        internal static Version NumericVersion(string text)
        {
            Version value;
            if (!Version.TryParse(text.TrimStart('v', 'V'), out value)) throw new InvalidDataException("Invalid update version.");
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        internal static UpdatePolicy Policy()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NativeSerials.UpdatePublicKey.xml"))
            {
                if (stream == null) throw new InvalidDataException("The update verification key is missing.");
                using (var reader = new StreamReader(stream)) return Policy(reader.ReadToEnd());
            }
        }

        internal static UpdatePolicy Policy(string verificationKey)
        {
            return new UpdatePolicy { Product = "NativeSerials", Platform = "windows-x64", PublicKeyXml = verificationKey,
                ManagedPaths = ManagedFiles, RequiredFiles = ManagedFiles, MaximumPackageBytes = 16 * 1024 * 1024,
                MaximumExtractedBytes = 32 * 1024 * 1024, MaximumEntries = 32, RetryCount = 10, RetryDelayMilliseconds = 500 };
        }

        internal static UpdateOffer Offer(string tag, IList<GitHubAsset> assets, Version current)
        {
            if (tag == null || !Regex.IsMatch(tag, "^v?[0-9]{1,9}(\\.[0-9]{1,9}){1,3}$")) throw new InvalidDataException("The release tag is invalid.");
            var name = "NativeSerialsWindows-" + tag.TrimStart('v') + ".zip";
            var prefix = UpdateService.ProjectUrl + "/releases/download/" + tag + "/";
            foreach (var asset in assets ?? new List<GitHubAsset>())
                if (asset != null && (asset.Name == name || asset.Name == name + ".json" || asset.Name == name + ".json.sig") && asset.Url != prefix + asset.Name)
                    throw new InvalidDataException("A release asset has an unexpected download address.");
            // The shared wire format uses three/four numeric components; display tags may use two.
            var release = new GitHubRelease { Tag = "v" + NumericVersion(tag), Assets = assets == null ? null : assets.ToList() };
            return GitHubUpdates.Select(new[] { release }, "v", name, NumericVersion(current.ToString()));
        }

        internal static void Start(string tag, Action exitApp)
        {
            var job = Path.Combine(WorkRoot, "job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            var worker = Path.Combine(job, "NativeSerialsUpdater.exe");
            File.Copy(Application.ExecutablePath, worker);
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Onj.SignedUpdater.dll"), Path.Combine(job, "Onj.SignedUpdater.dll"));
            var origin = Process.GetCurrentProcess();
            var target = StorageScanner.CanonicalLocation(AppDomain.CurrentDomain.BaseDirectory);
            var arguments = "--apply-update --update-tag " + Quote(tag) + " --update-target " + Quote(target) +
                " --update-wait-pid " + origin.Id + " --update-wait-exe " + Quote(origin.MainModule.FileName) +
                " --update-wait-start " + Quote(origin.StartTime.ToUniversalTime().ToString("O")) +
                " --update-current " + Quote(FileVersionInfo.GetVersionInfo(Application.ExecutablePath).FileVersion);
            Process.Start(new ProcessStartInfo(worker, arguments) { WorkingDirectory = job, UseShellExecute = false, CreateNoWindow = true });
            exitApp();
        }

        internal static bool Run(string[] args)
        {
            Cleanup(args);
            if (args == null || !args.Contains("--apply-update")) return false;
            string target = null;
            var job = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var log = new StringBuilder();
            bool installed = false;
            try
            {
                if (!IsJob(job)) throw new InvalidDataException("The updater is not running from its own staging folder.");
                target = StorageScanner.CanonicalLocation(Required(args, "--update-target"));
                var pid = int.Parse(Required(args, "--update-wait-pid"));
                var start = DateTime.Parse(Required(args, "--update-wait-start"), null, System.Globalization.DateTimeStyles.RoundtripKind);
                ProcessGuard.WaitForExit(pid, Required(args, "--update-wait-exe"), start, TimeSpan.FromSeconds(30), CancellationToken.None);
                var current = NumericVersion(Required(args, "--update-current"));
                var tag = Required(args, "--update-tag");
                var offer = FetchOffer(tag, current);
                var policy = Policy();
                policy.Status = message => { if (log.Length < 49152) log.AppendLine(message); };
                DownloadedUpdate downloaded;
                using (var client = new GitHubUpdates(new TrustedUpdateHosts(), TimeSpan.FromMinutes(2)))
                    downloaded = client.DownloadAsync(offer, policy, Path.Combine(job, "download"), CancellationToken.None).GetAwaiter().GetResult();
                var backup = BackupDirectory(target);
                UpdateInstaller.Recover(target, Path.Combine(job, "recovery"), backup, policy);
                Install(downloaded.PackagePath, downloaded.ManifestBytes, downloaded.Signature, current, offer.Version,
                    target, Path.Combine(job, "install"), backup, policy);
                installed = true;
                WriteLog(log.ToString());
                Restart(target, job);
            }
            catch (Exception error)
            {
                log.AppendLine(error.ToString()); WriteLog(log.ToString());
                MessageBox.Show("Native Serials could not finish the update." + Environment.NewLine + Environment.NewLine + error.Message +
                    Environment.NewLine + Environment.NewLine + (installed ? "The verified update is installed. Open Native Serials again manually." : "The previous program files were retained or restored. If recovery was blocked by a file lock, retain the update backup and contact support."),
                    "Native Serials updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (!installed && target != null && File.Exists(Path.Combine(target, "NativeSerials.exe")))
                    try { Restart(target, job); } catch { }
            }
            return true;
        }

        internal static InstallResult Install(string zip, byte[] manifest, string signature, Version current, Version expected,
            string target, string scratch, string backup, UpdatePolicy policy)
        {
            return UpdateInstaller.Install(zip, manifest, signature, NumericVersion(current.ToString()), NumericVersion(expected.ToString()), policy, target, scratch, backup);
        }

        private static UpdateOffer FetchOffer(string tag, Version current)
        {
            // The standalone Framework worker does not inherit the main app's TLS settings.
            ServicePointManager.SecurityProtocol = Tls12Protocol;
            if (!Regex.IsMatch(tag, "^v?[0-9]{1,9}(\\.[0-9]{1,9}){1,3}$")) throw new InvalidDataException("Invalid release tag.");
            using (var client = new HttpClient(new TrustedUpdateHosts()) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 1048576 })
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/OnjLouis/NativeSerials/releases/tags/" + tag))
            {
                request.Headers.UserAgent.ParseAdd("NativeSerials-Updater/1.1");
                using (var response = client.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 1048576) throw new InvalidDataException("The release metadata is too large.");
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
                            if (output.Length + count > 1048576) throw new InvalidDataException("The release metadata is too large.");
                            output.Write(buffer, 0, count);
                        }
                        output.Position = 0;
                        var release = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(output);
                        if (release == null || release.Tag != tag || release.Draft || release.Prerelease) throw new InvalidDataException("The selected release is not public and stable.");
                        var offer = Offer(tag, release.Assets, current);
                        if (offer == null) throw new InvalidDataException("The selected release is not newer than the installed version.");
                        return offer;
                    }
                }
            }
        }

        private static string BackupDirectory(string target)
        {
            using (var hash = SHA256.Create()) {
                var id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(target.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
                return Path.Combine(WorkRoot, "Backups", id);
            }
        }
        private static void Restart(string target, string job)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(target, "NativeSerials.exe"), "--cleanup-update " + Quote(job)) { WorkingDirectory = target, UseShellExecute = true });
        }
        private static void Cleanup(string[] args)
        {
            var job = Value(args, "--cleanup-update");
            if (string.IsNullOrEmpty(job) || !IsJob(job)) return;
            Task.Run(delegate {
                for (int attempt = 0; attempt < 20; attempt++) {
                    try {
                        if ((File.GetAttributes(job) & FileAttributes.ReparsePoint) != 0) return;
                        if (HasLinks(job)) return;
                        Directory.Delete(job, true); return;
                    } catch (DirectoryNotFoundException) { return; }
                    catch (FileNotFoundException) { return; }
                    catch (IOException) { Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(500); }
                }
            });
        }
        private static bool IsJob(string path)
        {
            try {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                var parent = Path.GetDirectoryName(full);
                return string.Equals(parent, WorkRoot, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(Path.GetFileName(full), "^job-[a-f0-9]{32}$");
            } catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        private static bool HasLinks(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            int count = 0;
            while (pending.Count > 0) foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop())) {
                if (++count > 10000) return true;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
            return false;
        }
        private static void WriteLog(string text)
        {
            try { Directory.CreateDirectory(WorkRoot); File.WriteAllText(Path.Combine(WorkRoot, "LastUpdate.log"), text.Length > 65536 ? text.Substring(text.Length - 65536) : text); } catch { }
        }
        private static string Required(string[] args, string name) { var value = Value(args, name); if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing updater argument: " + name); return value; }
        private static string Value(string[] args, string name) { if (args != null) for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1]; return ""; }
        private static string Quote(string value) { if (value == null || value.Contains("\"") || value.EndsWith("\\")) throw new ArgumentException("Invalid updater argument."); return "\"" + value + "\""; }

        private sealed class TrustedUpdateHosts : DelegatingHandler
        {
            internal TrustedUpdateHosts() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri;
                bool allowed = url != null && url.Scheme == "https" && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Fragment) &&
                    ((url.Host == "api.github.com" && url.AbsolutePath.StartsWith("/repos/OnjLouis/NativeSerials/", StringComparison.Ordinal) && string.IsNullOrEmpty(url.Query)) ||
                     (url.Host == "github.com" && url.AbsolutePath.StartsWith("/OnjLouis/NativeSerials/releases/download/", StringComparison.Ordinal) && string.IsNullOrEmpty(url.Query)) ||
                     (url.Host == "release-assets.githubusercontent.com" && url.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)));
                if (!allowed) throw new InvalidDataException("The update request used an unexpected host or address.");
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
