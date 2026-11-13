using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SshTunnelManager.Core;

namespace SshTunnelManager
{
    public class MainForm : Form
    {
        private readonly List<SessionProfile> _sessions = new List<SessionProfile>();
        private TunnelEngine _engine;
        private SessionProfile _current;
        private bool _loading;

        // Trái: danh sách phiên
        private ListBox _list;
        private Button _btnNew, _btnSave, _btnDelete, _btnDuplicate;

        // Phải: form nhập
        private TableLayoutPanel _form;
        private TextBox _txtName, _txtHost, _txtUser, _txtPass, _txtBind, _txtInsideHost, _txtKey;
        private NumericUpDown _numSshPort, _numOutsidePort, _numInsidePort, _numKeepAlive;
        private CheckBox _chkRemember, _chkShowPass, _chkAutoReconnect;
        private RadioButton _rbLocal, _rbRemote;
        private Label _lblEquivalent;

        // Trạng thái + log
        private Button _btnConnect, _btnDisconnect;
        private Label _lblState, _lblUptime;
        private TextBox _txtLog;
        private Timer _uiTimer;
        private SplitContainer _split;
        private TextBox _txtFilter;

        private readonly CommandLineOptions _options;

        public MainForm() : this(new CommandLineOptions())
        {
        }

        public MainForm(CommandLineOptions options)
        {
            _options = options ?? new CommandLineOptions();

            Text = "SSH Tunnel Manager - Pooh";
            MinimumSize = new Size(860, 600);
            Size = new Size(1080, 760);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildUi();
            if (!_options.HasAnything) RestoreUiState();
            LoadSessions();
            if (_options.HasAnything) ApplyOptions();
        }

        private void BuildUi()
        {
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                SplitterWidth = 6
            };

            BuildLeftPanel(_split.Panel1);
            BuildRightPanel(_split.Panel2);

            var status = new StatusStrip();
            status.Items.Add(new ToolStripStatusLabel("Sẵn sàng"));
            Controls.Add(_split);
            Controls.Add(status);

            // Đặt vị trí chia sau khi form có kích thước thật, để panel phải đủ rộng.
            Load += (s, e) =>
            {
                try
                {
                    // PanelMinSize chỉ đặt được khi control đã đủ lớn, nếu đặt trong constructor sẽ ném exception.
                    _split.Panel1MinSize = 180;
                    _split.Panel2MinSize = 420;
                }
                catch { }

                try
                {
                    int target = Math.Max(_split.Panel1MinSize, Math.Min(290, Width / 3));
                    // panel phải phải đủ rộng cho 2 cột nhãn + giá trị
                    int maxAllowed = ClientSize.Width - _split.Panel2MinSize - _split.SplitterWidth;
                    if (target > maxAllowed) target = Math.Max(_split.Panel1MinSize, maxAllowed);
                    _split.SplitterDistance = target;
                }
                catch { }


            };

            Shown += (s2, e2) => DumpLayout();

            _uiTimer = new Timer { Interval = 1000 };
            _uiTimer.Tick += (s, e) =>
            {
                if (_engine != null && _engine.State == TunnelState.Connected)
                {
                    var t = _engine.Uptime;
                    _lblUptime.Text = string.Format("{0:hh\\:mm\\:ss}", t);
                }
            };
            _uiTimer.Start();
        }

        // ================= PANEL TRÁI =================
        private void BuildLeftPanel(SplitterPanel panel)
        {
            var header = new Label
            {
                Text = "Phiên đã lưu",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var filterRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            _txtFilter = new TextBox { Dock = DockStyle.Fill };
            _txtFilter.TextChanged += (s, e) => RefreshList();
            filterRow.Controls.Add(_txtFilter);

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                ColumnCount = 4,
                RowCount = 1,
                Padding = new Padding(0, 2, 0, 2)
            };
            for (int i = 0; i < 4; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            _btnNew = MakeButton("Tạo mới", OnNewSession);
            _btnSave = MakeButton("Lưu", OnSaveSession);
            _btnDuplicate = MakeButton("Nhân bản", OnDuplicateSession);
            _btnDelete = MakeButton("Xoá", OnDeleteSession);

            foreach (var b in new[] { _btnNew, _btnSave, _btnDuplicate, _btnDelete })
            {
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(2, 0, 2, 0);
                buttons.Controls.Add(b);
            }

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                DisplayMember = "Name"
            };
            _list.SelectedIndexChanged += OnSessionSelected;
            _list.DoubleClick += (s, e) => { if (_current != null) StartTunnel(); };

            var hint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Text = "Nhấp đúp để kết nối",
                ForeColor = Color.Gray
            };

