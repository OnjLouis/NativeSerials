using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NativeSerials
{
    internal static class UpdateService
    {
        public const string ProjectUrl = "https://github.com/OnjLouis/NativeSerials";
        private const string UserAgent = "Native Serials updater";
        private const string ContactUrl = "https://onj.me/contact";
        private const string DonateUrl = "https://onj.me/donate";

        public static void CheckForUpdates(IWin32Window owner, string currentVersion, Action exitApp)
        {
            try
            {
                var releases = FetchReleases();
                var release = LatestVersionedRelease(releases) ?? FetchLatestRelease();
                var latestVersionText = release == null ? string.Empty : (release.TagName ?? string.Empty).Trim().TrimStart('v', 'V');
                Version current;
                Version remote;
                if (string.IsNullOrWhiteSpace(latestVersionText) ||
                    !Version.TryParse(currentVersion, out current) ||
                    !Version.TryParse(latestVersionText, out remote))
                {
                    MessageBox.Show(owner, "Could not read the latest Native Serials release version.", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (remote <= current)
                {
                    MessageBox.Show(owner, "Native Serials is up to date. Current version: " + currentVersion + ".", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ShowUpdateAvailableDialog(owner, release, releases, current, remote, exitApp);
            }
            catch (WebException ex)
            {
                MessageBox.Show(owner, "Could not check for updates. GitHub releases may not exist yet, or the network request failed." + Environment.NewLine + Environment.NewLine + ex.Message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void CheckForUpdatesAutomatic(IWin32Window owner, string currentVersion, Action exitApp)
        {
            try
            {
                var releases = FetchReleases();
                var release = LatestVersionedRelease(releases) ?? FetchLatestRelease();
                var latestVersionText = release == null ? string.Empty : (release.TagName ?? string.Empty).Trim().TrimStart('v', 'V');
                Version current;
                Version remote;
                if (string.IsNullOrWhiteSpace(latestVersionText) ||
                    !Version.TryParse(currentVersion, out current) ||
                    !Version.TryParse(latestVersionText, out remote) ||
                    remote <= current)
                {
                    return;
                }

                ShowUpdateAvailableDialog(owner, release, releases, current, remote, exitApp);
            }
            catch
            {
            }
        }

        public static void ShowVersionHistory(IWin32Window owner, string currentVersion)
        {
            try
            {
                var releases = FetchReleases();
                var release = LatestVersionedRelease(releases) ?? FetchLatestRelease();
                var version = release == null ? currentVersion : (release.TagName ?? currentVersion).Trim().TrimStart('v', 'V');
                var releaseUrl = release == null || string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl;
                var notesText = FormatReleaseNotesForDialog(release == null ? string.Empty : release.Body, "No release notes were provided for this update.");
                using (var dialog = new TextViewerForm("Version history - " + version, "Latest release: " + version + Environment.NewLine + Environment.NewLine + notesText, "Version history " + version, "Native Serials release notes.", true))
                {
                    dialog.ShowDialog(owner);
                }
                OpenUrl(releaseUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Could not check version history. GitHub releases may not exist yet, or the network request failed." + Environment.NewLine + Environment.NewLine + ex.Message, "Version history", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        public static void OpenProjectPage()
        {
            OpenUrl(ProjectUrl);
        }

        public static void OpenContactPage()
        {
            OpenUrl(ContactUrl);
        }

        public static void OpenDonatePage()
        {
            OpenUrl(DonateUrl);
        }

        public static bool TryApplyUpdate(string[] args)
        {
            if (args == null || !args.Contains("--apply-update")) return false;
            try
            {
                var zipUrl = ArgValue(args, "--update-url");
                var targetDir = ArgValue(args, "--update-target");
                var exePath = ArgValue(args, "--update-exe");
                var waitPidText = ArgValue(args, "--update-wait-pid");
                int waitPid;
                if (int.TryParse(waitPidText, out waitPid))
                {
                    try
                    {
                        var process = Process.GetProcessById(waitPid);
                        process.WaitForExit(30000);
                    }
                    catch
                    {
                    }
                }

                var temp = Path.Combine(Path.GetTempPath(), "NativeSerialsUpdate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                var zipPath = Path.Combine(temp, "NativeSerialsUpdate.zip");
                using (var client = CreateGitHubClient())
                {
                    client.DownloadFile(zipUrl, zipPath);
                }
                var extract = Path.Combine(temp, "extract");
                Directory.CreateDirectory(extract);
                ZipFile.ExtractToDirectory(zipPath, extract);
                var stagedExe = Directory.GetFiles(extract, "NativeSerials.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (stagedExe == null) throw new FileNotFoundException("The update ZIP did not contain NativeSerials.exe.");
                CopyDirectory(Path.GetDirectoryName(stagedExe), targetDir);
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                try { Directory.Delete(temp, true); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Native Serials update failed:" + Environment.NewLine + Environment.NewLine + ex.Message, "Native Serials updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }

        private static void ShowUpdateAvailableDialog(IWin32Window owner, GitHubReleaseInfo release, IEnumerable<GitHubReleaseInfo> releases, Version current, Version remote, Action exitApp)
        {
            var latest = release == null ? remote.ToString() : (release.TagName ?? remote.ToString());
            var releaseUrl = release == null || string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl;
            var zipAsset = FindPortableZipAsset(release);
            var releaseNotes = BuildUpdateReleaseNotes(releases, current, remote);

            using (var dialog = new Form())
            {
                dialog.Text = "Update available";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Width = 720;
                dialog.Height = 520;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowIcon = false;
                dialog.ShowInTaskbar = false;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Top, Text = "Native Serials " + latest + " is available.", Padding = new Padding(0, 0, 0, 8) }, 0, 0);
                layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = releaseNotes, AccessibleName = "Release notes" }, 0, 1);

                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
                var laterButton = new Button { Text = "&Later", DialogResult = DialogResult.Cancel, AutoSize = true };
                var releaseButton = new Button { Text = "Open &release page", AutoSize = true };
                releaseButton.Click += delegate { OpenUrl(releaseUrl); };
                if (zipAsset != null && !string.IsNullOrWhiteSpace(zipAsset.BrowserDownloadUrl))
                {
                    var installButton = new Button { Text = "&Download and install", AutoSize = true };
                    installButton.Click += delegate
                    {
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                        StartSelfUpdate(owner, zipAsset.BrowserDownloadUrl, exitApp);
                    };
                    buttons.Controls.Add(installButton);
                    dialog.AcceptButton = installButton;
                }
                buttons.Controls.Add(releaseButton);
                buttons.Controls.Add(laterButton);
                dialog.CancelButton = laterButton;
                layout.Controls.Add(buttons, 0, 2);
                dialog.Controls.Add(layout);
                dialog.ShowDialog(owner);
            }
        }

        private static void StartSelfUpdate(IWin32Window owner, string zipUrl, Action exitApp)
        {
            if (MessageBox.Show(owner, "Native Serials will close, download the update, replace the files in this folder, and restart." + Environment.NewLine + Environment.NewLine + "Do you want to continue?", "Download and install", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var exePath = Application.ExecutablePath;
                var updaterRoot = Path.Combine(Path.GetTempPath(), "NativeSerialsUpdater-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(updaterRoot);
                var updaterExe = Path.Combine(updaterRoot, "NativeSerials Updater.exe");
                File.Copy(exePath, updaterExe, true);
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterExe,
                    Arguments = "--apply-update --update-url " + Quote(zipUrl) + " --update-target " + Quote(appDir) + " --update-exe " + Quote(exePath) + " --update-wait-pid " + Process.GetCurrentProcess().Id,
                    WorkingDirectory = updaterRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (exitApp != null) exitApp();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "Could not start updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static GitHubReleaseInfo FetchLatestRelease()
        {
            using (var client = CreateGitHubClient())
            {
                return ParseRelease(client.DownloadString(ApiBaseUrl() + "/releases/latest"));
            }
        }

        private static List<GitHubReleaseInfo> FetchReleases()
        {
            using (var client = CreateGitHubClient())
            {
                return ParseReleases(client.DownloadString(ApiBaseUrl() + "/releases?per_page=100"));
            }
        }

        private static WebClient CreateGitHubClient()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var client = new WebClient();
            client.Headers.Add("User-Agent", UserAgent);
            return client;
        }

        private static string ApiBaseUrl()
        {
            return ProjectUrl.Replace("https://github.com/", "https://api.github.com/repos/");
        }

        private static GitHubReleaseInfo LatestVersionedRelease(IEnumerable<GitHubReleaseInfo> releases)
        {
            return (releases ?? new List<GitHubReleaseInfo>())
                .Select(r => new { Release = r, Version = ReleaseVersion(r) })
                .Where(i => i.Version != null)
                .OrderByDescending(i => i.Version)
                .Select(i => i.Release)
                .FirstOrDefault();
        }

        private static Version ReleaseVersion(GitHubReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.TagName)) return null;
            Version version;
            return Version.TryParse(release.TagName.Trim().TrimStart('v', 'V'), out version) ? version : null;
        }

        private static string BuildUpdateReleaseNotes(IEnumerable<GitHubReleaseInfo> releases, Version current, Version latest)
        {
            var newerReleases = (releases ?? new List<GitHubReleaseInfo>())
                .Select(r => new { Release = r, Version = ReleaseVersion(r) })
                .Where(i => i.Version != null && i.Version > current && i.Version <= latest)
                .OrderBy(i => i.Version)
                .ToList();
            var builder = new StringBuilder();
            builder.AppendLine("Your version: " + current);
            builder.AppendLine("New version: " + latest);
            builder.AppendLine();
            builder.AppendLine("Changes between " + current + " and " + latest);
            if (newerReleases.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine("No release notes were provided for this update.");
                return builder.ToString().TrimEnd();
            }
            foreach (var item in newerReleases)
            {
                builder.AppendLine();
                builder.AppendLine(item.Release.TagName);
                builder.AppendLine(FormatReleaseNotesForDialog(item.Release.Body, "No release notes were provided for this update."));
            }
            return builder.ToString().TrimEnd();
        }

        private static GitHubReleaseAsset FindPortableZipAsset(GitHubReleaseInfo release)
        {
            if (release == null || release.Assets == null) return null;
            return release.Assets
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) && !string.IsNullOrWhiteSpace(a.Name))
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.IndexOf("nativeserialswindows", StringComparison.OrdinalIgnoreCase) >= 0 || a.Name.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(a => a.Name.IndexOf("nativeserials", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
        }

        private static string FormatReleaseNotesForDialog(string markdown, string emptyText)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return emptyText;
            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
                .Select(line => line.TrimEnd())
                .Select(line => line.StartsWith("#", StringComparison.Ordinal) ? line.TrimStart('#').Trim() : line)
                .Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? "  " + line.Substring(2) : line)
                .Select(line => line.StartsWith("* ", StringComparison.Ordinal) ? "  " + line.Substring(2) : line);
            return string.Join(Environment.NewLine, lines).Trim();
        }

        private static List<GitHubReleaseInfo> ParseReleases(string json)
        {
            var rows = new JavaScriptSerializer().DeserializeObject(json) as object[];
            if (rows == null) return new List<GitHubReleaseInfo>();
            return rows.Select(ParseReleaseObject).Where(r => r != null).ToList();
        }

        private static GitHubReleaseInfo ParseRelease(string json)
        {
            return ParseReleaseObject(new JavaScriptSerializer().DeserializeObject(json));
        }

        private static GitHubReleaseInfo ParseReleaseObject(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) return null;
            var release = new GitHubReleaseInfo { TagName = GetString(map, "tag_name"), HtmlUrl = GetString(map, "html_url"), Body = GetString(map, "body"), Assets = new List<GitHubReleaseAsset>() };
            object assetsValue;
            if (map.TryGetValue("assets", out assetsValue))
            {
                var assets = assetsValue as object[];
                if (assets != null)
                {
                    foreach (var assetValue in assets)
                    {
                        var assetMap = assetValue as Dictionary<string, object>;
                        if (assetMap == null) continue;
                        release.Assets.Add(new GitHubReleaseAsset { Name = GetString(assetMap, "name"), BrowserDownloadUrl = GetString(assetMap, "browser_download_url") });
                    }
                }
            }
            return release;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : string.Empty;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }

        private static string ArgValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return string.Empty;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        private sealed class GitHubReleaseInfo
        {
            public string TagName;
            public string HtmlUrl;
            public string Body;
            public List<GitHubReleaseAsset> Assets;
        }

        private sealed class GitHubReleaseAsset
        {
            public string Name;
            public string BrowserDownloadUrl;
        }
    }
}
