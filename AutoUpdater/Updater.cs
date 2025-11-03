using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AutoUpdater {
    public class Updater : IDisposable {
        private const string VersionFileName = ".autoupdater-version";

        private readonly UpdaterConfig _config;
        private readonly Action<string> _statusCallback;
        private readonly HttpClient _httpClient;
        private readonly JavaScriptSerializer _serializer;
        private bool _disposed;

        public Updater(UpdaterConfig config, Action<string> statusCallback) {
            if (config == null) throw new ArgumentNullException("config");

            _config = config;
            _statusCallback = statusCallback ?? delegate { };
            _serializer = new JavaScriptSerializer();
            _httpClient = CreateHttpClient(config);
        }

        public async Task<UpdateCheckResult> CheckRemoteAsync() {
            EnsureNotDisposed();

            var result = new UpdateCheckResult();
            result.Mode = _config.CheckMode;

            var localVersion = ReadInstalledVersion();
            result.LocalVersion = localVersion;
            result.LocalDisplay = string.IsNullOrEmpty(localVersion) ? "(���� ����)" : localVersion;

            if (_config.CheckMode == "commit") {
                var commit = await FetchLatestCommitAsync();
                if (commit == null || string.IsNullOrEmpty(commit.Sha)) {
                    result.RemoteDisplay = "���� ����";
                    return result;
                }

                result.RemoteVersion = commit.Sha;
                result.RemoteDisplay = ShortenSha(commit.Sha);
                result.DownloadUrl = BuildCommitDownloadUrl(commit.Sha);
                result.AssetFileName = _config.RepoName + "-" + ShortenSha(commit.Sha) + ".zip";
                result.HasUpdate = !AreVersionsEqual(localVersion, commit.Sha);
                return result;
            }

            var release = await FetchLatestReleaseAsync();
            if (release == null) {
                result.RemoteDisplay = "���� ����";
                return result;
            }

            var asset = SelectAsset(release);
            if (asset == null) {
                result.RemoteDisplay = string.IsNullOrEmpty(release.TagName) ? release.Name ?? "release" : release.TagName;
                return result;
            }

            result.RemoteVersion = PickReleaseVersionString(release);
            result.RemoteDisplay = result.RemoteVersion;
            result.DownloadUrl = asset.BrowserDownloadUrl;
            result.AssetFileName = asset.Name;

            var shaAsset = FindSha256Asset(release, asset);
            if (shaAsset != null) {
                result.Sha256DownloadUrl = shaAsset.BrowserDownloadUrl;
            }

            result.HasUpdate = !AreVersionsEqual(localVersion, result.RemoteVersion);
            return result;
        }

        public async Task UpdateAsync(UpdateCheckResult checkResult) {
            EnsureNotDisposed();

            if (checkResult == null) throw new ArgumentNullException("checkResult");
            if (string.IsNullOrEmpty(checkResult.DownloadUrl)) throw new InvalidOperationException("�ٿ�ε� URL�� �����ϴ�.");

            _statusCallback("�ٿ�ε� ���� ��...");

            var tempRoot = Path.Combine(Path.GetTempPath(), "AutoUpdater" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(tempRoot);

            string tempFilePath = Path.Combine(tempRoot, checkResult.AssetFileName ?? "update.bin");

            try {
                await DownloadFileAsync(checkResult.DownloadUrl, tempFilePath);

                if (_config.UseSha256) {
                    await ValidateSha256Async(checkResult, tempFilePath);
                }

                BackupExisting();

                Deploy(tempFilePath);

                if (!string.IsNullOrEmpty(checkResult.RemoteVersion)) {
                    WriteInstalledVersion(checkResult.RemoteVersion);
                }
            }
            finally {
                TryDeleteDirectory(tempRoot);
            }
        }

        public void TryStartTargetApp() {
            EnsureNotDisposed();

            var exePath = GetExecutablePath();
            if (!File.Exists(exePath)) {
                return;
            }

            try {
                var startInfo = new ProcessStartInfo(exePath) {
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? _config.InstallDir,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch (Exception ex) {
                _statusCallback("���� ����: " + ex.Message);
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _httpClient.Dispose();
            _disposed = true;
        }

        private HttpClient CreateHttpClient(UpdaterConfig config) {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoUpdater", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (!string.IsNullOrEmpty(config.GitHubTokenEnv)) {
                var token = Environment.GetEnvironmentVariable(config.GitHubTokenEnv);
                if (!string.IsNullOrEmpty(token)) {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
                }
            }

            return client;
        }

        private async Task DownloadFileAsync(string url, string destinationPath) {
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var output = File.Create(destinationPath)) {
                await stream.CopyToAsync(output);
            }
        }

        private async Task ValidateSha256Async(UpdateCheckResult checkResult, string filePath) {
            var expected = await ResolveSha256Async(checkResult);
            if (string.IsNullOrEmpty(expected)) {
                throw new InvalidOperationException("SHA256 �˻縦 �� �� �����ϴ�.");
            }

            var actual = ComputeSha256(filePath);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("SHA256 ������ �ʽ��ϴ�.");
            }
        }

        private async Task<string> ResolveSha256Async(UpdateCheckResult checkResult) {
            if (!string.IsNullOrEmpty(checkResult.Sha256Value)) {
                return NormalizeSha256(checkResult.Sha256Value);
            }

            if (string.IsNullOrEmpty(checkResult.Sha256DownloadUrl)) {
                return string.Empty;
            }

            var response = await _httpClient.GetAsync(checkResult.Sha256DownloadUrl);
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync();
            return ParseSha256FromText(text);
        }

        private string ParseSha256FromText(string text) {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines) {
                var trimmed = line.Trim();
                if (trimmed.Length < 64) continue;

                for (int i = 0; i < trimmed.Length; i++) {
                    var c = trimmed[i];
                    if (!IsHexChar(c)) {
                        trimmed = trimmed.Substring(0, i);
                        break;
                    }
                }

                if (trimmed.Length >= 64) {
                    return NormalizeSha256(trimmed);
                }
            }

            return string.Empty;
        }

        private string NormalizeSha256(string value) {
            var sb = new StringBuilder();
            foreach (var c in value) {
                if (IsHexChar(c)) {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        private bool IsHexChar(char c) {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private string ComputeSha256(string filePath) {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath)) {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        private void BackupExisting() {
            if (string.IsNullOrEmpty(_config.BackupDir)) {
                return;
            }

            var sourcePath = GetExecutablePath();
            if (!File.Exists(sourcePath)) {
                return;
            }

            try {
                Directory.CreateDirectory(_config.BackupDir);
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                var fileName = Path.GetFileNameWithoutExtension(sourcePath) + "-" + timestamp + Path.GetExtension(sourcePath);
                var destination = Path.Combine(_config.BackupDir, fileName);
                File.Copy(sourcePath, destination, true);
            }
            catch (Exception ex) {
                _statusCallback("���μ��� ����: " + ex.Message);
            }
        }

        private void Deploy(string downloadedFilePath) {
            var installDir = _config.InstallDir;
            if (string.IsNullOrEmpty(installDir)) {
                throw new InvalidOperationException("���� ���丮�� �����ϴ�.");
            }

            Directory.CreateDirectory(installDir);

            var extension = Path.GetExtension(downloadedFilePath);
            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)) {
                ExtractZip(downloadedFilePath, installDir);
            }
            else {
                var destination = Path.Combine(installDir, _config.ExecutableName);
                File.Copy(downloadedFilePath, destination, true);
            }
        }

        private void ExtractZip(string zipPath, string destinationDir) {
            var tempDir = Path.Combine(Path.GetTempPath(), "AutoUpdater_extract_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(tempDir);

            try {
                using (var archive = ZipFile.OpenRead(zipPath)) {
                    foreach (var entry in archive.Entries) {
                        var targetPath = Path.Combine(tempDir, entry.FullName);
                        var targetDirectory = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDirectory)) {
                            Directory.CreateDirectory(targetDirectory);
                        }

                        if (string.IsNullOrEmpty(entry.Name)) {
                            continue;
                        }

                        entry.ExtractToFile(targetPath, true);
                    }
                }

                var contentRoot = ResolveSingleDirectory(tempDir);
                CopyDirectory(contentRoot, destinationDir);
            }
            finally {
                TryDeleteDirectory(tempDir);
            }
        }

        private string ResolveSingleDirectory(string root) {
            var directories = Directory.GetDirectories(root);
            if (directories.Length == 1 && Directory.GetFiles(root).Length == 0) {
                return directories[0];
            }
            return root;
        }

        private void CopyDirectory(string sourceDir, string destinationDir) {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)) {
                var relative = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(destinationDir, relative);
                Directory.CreateDirectory(target);
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)) {
                var relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.Combine(destinationDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destinationDir);
                File.Copy(file, target, true);
            }
        }

        private void TryDeleteDirectory(string path) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, true);
                }
            }
            catch {
                // ignored
            }
        }

        private GitHubAsset SelectAsset(GitHubRelease release) {
            if (release == null) return null;
            if (release.Assets == null) return null;

            if (string.IsNullOrEmpty(_config.AssetNamePattern)) {
                return release.Assets.FirstOrDefault();
            }

            GitHubAsset match = null;
            var regex = new Regex(_config.AssetNamePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (var asset in release.Assets) {
                if (asset == null || string.IsNullOrEmpty(asset.Name)) continue;
                if (regex.IsMatch(asset.Name)) {
                    match = asset;
                    break;
                }
            }

            return match;
        }

        private GitHubAsset FindSha256Asset(GitHubRelease release, GitHubAsset targetAsset) {
            if (release == null || release.Assets == null || targetAsset == null) {
                return null;
            }

            var baseName = targetAsset.Name;
            if (string.IsNullOrEmpty(baseName)) return null;

            var candidates = new List<string> {
                baseName + ".sha256",
                baseName + ".sha256.txt",
                baseName + ".sha256sum"
            };

            foreach (var asset in release.Assets) {
                if (asset == null || string.IsNullOrEmpty(asset.Name)) continue;
                if (candidates.Contains(asset.Name, StringComparer.OrdinalIgnoreCase)) {
                    return asset;
                }
            }

            return null;
        }

        private string PickReleaseVersionString(GitHubRelease release) {
            if (!string.IsNullOrEmpty(release.TagName)) return release.TagName;
            if (!string.IsNullOrEmpty(release.Name)) return release.Name;
            if (!string.IsNullOrEmpty(release.PublishedAt)) return release.PublishedAt;
            return "release";
        }

        private async Task<GitHubRelease> FetchLatestReleaseAsync() {
            var url = string.Format(CultureInfo.InvariantCulture, "https://api.github.com/repos/{0}/{1}/releases", _config.RepoOwner, _config.RepoName);
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var rawList = _serializer.Deserialize<List<Dictionary<string, object>>>(json);
            if (rawList == null) return null;

            foreach (var raw in rawList) {
                if (raw == null) continue;
                var release = CreateRelease(raw);
                if (release.Draft) continue;
                return release;
            }

            return null;
        }

        private async Task<GitHubCommit> FetchLatestCommitAsync() {
            var url = string.Format(CultureInfo.InvariantCulture, "https://api.github.com/repos/{0}/{1}/commits/{2}", _config.RepoOwner, _config.RepoName, _config.Branch);
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var raw = _serializer.Deserialize<Dictionary<string, object>>(json);
            if (raw == null) return null;

            var commit = new GitHubCommit();
            commit.Sha = ReadString(raw, "sha");
            return commit;
        }

        private string BuildCommitDownloadUrl(string sha) {
            return string.Format(CultureInfo.InvariantCulture, "https://codeload.github.com/{0}/{1}/zip/{2}", _config.RepoOwner, _config.RepoName, sha);
        }

        private GitHubRelease CreateRelease(Dictionary<string, object> raw) {
            var release = new GitHubRelease();
            release.Name = ReadString(raw, "name");
            release.TagName = ReadString(raw, "tag_name");
            release.PublishedAt = ReadString(raw, "published_at");
            release.Prerelease = ReadBool(raw, "prerelease");
            release.Draft = ReadBool(raw, "draft");
            release.Assets = new List<GitHubAsset>();

            object assetsObj;
            if (raw.TryGetValue("assets", out assetsObj) && assetsObj is object[]) {
                var array = (object[])assetsObj;
                foreach (var item in array) {
                    var dict = item as Dictionary<string, object>;
                    if (dict == null) continue;
                    var asset = new GitHubAsset();
                    asset.Name = ReadString(dict, "name");
                    asset.BrowserDownloadUrl = ReadString(dict, "browser_download_url");
                    release.Assets.Add(asset);
                }
            }

            return release;
        }

        private string ReadString(Dictionary<string, object> raw, string key) {
            object value;
            if (!raw.TryGetValue(key, out value) || value == null) {
                return string.Empty;
            }

            var text = value as string;
            if (text != null) return text;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private bool ReadBool(Dictionary<string, object> raw, string key) {
            object value;
            if (!raw.TryGetValue(key, out value) || value == null) {
                return false;
            }

            if (value is bool) {
                return (bool)value;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return false;

            bool parsed;
            return bool.TryParse(text, out parsed) && parsed;
        }

        private string ReadInstalledVersion() {
            var versionFile = Path.Combine(_config.InstallDir ?? string.Empty, VersionFileName);
            if (File.Exists(versionFile)) {
                try {
                    return File.ReadAllText(versionFile).Trim();
                }
                catch {
                    return string.Empty;
                }
            }

            var exePath = GetExecutablePath();
            if (File.Exists(exePath)) {
                try {
                    var fvi = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrEmpty(fvi.ProductVersion)) return fvi.ProductVersion;
                    if (!string.IsNullOrEmpty(fvi.FileVersion)) return fvi.FileVersion;
                }
                catch {
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        private void WriteInstalledVersion(string version) {
            var versionFile = Path.Combine(_config.InstallDir ?? string.Empty, VersionFileName);
            try {
                File.WriteAllText(versionFile, version ?? string.Empty, Encoding.UTF8);
            }
            catch {
                // ignore persistence failure
            }
        }

        private string GetExecutablePath() {
            return Path.Combine(_config.InstallDir ?? string.Empty, _config.ExecutableName ?? string.Empty);
        }

        private void EnsureNotDisposed() {
            if (_disposed) {
                throw new ObjectDisposedException(nameof(Updater));
            }
        }

        private string ShortenSha(string sha) {
            if (string.IsNullOrEmpty(sha)) return string.Empty;
            return sha.Length <= 7 ? sha : sha.Substring(0, 7);
        }

        private bool AreVersionsEqual(string left, string right) {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) {
                return false;
            }

            return string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeVersion(string value) {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var trimmed = value.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) {
                return trimmed.Substring(1);
            }
            return trimmed;
        }

        private class GitHubRelease {
            public string Name { get; set; }
            public string TagName { get; set; }
            public string PublishedAt { get; set; }
            public bool Prerelease { get; set; }
            public bool Draft { get; set; }
            public List<GitHubAsset> Assets { get; set; }
        }

        private class GitHubAsset {
            public string Name { get; set; }
            public string BrowserDownloadUrl { get; set; }
        }

        private class GitHubCommit {
            public string Sha { get; set; }
        }
    }

    public class UpdateCheckResult {
        public UpdateCheckResult() {
            LocalDisplay = string.Empty;
            RemoteDisplay = string.Empty;
        }

        public bool HasUpdate { get; internal set; }
        public string LocalDisplay { get; internal set; }
        public string RemoteDisplay { get; internal set; }
        internal string LocalVersion { get; set; }
        internal string RemoteVersion { get; set; }
        internal string DownloadUrl { get; set; }
        internal string AssetFileName { get; set; }
        internal string Sha256DownloadUrl { get; set; }
        internal string Sha256Value { get; set; }
        internal string Mode { get; set; }
    }
}
