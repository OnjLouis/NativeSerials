using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
                var notesText = FormatReleaseNotesForDialog(release == null ? string.Empty : release.Body, "No release notes were provided for this update.");
                using (var dialog = new TextViewerForm("Version history - " + version, "Latest release: " + version + Environment.NewLine + Environment.NewLine + notesText, "Version history " + version, "Review the changes published for this version. Select text to copy an excerpt, or use Copy All for the complete notes.", true))
                {
                    dialog.ShowDialog(owner);
                }
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
            return SignedUpdateAdapter.Run(args);
        }

        private static void ShowUpdateAvailableDialog(IWin32Window owner, GitHubReleaseInfo release, IEnumerable<GitHubReleaseInfo> releases, Version current, Version remote, Action exitApp)
        {
            using (var dialog = CreateUpdateAvailableDialog(owner, release, releases, current, remote, exitApp))
                dialog.ShowDialog(owner);
        }

        private static Form CreateUpdateAvailableDialog(IWin32Window owner, GitHubReleaseInfo release, IEnumerable<GitHubReleaseInfo> releases, Version current, Version remote, Action exitApp)
        {
            var latest = release == null ? remote.ToString() : (release.TagName ?? remote.ToString());
            var releaseUrl = release == null || string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl;
            var zipAsset = FindPortableZipAsset(release);
            Onj.SignedUpdater.UpdateOffer signedOffer = null;
            try {
                if (zipAsset != null) signedOffer = SignedUpdateAdapter.Offer(release.TagName,
                    release.Assets.Select(a => new Onj.SignedUpdater.GitHubAsset { Name = a.Name, Url = a.BrowserDownloadUrl, Size = a.Size }).ToList(), current);
            } catch (InvalidDataException) { }
            var releaseNotes = BuildUpdateReleaseNotes(releases, current, remote);

            var dialog = new Form();
            try
            {
                dialog.Text = "Update available";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Width = 720;
                dialog.Height = 520;
                dialog.MinimumSize = new Size(520, 360);
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowIcon = false;
                dialog.ShowInTaskbar = false;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Top, Text = "Native Serials " + latest + " is available.", Padding = new Padding(0, 0, 0, 8) }, 0, 0);
                layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = releaseNotes, AccessibleName = "Release notes", AccessibleDescription = "Changes included in the available update. Read or select the text with the arrow keys." }, 0, 1);

                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
                var laterButton = new Button { Text = "&Later", DialogResult = DialogResult.Cancel, AutoSize = true };
                var releaseButton = new Button { Text = "Open &release page", AutoSize = true };
                releaseButton.Click += delegate { OpenUrl(releaseUrl); };
                if (signedOffer != null)
                {
                    var installButton = new Button { Text = "&Download and install", AutoSize = true };
                    installButton.Click += delegate
                    {
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                        StartSelfUpdate(owner, release.TagName, exitApp);
                    };
                    buttons.Controls.Add(installButton);
                    dialog.AcceptButton = installButton;
                }
                buttons.Controls.Add(releaseButton);
                buttons.Controls.Add(laterButton);
                dialog.CancelButton = laterButton;
                layout.Controls.Add(buttons, 0, 2);
                dialog.Controls.Add(layout);
                HelpDialog.Prepare(dialog);
                return dialog;
            }
            catch { dialog.Dispose(); throw; }
        }

        private static void StartSelfUpdate(IWin32Window owner, string tag, Action exitApp)
        {
            if (MessageBox.Show(owner, "Native Serials will close, verify the signed update, back up its program files, install and restart. Other files in this folder will be kept." + Environment.NewLine + Environment.NewLine + "Do you want to continue?", "Download and install", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                SignedUpdateAdapter.Start(tag, exitApp);
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
            var client = new BoundedWebClient();
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
            var name = "NativeSerialsWindows-" + release.TagName.TrimStart('v', 'V') + ".zip";
            var choices = release.Assets.Where(a => a != null && a.Name == name).ToList();
            return choices.Count == 1 ? choices[0] : null;
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
            object flag;
            if ((map.TryGetValue("draft", out flag) && flag is bool && (bool)flag) ||
                (map.TryGetValue("prerelease", out flag) && flag is bool && (bool)flag)) return null;
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
                        long size;
                        long.TryParse(GetString(assetMap, "size"), out size);
                        release.Assets.Add(new GitHubReleaseAsset { Name = GetString(assetMap, "name"), BrowserDownloadUrl = GetString(assetMap, "browser_download_url"), Size = size });
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
            public long Size;
        }

        private sealed class BoundedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                request.Timeout = 20000;
                var http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = 20000;
                return request;
            }
        }
    }
}
