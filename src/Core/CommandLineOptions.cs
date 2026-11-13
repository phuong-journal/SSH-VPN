using System;
using System.Collections.Generic;

namespace SshTunnelManager
{
    /// <summary>
    /// Tham số dòng lệnh, cho phép dựng phiên và kết nối ngay từ script.
    /// Ví dụ:
    ///   SshTunnelManager.exe --host 1.2.3.4 --ssh-port 225 --user openclaw --password xxx
    ///                        --inside 127.0.0.1:18789 --outside 18789 --connect
    /// </summary>
    public class CommandLineOptions
    {
        public string Host;
        public int SshPort = 22;
        public string User;
        public string Password;
        public string InsideHost = "127.0.0.1";
        public int InsidePort = 80;
        public int OutsidePort = 8080;
        public string Bind = "127.0.0.1";
        public bool Remote;
        public bool Connect;
        public bool Remember;

        public bool HasAnything
        {
            get { return !string.IsNullOrEmpty(Host); }
        }

        public static CommandLineOptions Parse(string[] args)
        {
            var o = new CommandLineOptions();
            if (args == null || args.Length == 0) return o;

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i].TrimStart('-', '/').ToLowerInvariant();
                string value = i + 1 < args.Length ? args[i + 1] : null;

                switch (key)
                {
                    case "host":
                    case "server":
                        o.Host = value;
                        i++;
                        break;
                    case "ssh-port":
                        o.SshPort = ToInt(value, 22);
                        i++;
                        break;
                    case "user":
                    case "username":
                        o.User = value;
                        i++;
                        break;
                    case "password":
                    case "pass":
                        o.Password = value;
                        i++;
                        break;
                    case "inside":
                        ParseHostPort(value, ref o.InsideHost, ref o.InsidePort);
                        i++;
                        break;
                    case "inside-port":
                        o.InsidePort = ToInt(value, 80);
                        i++;
                        break;
                    case "outside":
                    case "outside-port":
                        o.OutsidePort = ToInt(value, 8080);
                        i++;
                        break;
                    case "bind":
                        o.Bind = value;
                        i++;
                        break;
                    case "remote":
                        o.Remote = true;
                        break;
                    case "connect":
                        o.Connect = true;
                        break;
                    case "remember":
                        o.Remember = true;
                        break;
                }
            }

            return o;
        }

        private static int ToInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static void ParseHostPort(string s, ref string host, ref int port)
        {
            if (string.IsNullOrEmpty(s)) return;
            int idx = s.LastIndexOf(':');
            if (idx > 0)
            {
                host = s.Substring(0, idx);
                port = ToInt(s.Substring(idx + 1), port);
            }
            else
            {
                host = s;
            }
        }
    }
}
