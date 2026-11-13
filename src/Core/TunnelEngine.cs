using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshTunnelManager.Core
{
    public class TunnelLogEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public bool IsError { get; private set; }

        public TunnelLogEventArgs(string message, bool isError)
        {
            Message = message;
            IsError = isError;
        }
    }

    public class TunnelStateEventArgs : EventArgs
    {
        public TunnelState State { get; private set; }
        public string Detail { get; private set; }

        public TunnelStateEventArgs(TunnelState state, string detail)
        {
            State = state;
            Detail = detail ?? "";
        }
    }

    /// <summary>
    /// Hỏi người dùng chấp nhận vân tay host key. Engine chạy ở luồng nền nên phải chờ phản hồi
    /// từ giao diện; hết thời gian thì coi như từ chối.
    /// </summary>
    public class HostKeyChallengeEventArgs : EventArgs
    {
        private readonly System.Threading.ManualResetEvent _done =
            new System.Threading.ManualResetEvent(false);

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Fingerprint { get; private set; }
        public string PreviousFingerprint { get; private set; }

        public bool Accepted { get; private set; }
        public bool Remember { get; private set; }

        public HostKeyChallengeEventArgs(string host, int port, string fingerprint, string previous)
        {
            Host = host;
            Port = port;
            Fingerprint = fingerprint;
            PreviousFingerprint = previous ?? "";
        }

        public bool HasChanged
        {
            get { return !string.IsNullOrEmpty(PreviousFingerprint); }
        }

        /// <summary>Gọi từ luồng giao diện sau khi người dùng chọn.</summary>
        public void Respond(bool accepted, bool remember)
        {
            Accepted = accepted;
            Remember = remember;
            _done.Set();
        }

        /// <summary>Chờ người dùng trả lời, tối đa timeout.</summary>
        public bool WaitForResponse(TimeSpan timeout)
        {
            return _done.WaitOne(timeout);
        }
    }

    /// <summary>
    /// Giữ một phiên SSH và một cổng forward. Tự kết nối lại khi rớt nếu bật AutoReconnect.
    /// </summary>
    public sealed class TunnelEngine : IDisposable
    {
        private readonly object _sync = new object();
        private SshClient _client;
        private ForwardedPort _port;
        private Thread _watchThread;
        private volatile bool _stopRequested;
        private volatile bool _disposed;
        private DateTime _connectedAtUtc;
        private volatile bool _hostKeyRejected;

        public SessionProfile Profile { get; private set; }
        public TunnelState State { get; private set; }
        public string ActualBindAddress { get; private set; }
        public int ReconnectAttempts { get; private set; }

        public event EventHandler<TunnelLogEventArgs> Log;
        public event EventHandler<TunnelStateEventArgs> StateChanged;

        /// <summary>Người dùng chấp nhận/từ chối vân tay host key. Null = tự chấp nhận lần đầu.</summary>
        public event EventHandler<HostKeyChallengeEventArgs> HostKeyChallenge;

        public TunnelEngine()
        {
            State = TunnelState.Stopped;
        }

        public TimeSpan Uptime
        {
            get
            {
                if (State != TunnelState.Connected) return TimeSpan.Zero;
                return DateTime.UtcNow - _connectedAtUtc;
            }
        }

        /// <summary>Cổng ngoài thực tế đang mở (khác OutsidePort khi yêu cầu cổng 0 = tự chọn).</summary>
        public int BoundPort
        {
            get
            {
                var local = _port as ForwardedPortLocal;
                if (local != null && local.IsStarted) return (int)local.BoundPort;
                return Profile != null ? Profile.OutsidePort : 0;
            }
        }

        public void Start(SessionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");

            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("TunnelEngine");
                if (State == TunnelState.Connecting || State == TunnelState.Connected)
                    throw new InvalidOperationException("Phiên đang chạy.");

                Profile = profile.Clone();
                _stopRequested = false;
                _hostKeyRejected = false;
                ReconnectAttempts = 0;
            }

            SetState(TunnelState.Connecting, "Đang kết nối…");

            _watchThread = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = "ssh-tunnel-watch"
            };
            _watchThread.Start();
        }

        public void Stop()
        {
            _stopRequested = true;
            SetState(TunnelState.Stopping, "Đang đóng…");
            CloseConnection();
            SetState(TunnelState.Stopped, "Đã dừng");
        }

        private void WatchLoop()
        {
            while (!_stopRequested && !_disposed)
            {
                try
                {
                    ConnectOnce();

                    // Giữ luồng sống trong khi kết nối còn tốt.
                    while (!_stopRequested && _client != null && _client.IsConnected)
                    {
                        Thread.Sleep(500);
                    }

                    if (_stopRequested) break;

                    LogLine("Mất kết nối SSH.", true);
                }
                catch (Exception ex)
                {
                    if (_stopRequested) break;
                    if (_hostKeyRejected)
                    {
                        SetState(TunnelState.Error, "Đã huỷ vì vân tay máy chủ không được chấp nhận");
                        return;
                    }
                    LogLine("Lỗi: " + Explain(ex), true);
                }
                finally
                {
                    CloseConnection();
                }

                if (_stopRequested) break;

                var profile = Profile;
                if (profile == null || !profile.AutoReconnect)
                {
                    SetState(TunnelState.Error, "Mất kết nối (không tự kết nối lại)");
                    return;
                }

                ReconnectAttempts++;
                int delay = Math.Min(60, 2 * ReconnectAttempts);
                SetState(TunnelState.Connecting,
                    string.Format("Kết nối lại sau {0}s (lần {1})…", delay, ReconnectAttempts));
                LogLine(string.Format("Thử kết nối lại sau {0}s.", delay), true);

                for (int i = 0; i < delay * 2 && !_stopRequested; i++)
                    Thread.Sleep(500);

                if (ReconnectAttempts >= 50)
                {
                    SetState(TunnelState.Error, "Đã thử lại 50 lần, dừng.");
                    return;
                }
            }
        }

        private void ConnectOnce()
        {
            var profile = Profile;
            ConnectionInfo info = BuildConnectionInfo(profile);

            var client = new SshClient(info);
            client.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, profile.KeepAliveSeconds));

            string expected = profile.HostKeyFingerprint;
            bool trusted = false;

            client.HostKeyReceived += (sender, e) =>
            {
                string fp = ComputeFingerprint(e.HostKey);
                e.CanTrust = false; // chỉ tin sau khi kiểm tra / người dùng chấp nhận

                if (!string.IsNullOrEmpty(expected) &&
                    string.Equals(expected, fp, StringComparison.OrdinalIgnoreCase))
                {
                    e.CanTrust = true;
                    trusted = true;
                    return;
                }

                // Chưa biết vân tay, hoặc vân tay đã khác: hỏi người dùng.
                var handler = HostKeyChallenge;
                if (handler == null)
                {
                    if (string.IsNullOrEmpty(expected))
                    {
                        LogLine("Vân tay host key: " + fp);
                        e.CanTrust = true;
                        trusted = true;
                    }
                    else
                    {
                        LogLine("CẢNH BÁO: vân tay host key đã thay đổi! Trước: " + expected + " - Nay: " + fp, true);
                        e.CanTrust = false;
                    }
                    return;
                }

                var args = new HostKeyChallengeEventArgs(profile.ServerHost, profile.SshPort, fp, expected);
                var h = handler;
                if (h != null) h(this, args);

                bool answered = args.WaitForResponse(TimeSpan.FromMinutes(5));

                if (!answered)
                {
                    LogLine("Hết thời gian chờ xác nhận vân tay máy chủ.", true);
                    e.CanTrust = false;
                    return;
                }

                if (args.Accepted)
                {
                    e.CanTrust = true;
                    trusted = true;
                    if (args.Remember)
                    {
                        Profile.HostKeyFingerprint = fp;
                        LogLine("Đã lưu vân tay máy chủ: " + fp);
                    }
                    else
                    {
                        LogLine("Chấp nhận vân tay cho phiên này (không lưu).");
                    }
                }
                else
                {
                    _hostKeyRejected = true;
                    LogLine("Đã huỷ: bạn không chấp nhận vân tay máy chủ.", true);
                    e.CanTrust = false;
                    trusted = false;
                }
            };

            LogLine(string.Format("Kết nối {0}@{1}:{2} …", profile.Username, profile.ServerHost, profile.SshPort));
            client.Connect();

            if (!string.IsNullOrEmpty(expected) && !trusted)
            {
                client.Dispose();
                throw new InvalidOperationException("Host key không khớp.");
            }

            _client = client;
            _connectedAtUtc = DateTime.UtcNow;

            ForwardedPort port = CreatePort(profile);
            port.Exception += (sender, e) =>
            {
                if (e.Exception != null)
                    LogLine("Lỗi cổng forward: " + Explain(e.Exception), true);
            };

            client.AddForwardedPort(port);
            port.Start();
            _port = port;

            var local = port as ForwardedPortLocal;
            if (local != null)
            {
                ActualBindAddress = local.BoundHost;
                LogLine(string.Format("Đang mở {0}:{1} -> {2}:{3} (qua server)",
                    local.BoundHost, local.BoundPort, profile.InsideHost, profile.InsidePort));
            }
            else
            {
                ActualBindAddress = profile.BindAddress;
                LogLine(string.Format("Đang mở trên server {0}:{1} -> máy này {2}:{3}",
                    profile.BindAddress, profile.OutsidePort, profile.InsideHost, profile.InsidePort));
            }

            ReconnectAttempts = 0;
            SetState(TunnelState.Connected, "Đã kết nối");
            LogLine("Sẵn sàng.");
        }

        private ForwardedPort CreatePort(SessionProfile profile)
        {
            if (profile.Mode == ForwardMode.Local)
            {
                string bind = string.IsNullOrEmpty(profile.BindAddress) ? "127.0.0.1" : profile.BindAddress;
                bool allInterfaces = bind == "0.0.0.0" || bind == "*" || bind == "::";

                if (allInterfaces)
                {
                    // SSH.NET không bind được 0.0.0.0 trực tiếp: bind mọi IPv4 interface của máy.
                    return new ForwardedPortLocal((uint)profile.OutsidePort, profile.InsideHost, (uint)profile.InsidePort);
                }

                return new ForwardedPortLocal(bind, (uint)profile.OutsidePort, profile.InsideHost, (uint)profile.InsidePort);
            }

            string boundHost = string.IsNullOrEmpty(profile.BindAddress) ? "127.0.0.1" : profile.BindAddress;
            return new ForwardedPortRemote(boundHost, (uint)profile.OutsidePort, profile.InsideHost, (uint)profile.InsidePort);
        }

        private static ConnectionInfo BuildConnectionInfo(SessionProfile profile)
        {
            string password = ProfileStore.Unprotect(profile.ProtectedPassword);
            var methods = new System.Collections.Generic.List<AuthenticationMethod>();

            if (!string.IsNullOrEmpty(profile.PrivateKeyPath) && File.Exists(profile.PrivateKeyPath))
            {
                PrivateKeyFile key;
                string pass = ProfileStore.Unprotect(profile.ProtectedPassphrase);
                key = string.IsNullOrEmpty(pass)
                    ? new PrivateKeyFile(profile.PrivateKeyPath)
                    : new PrivateKeyFile(profile.PrivateKeyPath, pass);
                methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, key));
            }

            if (!string.IsNullOrEmpty(password))
                methods.Add(new PasswordAuthenticationMethod(profile.Username, password));

            if (methods.Count == 0)
                throw new InvalidOperationException("Chưa có mật khẩu hoặc khoá riêng cho phiên này.");

            var connection = new ConnectionInfo(
                profile.ServerHost,
                profile.SshPort <= 0 ? 22 : profile.SshPort,
                profile.Username,
                methods.ToArray());

            connection.Timeout = TimeSpan.FromSeconds(30);
            return connection;
        }

        private static string ComputeFingerprint(byte[] hostKey)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(hostKey);
                return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
            }
        }

        private void CloseConnection()
        {
            try
            {
                if (_port != null)
                {
                    try { if (_port.IsStarted) _port.Stop(); }
                    catch { }
                    try { ((IDisposable)_port).Dispose(); }
                    catch { }
                    _port = null;
                }
            }
            catch { }

            try
            {
                if (_client != null)
                {
                    try { if (_client.IsConnected) _client.Disconnect(); }
                    catch { }
                    try { _client.Dispose(); }
                    catch { }
                    _client = null;
                }
            }
            catch { }
        }

        public static string Explain(Exception ex)
        {
            if (ex is SshAuthenticationException)
                return "Sai tài khoản hoặc mật khẩu (server từ chối xác thực).";
            if (ex is SshConnectionException)
                return "Không kết nối được tới máy chủ: " + ex.Message;
            var socket = ex as System.Net.Sockets.SocketException;
            if (socket != null || ex.InnerException is System.Net.Sockets.SocketException)
            {
                var se = socket ?? (System.Net.Sockets.SocketException)ex.InnerException;
                switch (se.SocketErrorCode)
                {
                    case System.Net.Sockets.SocketError.ConnectionRefused:
                        return "Máy chủ từ chối kết nối: cổng SSH bị đóng hoặc firewall chặn.";
                    case System.Net.Sockets.SocketError.HostNotFound:
                    case System.Net.Sockets.SocketError.NoData:
                        return "Không phân giải được tên miền/IP máy chủ.";
                    case System.Net.Sockets.SocketError.TimedOut:
                        return "Hết thời gian chờ kết nối (mạng chậm hoặc bị chặn).";
                    case System.Net.Sockets.SocketError.AccessDenied:
                        return "Windows chặn mở cổng này. Thường do cổng nằm trong dải Hyper-V/WSL/Docker " +
                               "đã đặt trước, hoặc cổng <1024 mà chưa chạy bằng Administrator.";
                    case System.Net.Sockets.SocketError.AddressAlreadyInUse:
                        return "Cổng đang bị chương trình khác sử dụng.";
                }
                return "Lỗi mạng (" + se.SocketErrorCode + "): " + se.Message;
            }
            if (ex is SshException) return ex.Message;
            if (ex is UnauthorizedAccessException) return "Bị từ chối truy cập file hoặc cổng.";
            return ex.Message;
        }

        private void LogLine(string message)
        {
            LogLine(message, false);
        }

        private void LogLine(string message, bool isError)
        {
            var handler = Log;
            if (handler != null)
            {
                try { handler(this, new TunnelLogEventArgs(message, isError)); }
                catch { }
            }
        }

        private void SetState(TunnelState state, string detail)
        {
            State = state;
            var handler = StateChanged;
            if (handler != null)
            {
                try { handler(this, new TunnelStateEventArgs(state, detail)); }
                catch { }
            }
        }

        /// <summary>
        /// Kiểm tra trước khi kết nối: cổng ngoài có mở được không (chỉ áp dụng cho -L).
        /// Trả null nếu ổn.
        /// </summary>
        public static PortCheck.Result PreflightLocalPort(SessionProfile profile)
        {
            if (profile == null) return null;
            if (profile.Mode != ForwardMode.Local) return null;
            if (profile.OutsidePort <= 0) return null;

            string bind = string.IsNullOrEmpty(profile.BindAddress) ? "127.0.0.1" : profile.BindAddress;
            if (bind == "0.0.0.0" || bind == "*" || bind == "::")
                bind = "0.0.0.0";

            var result = PortCheck.TryBind(bind, profile.OutsidePort);
            return result.Ok ? null : result;
        }

        public void Dispose()
        {
            _disposed = true;
            _stopRequested = true;
            CloseConnection();

            var thread = _watchThread;
            if (thread != null && thread.IsAlive)
                thread.Join(3000);
        }
    }
}
