using System;
using System.Configuration;
using System.IO;
using System.Windows.Forms;
using LibGit2Sharp;

namespace Updater {
    internal static class Program {
        [STAThread]
        static int Main(string[] args) {
            var parsedArguments = ParseArguments(args);
            RepositorySettings settings;

            try {
                settings = LoadSettings();
            }
            catch (Exception ex) {
                ShowFatalError(ex);
                return 1;
            }

            try {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new FormUpdater(settings, parsedArguments)) {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            catch (Exception ex) {
                ShowFatalError(ex);
                return 1;
            }
        }

        private static void ShowFatalError(Exception ex) {
            MessageBox.Show(
                ex.ToString(),
                "Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        internal static RepositorySettings LoadSettings() {
            var repositoryUrl = ConfigurationManager.AppSettings["RepositoryUrl"] ?? string.Empty;
            var localPathSetting = ConfigurationManager.AppSettings["LocalRepositoryPath"] ?? string.Empty;
            var expandedLocalPath = string.IsNullOrWhiteSpace(localPathSetting)
                ? string.Empty
                : Environment.ExpandEnvironmentVariables(localPathSetting);

            return new RepositorySettings(repositoryUrl.Trim(), expandedLocalPath.Trim());
        }

        private static ParsedArguments ParseArguments(string[] args) {
            var parsedArguments = new ParsedArguments();

            if (args == null) {
                return parsedArguments;
            }

            foreach (var rawArgument in args) {
                if (string.IsNullOrWhiteSpace(rawArgument)) {
                    continue;
                }

                var argument = rawArgument.Trim();
                if (IsKeepConsoleSwitch(argument)) {
                    parsedArguments.KeepConsoleOpen = true;
                    continue;
                }

                if (parsedArguments.ExplicitPath == null && LooksLikePath(argument)) {
                    parsedArguments.ExplicitPath = argument;
                }
            }

            return parsedArguments;
        }

        private static bool LooksLikePath(string argument) {
            if (string.IsNullOrWhiteSpace(argument)) {
                return false;
            }

            var firstCharacter = argument[0];
            if (firstCharacter == '/' || firstCharacter == '-') {
                return false;
            }

            return true;
        }

        private static bool IsKeepConsoleSwitch(string argument) {
            return string.Equals(argument, "/k", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-k", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--keep", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--keep-open", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ResolveTargetDirectory(ParsedArguments parsedArguments, RepositorySettings settings) {
            if (!string.IsNullOrWhiteSpace(parsedArguments.ExplicitPath)) {
                var providedPath = Path.GetFullPath(parsedArguments.ExplicitPath);
                if (Directory.Exists(providedPath)) {
                    if (!IsGitRepository(providedPath)) {
                        throw new InvalidOperationException($"Provided path is not a git repository: {providedPath}");
                    }

                    return providedPath;
                }

                if (!string.IsNullOrWhiteSpace(settings.RepositoryUrl)) {
                    return providedPath;
                }

                throw new DirectoryNotFoundException($"Provided path not found: {providedPath}");
            }

            if (!string.IsNullOrWhiteSpace(settings.LocalRepositoryPath)) {
                return Path.GetFullPath(settings.LocalRepositoryPath);
            }

            var currentDirectory = Environment.CurrentDirectory;
            var repositoryRoot = FindGitRepositoryRoot(currentDirectory);
            if (repositoryRoot != null) {
                return repositoryRoot;
            }

            var assemblyLocation = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(assemblyLocation)) {
                repositoryRoot = FindGitRepositoryRoot(assemblyLocation);
                if (repositoryRoot != null) {
                    return repositoryRoot;
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.RepositoryUrl) && !string.IsNullOrWhiteSpace(settings.LocalRepositoryPath)) {
                return Path.GetFullPath(settings.LocalRepositoryPath);
            }

            throw new InvalidOperationException("Could not locate a git repository. Provide a path or configure RepositoryUrl and LocalRepositoryPath.");
        }

        internal static void EnsureRepositoryInitialized(string repositoryPath, string repositoryUrl, Action<string> log = null) {
            if (Directory.Exists(repositoryPath)) {
                if (!Repository.IsValid(repositoryPath)) {
                    throw new InvalidOperationException($"Target directory exists but is not a git repository: {repositoryPath}");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(repositoryUrl)) {
                throw new InvalidOperationException("Repository does not exist locally and RepositoryUrl is not configured.");
            }

            var fullPath = Path.GetFullPath(repositoryPath);
            var parentDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parentDirectory)) {
                throw new InvalidOperationException($"Cannot determine parent directory for clone target: {repositoryPath}");
            }

            Directory.CreateDirectory(parentDirectory);
            log?.Invoke($"Cloning repository from {repositoryUrl}...");

            var cloneOptions = new CloneOptions();
            Repository.Clone(repositoryUrl, fullPath, cloneOptions);
        }

        internal static string FindGitRepositoryRoot(string startDirectory) {
            var directoryInfo = new DirectoryInfo(Path.GetFullPath(startDirectory));
            while (directoryInfo != null) {
                var gitFolder = Path.Combine(directoryInfo.FullName, ".git");
                if (Directory.Exists(gitFolder)) {
                    return directoryInfo.FullName;
                }

                directoryInfo = directoryInfo.Parent;
            }

            return null;
        }

        internal static bool IsGitRepository(string directory) {
            return Repository.IsValid(directory);
        }

        internal static MergeStatus UpdateRepository(string repositoryPath, Action<string> log = null) {
            Repository repository = null;
            try {
                repository = new Repository(repositoryPath);

                var signature = CreateSignature();
                var pullOptions = new PullOptions {
                    FetchOptions = new FetchOptions(),
                    MergeOptions = new MergeOptions {
                        FailOnConflict = true
                    }
                };

                var result = Commands.Pull(repository, signature, pullOptions);
                log?.Invoke($"Pull result: {result.Status}");

                if (result.Status == MergeStatus.Conflicts) {
                    throw new InvalidOperationException("Pull resulted in merge conflicts. Manual intervention required.");
                }

                return result.Status;
            }
            finally {
                repository?.Dispose();
            }
        }

        internal static Signature CreateSignature() {
            var name = ConfigurationManager.AppSettings["CommitSignatureName"];
            if (string.IsNullOrWhiteSpace(name)) {
                name = "Updater";
            }

            var email = ConfigurationManager.AppSettings["CommitSignatureEmail"];
            if (string.IsNullOrWhiteSpace(email)) {
                email = "updater@localhost";
            }

            return new Signature(name, email, DateTimeOffset.Now);
        }
    }

    public sealed class ParsedArguments {
        public string ExplicitPath { get; set; }
        public bool KeepConsoleOpen { get; set; }
    }

    public sealed class RepositorySettings {
        public RepositorySettings(string repositoryUrl, string localRepositoryPath) {
            RepositoryUrl = repositoryUrl;
            LocalRepositoryPath = localRepositoryPath;
        }

        public string RepositoryUrl { get; }
        public string LocalRepositoryPath { get; }
    }
}
