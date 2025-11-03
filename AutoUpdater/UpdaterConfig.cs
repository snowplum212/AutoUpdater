using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace AutoUpdater {
    public class UpdaterConfig {
        public UpdaterConfig() {
            RepoOwner = string.Empty;
            RepoName = string.Empty;
            AssetNamePattern = string.Empty;
            InstallDir = string.Empty;
            ExecutableName = string.Empty;
            BackupDir = string.Empty;
            GitHubTokenEnv = string.Empty;
            UseSha256 = true;
            CheckMode = "release";
            Branch = "main";
        }

        public string RepoOwner { get; set; }
        public string RepoName { get; set; }
        public string AssetNamePattern { get; set; }
        public string InstallDir { get; set; }
        public string ExecutableName { get; set; }
        public string BackupDir { get; set; }
        public string GitHubTokenEnv { get; set; }
        public bool UseSha256 { get; set; }

        // release | commit
        public string CheckMode { get; set; }
        public string Branch { get; set; }

        public static UpdaterConfig Load(string path) {
            if (!File.Exists(path)) {
                throw new FileNotFoundException("���� ������ ������ �ʽ��ϴ�.", path);
            }

            var json = File.ReadAllText(path);
            var serializer = new JavaScriptSerializer();
            var raw = serializer.Deserialize<Dictionary<string, object>>(json);
            if (raw == null) {
                throw new InvalidOperationException("���� �Ľ� ����");
            }

            var cfg = new UpdaterConfig();
            cfg.RepoOwner = ReadString(raw, "repoOwner", cfg.RepoOwner);
            cfg.RepoName = ReadString(raw, "repoName", cfg.RepoName);
            cfg.AssetNamePattern = ReadString(raw, "assetNamePattern", cfg.AssetNamePattern);
            cfg.InstallDir = ReadString(raw, "installDir", cfg.InstallDir);
            cfg.ExecutableName = ReadString(raw, "executableName", cfg.ExecutableName);
            cfg.BackupDir = ReadString(raw, "backupDir", cfg.BackupDir);
            cfg.GitHubTokenEnv = ReadString(raw, "gitHubTokenEnv", cfg.GitHubTokenEnv);
            cfg.CheckMode = ReadString(raw, "checkMode", cfg.CheckMode);
            cfg.Branch = ReadString(raw, "branch", cfg.Branch);
            cfg.UseSha256 = ReadBool(raw, "useSha256", cfg.UseSha256);

            cfg.Normalize();
            return cfg;
        }

        private void Normalize() {
            RepoOwner = RepoOwner ?? string.Empty;
            RepoName = RepoName ?? string.Empty;
            AssetNamePattern = AssetNamePattern ?? string.Empty;
            InstallDir = InstallDir ?? string.Empty;
            ExecutableName = ExecutableName ?? string.Empty;
            BackupDir = BackupDir ?? string.Empty;
            GitHubTokenEnv = GitHubTokenEnv ?? string.Empty;
            CheckMode = string.IsNullOrWhiteSpace(CheckMode) ? "release" : CheckMode.Trim().ToLowerInvariant();
            Branch = string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();
        }

        private static string ReadString(Dictionary<string, object> raw, string key, string fallback) {
            if (!raw.ContainsKey(key) || raw[key] == null) return fallback;
            var value = raw[key];
            if (value is string) return (string)value;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }

        private static bool ReadBool(Dictionary<string, object> raw, string key, bool fallback) {
            if (!raw.ContainsKey(key) || raw[key] == null) return fallback;
            var value = raw[key];
            if (value is bool) return (bool)value;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return fallback;
            bool parsed;
            if (bool.TryParse(text, out parsed)) return parsed;
            return fallback;
        }
    }
}
