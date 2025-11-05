using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Updater {
    public partial class FormUpdater : Form {
        private readonly RepositorySettings _settings;
        private readonly ParsedArguments _arguments;

        public int ExitCode { get; private set; } = 1;

        public FormUpdater(RepositorySettings settings, ParsedArguments arguments) {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _arguments = arguments ?? new ParsedArguments();

            InitializeComponent();

            Shown += FormUpdater_Shown;
        }

        private async void FormUpdater_Shown(object sender, EventArgs e) {
            Shown -= FormUpdater_Shown;
            await RunUpdateAsync();
        }

        private async Task RunUpdateAsync() {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.MarqueeAnimationSpeed = 0;
            UpdateProgress(0);
            AppendLog("Updater started.");

            try {
                UpdateStatus("Resolving repository location...");
                UpdateProgress(10);
                var targetDirectory = await Task.Run(() => Program.ResolveTargetDirectory(_arguments, _settings));
                UpdateProgress(30);
                AppendLog($"Using repository: {targetDirectory}");

                UpdateStatus("Ensuring repository is initialized...");
                UpdateProgress(45);
                await Task.Run(() => Program.EnsureRepositoryInitialized(targetDirectory, _settings.RepositoryUrl, AppendLog));
                UpdateProgress(65);

                UpdateStatus("Pulling latest changes...");
                UpdateProgress(75);
                var mergeStatus = await Task.Run(() => Program.UpdateRepository(targetDirectory, AppendLog));
                UpdateProgress(90);
                AppendLog($"Merge status: {mergeStatus}");

                UpdateStatus("Update completed successfully.");
                UpdateProgress(100);
                ExitCode = 0;
            }
            catch (Exception ex) {
                ExitCode = 1;
                UpdateStatus("Update failed.");
                AppendLog(ex.Message);
                MessageBox.Show(
                    this,
                    ex.ToString(),
                    "Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally {
                progressBar.Style = ProgressBarStyle.Blocks;
                progressBar.MarqueeAnimationSpeed = 0;
                progressBar.Value = progressBar.Maximum;
                if (ExitCode == 0) {
                    MessageBox.Show(
                        this,
                        "The application has been updated successfully.",
                        "Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    Close();
                }
            }
        }

        private void UpdateProgress(int percent) {
            if (InvokeRequired) {
                BeginInvoke(new Action<int>(UpdateProgress), percent);
                return;
            }

            var boundedValue = Math.Max(progressBar.Minimum, Math.Min(progressBar.Maximum, percent));
            progressBar.Value = boundedValue;
        }

        private void UpdateStatus(string message) {
            if (InvokeRequired) {
                BeginInvoke(new Action<string>(UpdateStatus), message);
                return;
            }

            labelStatus.Text = message;
        }

        private void AppendLog(string message) {
            if (InvokeRequired) {
                BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            lbStatus.Text = $"[{timestamp}] {message}";
        }
    }
}
