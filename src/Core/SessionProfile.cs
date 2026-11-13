using System;

namespace SshTunnelManager.Core
{
    public enum ForwardMode
    {
        /// <summary>ssh -L : mở cổng trên máy Windows, chuyển vào server.</summary>
        Local = 0,

        /// <summary>ssh -R : mở cổng trên server, chuyển về máy Windows.</summary>
        Remote = 1
    }

    public enum TunnelState
    {
        Stopped = 0,
        Connecting = 1,
        Connected = 2,
        Stopping = 3,
        Error = 4
    }

    public class SessionProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";

        /// <summary>Địa chỉ bên ngoài - IP hoặc hostname của máy chủ SSH ("địa chỉ bên ngoài").</summary>
        public string ServerHost { get; set; } = "";

        /// <summary>Cổng SSH ("port").</summary>
        public int SshPort { get; set; } = 22;

        public string Username { get; set; } = "";

        /// <summary>Mật khẩu đã mã hoá DPAPI (base64). Null nếu không nhớ.</summary>
        public string ProtectedPassword { get; set; }

        public bool RememberPassword { get; set; } = true;

        /// <summary>Địa chỉ bên trong - host đích nhìn từ phía server ("địa chỉ bên trong").</summary>
        public string InsideHost { get; set; } = "127.0.0.1";

        /// <summary>Cổng bên trong trên host đích ("nat inside").</summary>
        public int InsidePort { get; set; } = 80;

        /// <summary>Cổng phía ngoài ("outside port"). Local: cổng listen trên Windows. Remote: cổng listen trên server.</summary>
        public int OutsidePort { get; set; } = 8080;

        /// <summary>Địa chỉ bind cho cổng ngoài. 127.0.0.1 = chỉ máy này; 0.0.0.0 = cả LAN.</summary>
        public string BindAddress { get; set; } = "127.0.0.1";

        public ForwardMode Mode { get; set; } = ForwardMode.Local;

        public string PrivateKeyPath { get; set; } = "";
        public string ProtectedPassphrase { get; set; }

        public int KeepAliveSeconds { get; set; } = 30;
        public bool AutoReconnect { get; set; } = true;

        /// <summary>Vân tay SHA256 của host key đã tin tưởng (TOFU). Rỗng = chưa biết.</summary>
        public string HostKeyFingerprint { get; set; } = "";

        public DateTime LastUsedUtc { get; set; }

        public SessionProfile Clone()
        {
            return (SessionProfile)MemberwiseClone();
        }

        /// <summary>Lệnh ssh tương đương, để hiển thị cho người dùng đối chiếu.</summary>
        public string ToSshCommandLine()
        {
            string forward = Mode == ForwardMode.Local
                ? string.Format("-L {0}:{1}:{2}", OutsidePort, InsideHost, InsidePort)
                : string.Format("-R {0}:{1}:{2}", OutsidePort, InsideHost, InsidePort);

            string bind = "";
            if (!string.IsNullOrEmpty(BindAddress) && BindAddress != "127.0.0.1")
                bind = Mode == ForwardMode.Local
                    ? string.Format("-L {0}:{1}:{2}:{3}", BindAddress, OutsidePort, InsideHost, InsidePort)
                    : string.Format("-R {0}:{1}:{2}:{3}", BindAddress, OutsidePort, InsideHost, InsidePort);

            string fwd = bind.Length > 0 ? bind : forward;
            string user = string.IsNullOrEmpty(Username) ? "user" : Username;
            string host = string.IsNullOrEmpty(ServerHost) ? "host" : ServerHost;
            return string.Format("ssh -p {0} -N {1} {2}@{3}", SshPort, fwd, user, host);
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            return string.Format("{0}@{1}", Username, ServerHost);
        }
    }
}