            panel.Controls.Add(_list);
            panel.Controls.Add(hint);
            panel.Controls.Add(buttons);
            panel.Controls.Add(filterRow);
            panel.Controls.Add(header);
            panel.Padding = new Padding(8);
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text };
            b.Click += onClick;
            return b;
        }

        // ================= PANEL PHẢI =================
        private void BuildRightPanel(SplitterPanel panel)
        {
            // Dùng TableLayoutPanel 2 cột: nhãn | giá trị, tự co giãn theo bề rộng cửa sổ.
            _form = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(10, 6, 10, 6)
            };
            _form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            _form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            AddLabelRow("Tên phiên");
            _txtName = AddTextRow(320);

            _rbLocal = new RadioButton
            {
                Text = "Local (-L) · mở cổng trên máy này, chuyển vào server",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(3, 4, 3, 1)
            };
            _rbLocal.CheckedChanged += (s, e) => UpdateEquivalent();
            int rowRadio1 = NextRow();
            _form.Controls.Add(_rbLocal, 0, rowRadio1);
            _form.SetColumnSpan(_rbLocal, 2);

            _rbRemote = new RadioButton
            {
                Text = "Remote (-R) · mở cổng trên server, chuyển về máy này",
                AutoSize = true,
                Margin = new Padding(3, 1, 3, 4)
            };
            _rbRemote.CheckedChanged += (s, e) => UpdateEquivalent();
            int rowRadio2 = NextRow();
            _form.Controls.Add(_rbRemote, 0, rowRadio2);
            _form.SetColumnSpan(_rbRemote, 2);

            AddLabelRow("Địa chỉ bên ngoài");
            _txtHost = AddTextRow(320);

            AddLabelRow("Port SSH");
            _numSshPort = AddNumberRow(22, 1, 65535, 90);

            AddLabelRow("Tài khoản");
            _txtUser = AddTextRow(320);

            AddLabelRow("Mật khẩu");
            _txtPass = AddTextRow(320, true);

            var passOptions = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _chkRemember = new CheckBox
            {
                Text = "Nhớ mật khẩu",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 4, 14, 0)
            };
            _chkShowPass = new CheckBox
            {
                Text = "Hiện mật khẩu",
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            _chkShowPass.CheckedChanged += (s, e) => _txtPass.UseSystemPasswordChar = !_chkShowPass.Checked;

            passOptions.Controls.Add(_chkRemember);
            passOptions.Controls.Add(_chkShowPass);
            int rowPass = NextRow();
            _form.Controls.Add(new Label(), 0, rowPass);
            _form.Controls.Add(passOptions, 1, rowPass);

            AddLabelRow("Địa chỉ bên trong");
            _txtInsideHost = AddTextRow(320);

            AddLabelRow("Nat inside (cổng trong)");
            _numInsidePort = AddNumberRow(80, 1, 65535, 90);

            AddLabelRow("Outside port");
            _numOutsidePort = AddNumberRow(8080, 0, 65535, 90);
            _numOutsidePort.ValueChanged += (s, e) => UpdateEquivalent();

            AddLabelRow("Bind ngoài");
            _txtBind = AddTextRow(320);

            var outerHint = new Label
            {
                Text = "127.0.0.1 = chỉ máy này · 0.0.0.0 = mọi máy trong LAN",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(0, 0, 0, 6)
            };
            int rowHint = NextRow();
            _form.Controls.Add(new Label(), 0, rowHint);
            _form.Controls.Add(outerHint, 1, rowHint);

            AddLabelRow("Keepalive (giây)");
            _numKeepAlive = AddNumberRow(30, 5, 600, 90);

            AddLabelRow("Tự kết nối lại");
            _chkAutoReconnect = new CheckBox
            {
                Text = "Tự kết nối lại khi rớt mạng",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 4, 0, 4)
            };
            int rowReconnect = NextRow();
            _form.Controls.Add(new Label(), 0, rowReconnect);
            _form.Controls.Add(_chkAutoReconnect, 1, rowReconnect);

            AddLabelRow("Khoá riêng (tuỳ chọn)");
            var keyRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 2, 0, 2)
            };
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38F));

            _txtKey = new TextBox { Dock = DockStyle.Fill, MaxLength = 320 };
            var btnBrowse = new Button { Text = "...", Dock = DockStyle.Fill, Margin = new Padding(4, 0, 0, 0) };
            btnBrowse.Click += OnBrowseKey;

            keyRow.Controls.Add(_txtKey, 0, 0);
            keyRow.Controls.Add(btnBrowse, 1, 0);
            int rowKey = NextRow();
            _form.Controls.Add(new Label(), 0, rowKey);
            _form.Controls.Add(keyRow, 1, rowKey);

            // Lệnh tương đương
            _lblEquivalent = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 34,
                Font = new Font("Consolas", 8.5F),
                ForeColor = Color.FromArgb(0, 90, 120),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 0, 4)
            };
            int rowEquiv = NextRow();
            _form.Controls.Add(new Label
            {
                Text = "Lệnh tương đương",
                AutoSize = true,
                Margin = new Padding(3, 10, 3, 0)
            }, 0, rowEquiv);
            _form.Controls.Add(_lblEquivalent, 1, rowEquiv);

            // Hàng nút kết nối - dùng FlowLayout để không bị cắt khi cửa sổ hẹp
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 6, 0, 6)
            };
            _btnConnect = new Button { Text = "Kết nối", Size = new Size(120, 32) };
            _btnConnect.Click += (s, e) => StartTunnel();

            _btnDisconnect = new Button { Text = "Ngắt", Size = new Size(80, 32), Enabled = false };
            _btnDisconnect.Click += (s, e) => StopTunnel();

            _lblState = new Label { Text = "Chưa kết nối", AutoSize = true, Margin = new Padding(10, 9, 10, 0) };
            _lblUptime = new Label { Text = "", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 9, 0, 0) };

            actions.Controls.Add(_btnConnect);
            actions.Controls.Add(_btnDisconnect);
            actions.Controls.Add(_lblState);
            actions.Controls.Add(_lblUptime);

            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Padding = new Padding(4, 4, 4, 4);

            // Log: chiếm phần còn lại của panel
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 8.5F),
                BackColor = Color.FromArgb(252, 252, 252)
            };

            var logBox = new GroupBox
            {
                Text = "Nhật ký",
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 4, 6, 4),
                Margin = new Padding(8, 4, 10, 8),
                MinimumSize = new Size(0, 90)
            };
            logBox.Controls.Add(_txtLog);

            // Nút hành động luôn cố định ở đáy; phần form + nhật ký cuộn được nếu thiếu chỗ.
            actions.Dock = DockStyle.Bottom;
            actions.AutoSize = false;
            actions.Height = 46;

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 200F));
            stack.Controls.Add(_form, 0, 0);
            stack.Controls.Add(logBox, 0, 1);

            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroller.Controls.Add(stack);

            panel.Controls.Add(scroller);
            panel.Controls.Add(actions);
            panel.Padding = new Padding(4);

            UpdateEquivalent();
        }

        private int _lastRow = -1;

        /// <summary>Thêm một hàng mới cho form và trả về chỉ số hàng.</summary>
        private int NextRow()
        {
            int row = _form.RowCount;
            _form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _form.RowCount = row + 1;
            return row;
        }

        /// <summary>Thêm nhãn ở cột trái và ghi nhớ hàng để control giá trị vào cùng hàng.</summary>
        private void AddLabelRow(string text)
        {
            int row = NextRow();
            _form.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 0)
            }, 0, row);
            _lastRow = row;
        }

        private TextBox AddTextRow(int maxLength, bool password = false)
        {
            var box = new TextBox
            {
                Dock = DockStyle.Fill,
                MaxLength = maxLength,
                UseSystemPasswordChar = password,
                Margin = new Padding(0, 3, 0, 3)
            };
            _form.Controls.Add(box, 1, _lastRow);
            return box;
        }

        private NumericUpDown AddNumberRow(int value, int min, int max, int width)
        {
            var num = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Min(max, Math.Max(min, value)),
                Width = width,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 0, 3)
            };
            _form.Controls.Add(num, 1, _lastRow);
            return num;
        }

        // ================= PHIÊN =================
        private void LoadSessions()
        {
            _sessions.Clear();
            _sessions.AddRange(ProfileStore.Load());
            _sessions.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            RefreshList();

            if (_sessions.Count > 0) _list.SelectedIndex = 0;
            else OnNewSession(null, EventArgs.Empty);
        }

        private void RefreshList()
        {
            _loading = true;
            var selectedId = _current != null ? _current.Id : null;
            string filter = _txtFilter != null ? _txtFilter.Text.Trim() : "";

            _list.Items.Clear();
            foreach (var s in _sessions)
            {
                if (filter.Length > 0 &&
                    s.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _list.Items.Add(s);
            }

            if (selectedId != null)
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var item = _list.Items[i] as SessionProfile;
                    if (item != null && item.Id == selectedId)
                    {
                        _list.SelectedIndex = i;
                        break;
                    }
                }
            }

            _loading = false;
        }

        private void OnSessionSelected(object sender, EventArgs e)
        {
            if (_loading) return;
            var profile = _list.SelectedItem as SessionProfile;
            if (profile == null) return;

            _current = profile;
            FillForm(profile);
        }

        private void FillForm(SessionProfile p)
        {
            _loading = true;

            _txtName.Text = p.Name;
            _txtHost.Text = p.ServerHost;
            _numSshPort.Value = Clamp(p.SshPort, 1, 65535, 22);
            _txtUser.Text = p.Username;
            _txtPass.Text = ProfileStore.Unprotect(p.ProtectedPassword) ?? "";
            _chkRemember.Checked = p.RememberPassword;
            _rbLocal.Checked = p.Mode == ForwardMode.Local;
            _rbRemote.Checked = p.Mode == ForwardMode.Remote;
            _txtInsideHost.Text = p.InsideHost;
            _numInsidePort.Value = Clamp(p.InsidePort, 1, 65535, 80);
            _numOutsidePort.Value = Clamp(p.OutsidePort, 0, 65535, 8080);
            _txtBind.Text = p.BindAddress;
            _numKeepAlive.Value = Clamp(p.KeepAliveSeconds, 5, 600, 30);
            _chkAutoReconnect.Checked = p.AutoReconnect;
            _txtKey.Text = p.PrivateKeyPath;

            _loading = false;
            UpdateEquivalent();
        }

        private static decimal Clamp(int value, int min, int max, int fallback)
        {
            if (value < min) value = fallback;
            if (value > max) value = max;
            return value;
        }

        private SessionProfile ReadForm(SessionProfile target)
        {
            var p = target ?? new SessionProfile();

            p.Name = _txtName.Text.Trim();
            p.ServerHost = _txtHost.Text.Trim();
            p.SshPort = (int)_numSshPort.Value;
            p.Username = _txtUser.Text.Trim();

            p.Mode = _rbRemote.Checked ? ForwardMode.Remote : ForwardMode.Local;
            p.InsideHost = string.IsNullOrWhiteSpace(_txtInsideHost.Text) ? "127.0.0.1" : _txtInsideHost.Text.Trim();
            p.InsidePort = (int)_numInsidePort.Value;
            p.OutsidePort = (int)_numOutsidePort.Value;

            string bind = _txtBind.Text.Trim();
            p.BindAddress = string.IsNullOrEmpty(bind) ? "127.0.0.1" : bind;

            p.KeepAliveSeconds = (int)_numKeepAlive.Value;
            p.AutoReconnect = _chkAutoReconnect.Checked;
            p.PrivateKeyPath = _txtKey.Text.Trim();

            p.RememberPassword = _chkRemember.Checked;
            p.ProtectedPassword = p.RememberPassword ? ProfileStore.Protect(_txtPass.Text) : null;

            if (string.IsNullOrEmpty(p.Name))
            {
                p.Name = string.IsNullOrEmpty(p.Username)
                    ? "Phiên mới"
                    : string.Format("{0}@{1}", p.Username, p.ServerHost);
            }

            return p;
        }

        private void UpdateEquivalent()
        {
            if (_lblEquivalent == null) return;
            var preview = new SessionProfile
            {
                ServerHost = _txtHost.Text.Trim(),
                SshPort = (int)_numSshPort.Value,
                Username = _txtUser.Text.Trim(),
                InsideHost = string.IsNullOrWhiteSpace(_txtInsideHost.Text) ? "127.0.0.1" : _txtInsideHost.Text.Trim(),
                InsidePort = (int)_numInsidePort.Value,
                OutsidePort = (int)_numOutsidePort.Value,
                BindAddress = _txtBind.Text.Trim(),
                Mode = _rbRemote.Checked ? ForwardMode.Remote : ForwardMode.Local
            };
            _lblEquivalent.Text = preview.ToSshCommandLine();
        }

        private void OnNewSession(object sender, EventArgs e)
        {
            StopTunnel();
            _current = null;
            _list.ClearSelected();
            FillForm(new SessionProfile
            {
                ServerHost = "",
                SshPort = 22,
                Username = "",
                InsideHost = "127.0.0.1",
                InsidePort = 80,
                OutsidePort = 8080,
                BindAddress = "127.0.0.1",
                Mode = ForwardMode.Local
            });
            _txtName.Focus();
        }

        private void OnDuplicateSession(object sender, EventArgs e)
        {
            if (_current == null) return;
            var copy = _current.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = _current.Name + " (bản sao)";
            _sessions.Add(copy);
            ProfileStore.Save(_sessions);
            _current = copy;
            RefreshList();
        }

        private void OnDeleteSession(object sender, EventArgs e)
        {
            if (_current == null) return;
            if (MessageBox.Show("Xoá phiên \"" + _current.Name + "\"?", "Xác nhận",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            StopTunnel();
            _sessions.RemoveAll(x => x.Id == _current.Id);
            ProfileStore.Save(_sessions);
            _current = null;
            RefreshList();

            if (_sessions.Count > 0) _list.SelectedIndex = 0;
            else OnNewSession(null, EventArgs.Empty);
        }

        private bool OnSaveSessionCore()
        {
            if (string.IsNullOrWhiteSpace(_txtHost.Text))
            {
                MessageBox.Show("Chưa nhập địa chỉ bên ngoài (IP hoặc hostname máy chủ).",
                    "Thiếu thông tin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(_txtUser.Text))
            {
                MessageBox.Show("Chưa nhập tài khoản SSH.",
                    "Thiếu thông tin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            SessionProfile existing = _current;
            if (existing == null)
            {
                existing = new SessionProfile();
                _sessions.Add(existing);
            }

            var updated = ReadForm(existing);
            int index = _sessions.IndexOf(existing);
            if (index < 0) _sessions.Add(updated);
            else _sessions[index] = updated;

            ProfileStore.Save(_sessions);
            _current = updated;
            RefreshList();
            return true;
        }

        private void OnSaveSession(object sender, EventArgs e)
        {
            if (OnSaveSessionCore()) AppendLog("[đã lưu] " + _current.Name);
        }

        private void OnBrowseKey(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Chọn khoá riêng SSH";
                dialog.Filter = "Tất cả file|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) _txtKey.Text = dialog.FileName;
            }
        }

        // ================= KẾT NỐI =================
        private void StartTunnel()
        {
            if (_engine != null &&
                (_engine.State == TunnelState.Connected || _engine.State == TunnelState.Connecting))
                return;

            if (!OnSaveSessionCore()) return;

            var profile = _current;

            // Kiểm tra cổng trước khi kết nối, báo lỗi rõ ràng thay vì "lỗi mạng" khó hiểu.
            var portProblem = TunnelEngine.PreflightLocalPort(profile);
            if (portProblem != null)
            {
                ShowPortProblem(profile, portProblem);
                return;
            }

            StopTunnel();

            _engine = new TunnelEngine();
            _engine.Log += (s, ev) => BeginInvoke(new Action(() => AppendLog(ev.Message, ev.IsError)));
            _engine.StateChanged += (s, ev) => BeginInvoke(new Action(() => OnStateChanged(ev)));
            _engine.HostKeyChallenge += (s, ev) => PromptHostKey(ev);

            try
            {
                _engine.Start(profile);
                profile.LastUsedUtc = DateTime.UtcNow;
                ProfileStore.Save(_sessions);
                _btnConnect.Enabled = false;
                _btnDisconnect.Enabled = true;
            }
            catch (Exception ex)
            {
                AppendLog("Không bắt đầu được: " + TunnelEngine.Explain(ex), true);
                MessageBox.Show(TunnelEngine.Explain(ex), "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowPortProblem(SessionProfile profile, PortCheck.Result problem)
        {
            string text = PortCheck.BuildExplanation(problem, profile.OutsidePort);

            if (problem.IsInUse || problem.IsReservedRange)
            {
                int suggested = PortCheck.SuggestFreePort(profile.BindAddress, profile.OutsidePort + 1, 200);
                if (suggested > 0)
                    text += string.Format("\nCổng trống gần nhất: {0}", suggested);

                var answer = MessageBox.Show(text + "\n\nChọn cổng khác ngay bây giờ?",
                    "Không mở được cổng", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer == DialogResult.Yes && suggested > 0)
                {
                    _numOutsidePort.Value = suggested;
                    UpdateEquivalent();
                }
                return;
            }

            MessageBox.Show(text, "Không mở được cổng", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void PromptHostKey(HostKeyChallengeEventArgs ev)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    string message = string.Format(
                        "Máy chủ {0}:{1} gửi vân tay khoá:\n\n{2}\n\nChấp nhận và kết nối?",
                        ev.Host, ev.Port, ev.Fingerprint);

                    if (ev.HasChanged)
                    {
                        message += "\n\nCẢNH BÁO: vân tay khác lần trước!\nTrước: " + ev.PreviousFingerprint;
                        AppendLog("Vân tay máy chủ đã thay đổi, đang hỏi người dùng.", true);
                    }
                    else
                    {
                        AppendLog("Máy chủ gửi vân tay mới: " + ev.Fingerprint);
                    }

                    var answer = MessageBox.Show(this, message,
                        ev.HasChanged ? "Cảnh báo bảo mật" : "Xác nhận máy chủ",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                    ev.Respond(answer == DialogResult.Yes, answer == DialogResult.Yes);
                }));
            }
            catch
            {
                ev.Respond(false, false);
            }
        }

        private void StopTunnel()
        {
            var engine = _engine;
            _engine = null;

            if (engine != null)
            {
                try { engine.Stop(); }
                catch { }
                try { engine.Dispose(); }
                catch { }
            }

            _btnConnect.Enabled = true;
            _btnDisconnect.Enabled = false;
            _lblState.Text = "Chưa kết nối";
            _lblState.ForeColor = Color.Black;
            _lblUptime.Text = "";
        }

        private void OnStateChanged(TunnelStateEventArgs ev)
        {
            _lblState.Text = ev.Detail;

            switch (ev.State)
            {
                case TunnelState.Connected:
                    _lblState.ForeColor = Color.FromArgb(0, 120, 60);
                    _btnConnect.Enabled = false;
                    _btnDisconnect.Enabled = true;
                    if (_engine != null && _engine.BoundPort > 0)
                    {
                        _lblState.Text = string.Format("Đã kết nối · {0} → {1}:{2}",
                            _engine.BoundPort, _engine.Profile.InsideHost, _engine.Profile.InsidePort);
                    }
                    break;

                case TunnelState.Connecting:
                    _lblState.ForeColor = Color.FromArgb(180, 110, 0);
                    break;

                case TunnelState.Error:
                    _lblState.ForeColor = Color.Firebrick;
                    _btnConnect.Enabled = true;
                    _btnDisconnect.Enabled = false;
                    break;

                case TunnelState.Stopped:
                    _lblState.ForeColor = Color.Black;
                    break;
            }
        }

        private void DumpLayout()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("ClientSize=" + ClientSize);
                DumpControl(sb, this, 0);
                System.IO.Directory.CreateDirectory(ProfileStore.FolderPath);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(ProfileStore.FolderPath, "layout.txt"), sb.ToString());
            }
            catch { }
        }

        private void DumpControl(StringBuilder sb, Control c, int depth)
        {
            string line = new string(' ', depth * 2) + c.GetType().Name
                          + " [" + c.Name + "] Vis=" + c.Visible
                          + " Bounds=" + c.Bounds + " Dock=" + c.Dock;
            sb.AppendLine(line);
            foreach (Control child in c.Controls) DumpControl(sb, child, depth + 1);
        }

        private void AppendLog(string message, bool isError = false)
        {
            if (_txtLog.TextLength > 80000) _txtLog.Clear();

            string line = string.Format("[{0:HH:mm:ss}] {1}{2}",
                DateTime.Now, isError ? "! " : "", message);

            _txtLog.AppendText(line + Environment.NewLine);

            if (isError)
            {
                try
                {
                    Directory.CreateDirectory(ProfileStore.LogFolderPath);
                    File.AppendAllText(
                        Path.Combine(ProfileStore.LogFolderPath, DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                        line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>Dựng phiên từ tham số dòng lệnh và kết nối ngay nếu có --connect.</summary>
        private void ApplyOptions()
        {
            var p = new SessionProfile
            {
                Name = string.Format("{0}@{1}", _options.User ?? "", _options.Host ?? ""),
                ServerHost = _options.Host,
                SshPort = _options.SshPort,
                Username = _options.User,
                ProtectedPassword = string.IsNullOrEmpty(_options.Password)
                    ? null
                    : ProfileStore.Protect(_options.Password),
                RememberPassword = !string.IsNullOrEmpty(_options.Password),
                InsideHost = _options.InsideHost,
                InsidePort = _options.InsidePort,
                OutsidePort = _options.OutsidePort,
                BindAddress = string.IsNullOrEmpty(_options.Bind) ? "127.0.0.1" : _options.Bind,
                Mode = _options.Remote ? ForwardMode.Remote : ForwardMode.Local,
                AutoReconnect = true,
                KeepAliveSeconds = 30
            };

            FillForm(p);
            _current = p;

            if (_options.Remember)
            {
                _sessions.Add(p);
                ProfileStore.Save(_sessions);
                RefreshList();
            }

            if (_options.Connect)
            {
                // Form chưa có window handle trong constructor nên phải chờ hiện xong.
                EventHandler once = null;
                once = (s2, e2) =>
                {
                    Shown -= once;
                    StartTunnel();
                };
                Shown += once;
            }
        }

        private void RestoreUiState()
        {
            try
            {
                var ui = ProfileStore.LoadUi();
                if (ui.Width < 700 || ui.Height < 500) return;

                var bounds = new Rectangle(ui.Left, ui.Top, ui.Width, ui.Height);
                bool visible = false;
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.IntersectsWith(bounds))
                    {
                        visible = true;
                        break;
                    }
                }
                if (!visible) return;

                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
                if (ui.Maximized) WindowState = FormWindowState.Maximized;

                if (ui.ListWidth > 0)
                {
                    int max = ClientSize.Width - _split.Panel2MinSize - _split.SplitterWidth;
                    if (max <= _split.Panel1MinSize) return;
                    _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Math.Min(ui.ListWidth, max));
                }
            }
            catch
            {
            }
        }

        private void SaveUiState()
        {
            try
            {
                var ui = new ProfileStore.UiState
                {
                    Maximized = WindowState == FormWindowState.Maximized
                };

                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                ui.Width = b.Width;
                ui.Height = b.Height;
                ui.Left = b.Left;
                ui.Top = b.Top;
                ui.ListWidth = _split.SplitterDistance;

                ProfileStore.SaveUi(ui);
            }
            catch
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveUiState();
            StopTunnel();
            base.OnFormClosing(e);
        }
    }
}
