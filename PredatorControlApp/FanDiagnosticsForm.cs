using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    internal sealed class FanDiagnosticsForm : Form
    {
        private static readonly Color FormBg = Color.FromArgb(22, 22, 26);
        private static readonly Color TitleBarBg = Color.FromArgb(18, 18, 21);
        private static readonly Color SeparatorColor = Color.FromArgb(40, 40, 44);
        private static readonly Color TitleTextColor = Color.FromArgb(200, 200, 205);
        private static readonly Color BodyTextColor = Color.FromArgb(160, 160, 168);
        private static readonly Color HeaderColor = Color.FromArgb(120, 120, 135);
        private static readonly Color AccentColor = Color.FromArgb(0, 200, 160);
        private static readonly Color CloseHoverColor = Color.FromArgb(220, 50, 50);

        private static readonly Font FontTitle = new("Segoe UI", 10f, FontStyle.Bold);
        private static readonly Font FontHeader = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontBody = new("Segoe UI", 9f, FontStyle.Regular);

        private const int SidePad = 16;
        private const int TitleBarHeight = 36;
        private const int FormWidth = 440;

        private readonly IFanTestHost _host;
        private FanTestRunner? _runner;
        private PredatorSwitch _switchLog = null!;
        private PredatorButton _btnRun = null!;
        private Label _lblStatus = null!;
        private Label _lblResult = null!;

        public event EventHandler<bool>? LoggingToggled;

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        public FanDiagnosticsForm(IFanTestHost host, bool loggingEnabled)
        {
            _host = host;
            Text = "Fan Diagnostics";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = FormBg;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUI(loggingEnabled);
        }

        private void BuildUI(bool loggingEnabled)
        {
            int contentW = FormWidth - SidePad * 2;

            var pnlTitle = new Panel { Height = TitleBarHeight, Dock = DockStyle.Top, BackColor = TitleBarBg };
            pnlTitle.MouseDown += TitleBar_MouseDown;
            Controls.Add(pnlTitle);

            var lblTitle = new Label
            {
                Text = "Fan Diagnostics",
                Font = FontTitle,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(SidePad, (TitleBarHeight - FontTitle.Height) / 2),
                BackColor = Color.Transparent
            };
            lblTitle.MouseDown += TitleBar_MouseDown;
            pnlTitle.Controls.Add(lblTitle);

            var lblClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(140, 140, 145),
                AutoSize = true,
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            lblClose.Location = new Point(FormWidth - SidePad - lblClose.PreferredWidth - 2,
                                          (TitleBarHeight - lblClose.PreferredHeight) / 2);
            lblClose.Click += (s, e) => Close();
            lblClose.MouseEnter += (s, e) => lblClose.ForeColor = CloseHoverColor;
            lblClose.MouseLeave += (s, e) => lblClose.ForeColor = Color.FromArgb(140, 140, 145);
            pnlTitle.Controls.Add(lblClose);

            int y = TitleBarHeight + 14;

            y = AddText("Records temperatures, CPU load, what the fan curve wanted, every fan command sent, " +
                        "and fan RPM. Logs are kept for 14 days.", y, contentW) + 12;

            var lblLog = new Label
            {
                Text = "Log fan data continuously",
                Font = FontBody,
                ForeColor = TitleTextColor,
                AutoSize = true,
                Location = new Point(SidePad, y + 6)
            };
            Controls.Add(lblLog);
            _switchLog = new PredatorSwitch
            {
                Location = new Point(FormWidth - SidePad - 48, y),
                Size = new Size(48, 26),
                Checked = loggingEnabled
            };
            _switchLog.CheckedChanged += (s, e) => LoggingToggled?.Invoke(this, _switchLog.Checked);
            Controls.Add(_switchLog);
            y += 40;

            y = AddSeparator(y) + 14;

            y = AddHeader("LOAD TEST (ABOUT 10 MINUTES)", y) + 6;
            y = AddText("Plug in the charger, close other apps and leave the laptop alone while it runs. " +
                        "The CPU is fully loaded for short periods, so the fans will get loud. " +
                        "Uses your current fan curve settings.", y, contentW) + 12;

            _btnRun = new PredatorButton { Text = "Run Test", Size = new Size(contentW, 36), Location = new Point(SidePad, y) };
            _btnRun.Click += BtnRun_Click;
            Controls.Add(_btnRun);
            y += 36 + 10;

            _lblStatus = new Label
            {
                Text = "",
                Font = FontBody,
                ForeColor = AccentColor,
                AutoSize = false,
                Size = new Size(contentW, 38),
                Location = new Point(SidePad, y)
            };
            Controls.Add(_lblStatus);
            y += 38 + 8;

            y = AddSeparator(y) + 14;

            int halfW = (contentW - 10) / 2;
            var btnSave = new PredatorButton { Text = "Save Report", Size = new Size(halfW, 34), Location = new Point(SidePad, y) };
            btnSave.Click += BtnSave_Click;
            Controls.Add(btnSave);

            var btnFolder = new PredatorButton { Text = "Open Log Folder", Size = new Size(halfW, 34), Location = new Point(SidePad + halfW + 10, y) };
            btnFolder.Click += (s, e) =>
            {
                Directory.CreateDirectory(FanLog.Folder);
                Process.Start("explorer.exe", FanLog.Folder);
            };
            Controls.Add(btnFolder);
            y += 34 + 10;

            _lblResult = new Label
            {
                Text = "Save Report zips the logs without running the test.",
                Font = FontBody,
                ForeColor = BodyTextColor,
                AutoSize = false,
                Size = new Size(contentW, 54),
                Location = new Point(SidePad, y)
            };
            Controls.Add(_lblResult);
            y += 54 + 6;

            ClientSize = new Size(FormWidth, y);
        }

        private int AddText(string text, int y, int width)
        {
            var lbl = new Label
            {
                Text = text,
                Font = FontBody,
                ForeColor = BodyTextColor,
                AutoSize = false,
                MaximumSize = new Size(width, 0),
                Location = new Point(SidePad, y)
            };
            lbl.Size = new Size(width, lbl.GetPreferredSize(new Size(width, 0)).Height);
            Controls.Add(lbl);
            return y + lbl.Height;
        }

        private int AddHeader(string text, int y)
        {
            var lbl = new Label { Text = text, Font = FontHeader, ForeColor = HeaderColor, AutoSize = true, Location = new Point(SidePad, y) };
            Controls.Add(lbl);
            return y + lbl.PreferredHeight;
        }

        private int AddSeparator(int y)
        {
            Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(FormWidth, 1), BackColor = SeparatorColor });
            return y + 1;
        }

        private async void BtnRun_Click(object? sender, EventArgs e)
        {
            if (_runner != null)
            {
                _runner.Cancel();
                _btnRun.Text = "Stopping…";
                return;
            }

            if (!_host.CurveActive)
            {
                MessageBox.Show(this,
                    "The test measures your fan curve, so it needs to be active.\n\n" +
                    "Set Fan Mode to Custom, click Curve, then click Apply in the curve editor.",
                    "Fan Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var runner = new FanTestRunner(_host);
            int minutes = (int)Math.Ceiling(runner.TotalSec / 60.0);
            if (MessageBox.Show(this,
                    $"The test takes about {minutes} minutes and will fully load the CPU at times.\n\n" +
                    "Plug in the charger and don't use the laptop until it finishes. You can stop it at any time.",
                    "Fan Diagnostics", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                runner.Dispose();
                return;
            }

            _runner = runner;
            _runner.StatusChanged += text => { if (!IsDisposed) _lblStatus.Text = text; };
            _btnRun.Text = "Stop Test";
            _lblStatus.Text = "Starting…";

            try
            {
                string zip = await _runner.RunAsync();
                if (IsDisposed) return;
                _lblStatus.Text = "Test finished.";
                ShowReport(zip);
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
                if (!IsDisposed) _lblStatus.Text = "Test failed. See the error message.";
            }
            finally
            {
                _runner?.Dispose();
                _runner = null;
                if (!IsDisposed) _btnRun.Text = "Run Test";
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("PREDATOR CONTROL: FAN LOG REPORT (no load test)");
                sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine();
                sb.AppendLine(FanLog.SystemInfoText());
                sb.AppendLine();
                sb.AppendLine("Current fan settings:");
                sb.AppendLine(_host.DescribeFanSettings());
                ShowReport(FanLog.CreateReport(sb.ToString(), null));
            }
            catch (Exception ex)
            {
                Program.Report(ex, false);
            }
        }

        private void ShowReport(string zipPath)
        {
            _lblResult.ForeColor = AccentColor;
            _lblResult.Text = $"Report saved: {Path.GetFileName(zipPath)}\nSend this file. Its folder has been opened.";
            try { Process.Start("explorer.exe", $"/select,\"{zipPath}\""); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_runner != null)
            {
                if (MessageBox.Show(this, "Stop the running test?", "Fan Diagnostics",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _runner.Cancel();
            }
            base.OnFormClosing(e);
        }

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 0x2, 0);
        }
    }
}
