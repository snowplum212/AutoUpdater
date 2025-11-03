using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoUpdater {
    public partial class FormMain : Form {
        private UpdaterConfig _cfg = null;
        private Updater _updater = null;

        public FormMain() {
            InitializeComponent();
            Text = "AutoUpdater (GitHub)";
            Width = 520; Height = 160;

            var lbl = new Label { Name = "lblStatus", AutoSize = true, Top = 20, Left = 20, Text = "초기화 중..." };
            var btn = new Button { Name = "btnCheck", Text = "수동 업데이트 확인", Top = 60, Left = 20, Width = 160 };
            btn.Click += async (_, __) => await CheckAndMaybeUpdateAsync(interactive: true);
            Controls.Add(lbl);
            Controls.Add(btn);

            // 폴더 준비
            Directory.CreateDirectory(AppContext.BaseDirectory);
        }

        protected override async void OnLoad(EventArgs e) {
            base.OnLoad(e);
            try {
                var cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                _cfg = UpdaterConfig.Load(cfgPath);
                _updater = new Updater(_cfg, s => SetStatus(s));

                // 시작 시 자동 확인
                await CheckAndMaybeUpdateAsync(interactive: true);
            }
            catch (Exception ex) {
                SetStatus("오류: " + ex.Message);
            }
        }

        private async Task CheckAndMaybeUpdateAsync(bool interactive) {
            try {
                SetBusy(true);
                SetStatus("원격 버전 확인 중...");

                var check = await _updater.CheckRemoteAsync();
                if (!check.HasUpdate) {
                    SetStatus("이미 최신입니다.");
                    return;
                }

                // 사용자의 요구: 최신이 있으면 MessageBox로 묻기
                if (interactive) {
                    var msg = $"새 버전 제공됨: {check.RemoteDisplay} (현재: {check.LocalDisplay})\n업데이트 하시겠습니까?";
                    var dr = MessageBox.Show(this, msg, "업데이트 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) {
                        SetStatus("사용자 취소");
                        return;
                    }
                }

                SetStatus("업데이트 다운로드 및 교체 중...");
                await _updater.UpdateAsync(check);
                SetStatus("업데이트 완료");

                // 필요 시 대상 앱 자동 실행
                _updater.TryStartTargetApp();

                // (선택) 이 프로그램은 닫기
                // Close();
            }
            catch (Exception ex) {
                SetStatus("업데이트 실패: " + ex.Message);
            }
            finally {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy) {
            if (Controls["btnCheck"] is Button b) b.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private void SetStatus(string s) {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
            if (Controls["lblStatus"] is Label l) l.Text = s;
        }
    }
}
