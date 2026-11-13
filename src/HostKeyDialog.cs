using System;
using System.Drawing;
using System.Windows.Forms;

namespace SshTunnelManager
{
    /// <summary>
    /// Hỏi người dùng chấp nhận vân tay host key, giống lần đầu chạy ssh.
    /// </summary>
    public class HostKeyDialog : Form
    {
        public bool Accepted { get; private set; }
        public bool RememberFingerprint { get; private set; }

        private CheckBox _chkRemember;

        public HostKeyDialog(string host, int port, string fingerprint, string previousFingerprint)
        {
            bool changed = !string.IsNullOrEmpty(previousFingerprint);

            Text = changed ? "CẢNH BÁO: host key đã thay đổi" : "Xác nhận máy chủ SSH";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, changed ? 320 : 264);
            Font = new Font("Segoe UI", 9F);
            ShowInTaskbar = false;

            var title = new Label
            {
                Text = changed
                    ? "Vân tay máy chủ không khớp với lần trước!"
                    : "Không thể xác thực tính xác thực của máy chủ.",
                Location = new Point(16, 14),
                Size = new Size(528, 22),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = changed ? Color.Firebrick : Color.Black
            };
            Controls.Add(title);

            var body = new Label
            {
                Text = changed
                    ? "Có thể ai đó đang giả mạo máy chủ, hoặc máy chủ đã được cài lại.\nNếu không chắc, hãy bấm Huỷ và kiểm tra lại."
                    : "Máy chủ đang gửi một khoá định danh. Hãy so sánh vân tay dưới đây với vân tay thật của máy chủ trước khi tin.",
                Location = new Point(16, 40),
                Size = new Size(528, 40),
                ForeColor = Color.DimGray
            };
            Controls.Add(body);

            var hostLabel = new Label
            {
                Text = string.Format("Máy chủ: {0}:{1}", host, port),
                Location = new Point(16, 84),
                Size = new Size(528, 20)
            };
            Controls.Add(hostLabel);

            var fpBox = new TextBox
            {
                Text = fingerprint,
                Location = new Point(16, 108),
                Size = new Size(528, 24),
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };
            Controls.Add(fpBox);

            var algLabel = new Label
            {
                Text = "Thuật toán: SHA256 (kiểu OpenSSH hiện đại)",
                Location = new Point(16, 136),
                Size = new Size(528, 18),
                ForeColor = Color.DimGray
            };
            Controls.Add(algLabel);

            int y = 162;

            if (changed)
            {
                var prevBox = new TextBox
                {
                    Text = previousFingerprint,
                    Location = new Point(16, y),
                    Size = new Size(528, 24),
                    ReadOnly = true,
                    Font = new Font("Consolas", 9F),
                    BackColor = Color.MistyRose
                };
                Controls.Add(prevBox);

                var prevLabel = new Label
                {
                    Text = "Vân tay đã lưu trước đó:",
                    Location = new Point(16, y - 20),
                    Size = new Size(528, 18),
                    ForeColor = Color.Firebrick
                };
                Controls.Add(prevLabel);

                y += 44;
            }

            _chkRemember = new CheckBox
            {
                Text = changed ? "Tôi hiểu rủi ro, cập nhật vân tay mới" : "Lưu vân tay này cho các lần sau",
                Location = new Point(16, y),
                Size = new Size(528, 24),
                Checked = !changed
            };
            Controls.Add(_chkRemember);
            y += 34;

            var btnAccept = new Button
            {
                Text = changed ? "Vẫn kết nối" : "Chấp nhận và kết nối",
                Location = new Point(292, y),
                Size = new Size(160, 32),
                DialogResult = DialogResult.OK
            };
            btnAccept.Click += (s, e) =>
            {
                if (changed && !_chkRemember.Checked)
                {
                    MessageBox.Show(this,
                        "Phải đánh dấu \"Tôi hiểu rủi ro\" thì mới cập nhật được vân tay mới.",
                        "Chưa xác nhận", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                Accepted = true;
                RememberFingerprint = _chkRemember.Checked;
            };
            Controls.Add(btnAccept);

            var btnCancel = new Button
            {
                Text = "Huỷ",
                Location = new Point(460, y),
                Size = new Size(84, 32),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnCancel);

            AcceptButton = btnAccept;
            CancelButton = btnCancel;
        }
    }
}
