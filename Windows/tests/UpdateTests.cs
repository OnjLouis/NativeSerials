using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Onj.SignedUpdater;

namespace NativeSerials
{
    internal static class UpdateTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Reject(Action action) { try { action(); } catch { return; } throw new Exception("Unsafe update was accepted."); }
        public static int Main(string[] args)
        {
            if (args.SequenceEqual(new[] { "--wait-fixture" })) { Thread.Sleep(1200); return 0; }
            try { Run(); if (args.Length == 3) RealPackage(args); Console.WriteLine("Signed updater integration tests passed."); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void RealPackage(string[] args)
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "real-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "NativeSerials.exe"), "legacy program");
                File.WriteAllText(Path.Combine(target, "Manual.html"), "legacy manual");
                File.WriteAllText(Path.Combine(target, "personal.txt"), "preserve");
                var manifest = File.ReadAllBytes(args[1]); var signature = File.ReadAllText(args[2]);
                var policy = SignedUpdateAdapter.Policy();
                var result = SignedUpdateAdapter.Install(args[0], manifest, signature, new Version(1, 0), new Version(1, 1),
                    target, Path.Combine(root, "scratch"), Path.Combine(root, "backup"), policy);
                using (var zip = ZipFile.OpenRead(args[0])) foreach (var file in zip.Entries) {
                    using (var stream = file.Open()) using (var output = new MemoryStream()) {
                        stream.CopyTo(output);
                        Check(File.ReadAllBytes(Path.Combine(target, file.FullName)).SequenceEqual(output.ToArray()), "Final artifact installation mismatch");
                    }
                }
                Check(File.ReadAllText(Path.Combine(target, "personal.txt")) == "preserve", "Legacy migration changed unmanaged data");
                var installed = FileVersionInfo.GetVersionInfo(Path.Combine(target, "NativeSerials.exe"));
                Check(installed.FileVersion == "1.1.0.0", "Installed version differs");
                Check(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "scratch")).Any(), "Final artifact scratch not cleaned");
                Console.WriteLine("Final Windows package verified and installed over disposable legacy layout.");
            } finally { Directory.Delete(root, true); }
        }
        private static void Run()
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                using (var key = SigningKeys.CreatePrivateKey()) {
                    var policy = SignedUpdateAdapter.Policy(SigningKeys.ExportXml(key, false));
                    policy.RetryCount = 2; policy.RetryDelayMilliseconds = 10;
                    var zip = Path.Combine(root, "NativeSerialsWindows-1.2.zip");
                    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                        foreach (var name in SignedUpdateAdapter.ManagedFiles)
                            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("new " + name);
                    var signed = PackageSigner.Sign(zip, policy.Product, policy.Platform, new Version(1, 2, 0), key, policy);
                    var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
                    foreach (var name in SignedUpdateAdapter.ManagedFiles.Where(n => n != "ThirdPartyNotices.txt")) File.WriteAllText(Path.Combine(target, name), "old " + name);
                    Directory.CreateDirectory(Path.Combine(target, "User")); File.WriteAllText(Path.Combine(target, "User", "settings.ini"), "keep settings");
                    File.WriteAllText(Path.Combine(target, "unmanaged.txt"), "keep other files");
                    var scratch = Path.Combine(root, "scratch"); var backups = Path.Combine(root, "backups");
                    Action<byte[], string> install = (manifest, signature) => SignedUpdateAdapter.Install(zip, manifest, signature,
                        new Version(1, 1), new Version(1, 2), target, scratch, backups, policy);
                    Reject(() => install(signed.ManifestBytes, ""));
                    Reject(() => install(signed.ManifestBytes, "not a signature"));
                    var changed = (byte[])signed.ManifestBytes.Clone(); changed[changed.Length / 2] ^= 1;
                    Reject(() => install(changed, signed.Signature));
                    var other = SignedUpdateAdapter.Policy(SigningKeys.ExportXml(key, false)); other.Product = "DifferentProduct";
                    Reject(() => SignedUpdateAdapter.Install(zip, signed.ManifestBytes, signed.Signature, new Version(1, 1), new Version(1, 2), target, scratch, backups, other));
                    Reject(() => SignedUpdateAdapter.Install(zip, signed.ManifestBytes, signed.Signature, new Version(1, 2), new Version(1, 2), target, scratch, backups, policy));
                    using (var locked = new FileStream(Path.Combine(target, "Onj.SignedUpdater.dll"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        Reject(() => install(signed.ManifestBytes, signed.Signature));
                        foreach (var name in SignedUpdateAdapter.ManagedFiles.Where(n => n != "ThirdPartyNotices.txt"))
                            Check(File.ReadAllText(Path.Combine(target, name)) == "old " + name, "Rollback must restore every changed file");
                        Check(!File.Exists(Path.Combine(target, "ThirdPartyNotices.txt")), "Rollback must preserve prior absence");
                    }
                    install(signed.ManifestBytes, signed.Signature);
                    foreach (var name in SignedUpdateAdapter.ManagedFiles) Check(File.ReadAllText(Path.Combine(target, name)) == "new " + name, "Installed file mismatch");
                    Check(File.ReadAllText(Path.Combine(target, "User", "settings.ini")) == "keep settings", "User settings were changed");
                    Check(File.ReadAllText(Path.Combine(target, "unmanaged.txt")) == "keep other files", "Unknown files were changed");
                    Check(!Directory.EnumerateFileSystemEntries(scratch).Any(), "Transaction scratch must be empty");
                    var assets = new List<GitHubAsset>();
                    foreach (var name in new[] { Path.GetFileName(zip), Path.GetFileName(zip) + ".json", Path.GetFileName(zip) + ".json.sig" })
                        assets.Add(new GitHubAsset { Name = name, Size = 1, Url = UpdateService.ProjectUrl + "/releases/download/v1.2/" + name });
                    Check(SignedUpdateAdapter.Offer("v1.2", assets, new Version(1, 1)).Version == new Version(1, 2, 0, 0), "Two-component tag bridge");
                    Reject(() => SignedUpdateAdapter.Offer("v1.2", assets.Take(1).ToList(), new Version(1, 1)));
                    assets[0].Url = "https://example.invalid/update.zip";
                    Reject(() => SignedUpdateAdapter.Offer("v1.2", assets, new Version(1, 1)));
                }
                using (var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--wait-fixture") { UseShellExecute = false, CreateNoWindow = true })) {
                    var path = child.MainModule.FileName; var start = child.StartTime.ToUniversalTime();
                    Reject(() => ProcessGuard.WaitForExit(child.Id, path, start, TimeSpan.FromMilliseconds(20), CancellationToken.None));
                    Check(!child.HasExited, "A timed-out wait must not stop the process");
                    Reject(() => ProcessGuard.WaitForExit(child.Id, path + ".wrong", start, TimeSpan.FromSeconds(1), CancellationToken.None));
                    Check(child.WaitForExit(5000), "Disposable process fixture did not exit");
                }
            } finally { Directory.Delete(root, true); }
        }
    }
}
