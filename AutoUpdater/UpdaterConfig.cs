using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoUpdater {
    public class UpdaterConfig {
        public string RepoOwner { get; set; } = "";
        public string RepoName { get; set; } = "";
        public string AssetNamePattern { get; set; } = "";
        public string InstallDir { get; set; } = "";
        public string ExecutableName { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public string? GitHubTokenEnv { get; set; }
        public bool UseSha256 { get; set; } = true;

        // release | commit
        public string CheckMode { get; set; } = "release";
        public string Branch { get; set; } = "main";

        public static UpdaterConfig Load(string path) {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdaterConfig>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            }) ?? throw new("설정 파싱 실패");
        }
    }
}
