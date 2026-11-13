using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SshTunnelManager.Core
{
    /// <summary>
    /// Kiểm tra cổng có mở được trên Windows hay không.
    /// Lỗi WSAEACCES (10013) thường do cổng nằm trong dải Hyper-V / WSL / Docker đã đặt trước,
    /// chứ không phải do thiếu quyền admin.
    /// </summary>
    public static class PortCheck
    {
        public const int WsaAccessDenied = 10013;
        public const int WsaAddrInUse = 10048;

        public class Result
        {
            public bool Ok;
            public SocketError Error;
            public string Message;

            public bool IsReservedRange
            {
                get { return Error == SocketError.AccessDenied; }
            }

            public bool IsInUse
            {
                get { return Error == SocketError.AddressAlreadyInUse; }
            }
        }

        public static Result TryBind(string bindAddress, int port)
        {
            var result = new Result();
            if (port <= 0)
            {
                result.Ok = true;
                return result;
            }

            IPAddress address;
            if (!IPAddress.TryParse(string.IsNullOrEmpty(bindAddress) ? "127.0.0.1" : bindAddress, out address))
                address = IPAddress.Loopback;

            TcpListener listener = null;
            try
            {
                listener = new TcpListener(address, port);
                listener.Start();
                result.Ok = true;
                return result;
            }
            catch (SocketException ex)
            {
                result.Ok = false;
                result.Error = ex.SocketErrorCode;
                result.Message = ex.Message;
                return result;
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Stop(); }
                    catch { }
                }
            }
        }

        /// <summary>Đọc dải cổng Windows đã đặt trước (netsh). Trả chuỗi rỗng nếu không đọc được.</summary>
        public static List<string> GetExcludedRanges()
        {
            var ranges = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("netsh", "interface ipv4 show excludedportrange protocol=tcp")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return ranges;
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

                    foreach (Match m in Regex.Matches(output, @"^\s*(\d{2,5})\s+(\d{2,5})\s*$",
                        RegexOptions.Multiline))
                    {
                        ranges.Add(m.Groups[1].Value + " - " + m.Groups[2].Value);
                    }
                }
            }
            catch
            {
                // netsh không có hoặc bị chặn: bỏ qua, chỉ hiện thông báo chung.
            }
            return ranges;
        }

        public static bool IsInExcludedRange(int port)
        {
            if (port <= 0) return false;
            try
            {
                var psi = new ProcessStartInfo("netsh", "interface ipv4 show excludedportrange protocol=tcp")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

                    foreach (Match m in Regex.Matches(output, @"^\s*(\d{2,5})\s+(\d{2,5})\s*$",
                        RegexOptions.Multiline))
                    {
                        int start = int.Parse(m.Groups[1].Value);
                        int end = int.Parse(m.Groups[2].Value);
                        if (port >= start && port <= end) return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>Tìm một cổng trống gần cổng mong muốn, tránh cả dải bị Windows giữ.</summary>
        public static int SuggestFreePort(string bindAddress, int desired, int count)
        {
            if (desired <= 0) desired = 8000;
            for (int i = 0; i < count; i++)
            {
                int candidate = desired + i;
                if (candidate > 65535) break;
                if (IsInExcludedRange(candidate)) continue;
                if (TryBind(bindAddress, candidate).Ok) return candidate;
            }
            return 0;
        }

        public static string BuildExplanation(Result result, int port)
        {
            var sb = new StringBuilder();

            if (result.IsReservedRange)
            {
                sb.AppendLine(string.Format("Cổng {0} bị Windows giữ riêng, không phải do thiếu quyền.", port));
                sb.AppendLine();
                sb.AppendLine("Nguyên nhân thường gặp: Hyper-V, WSL2, Docker Desktop hoặc Windows NAT");
                sb.AppendLine("đã đặt trước một dải cổng. Bạn không thể dùng cổng nằm trong dải đó,");
                sb.AppendLine("kể cả khi chạy bằng quyền Administrator.");
                sb.AppendLine();

                var ranges = GetExcludedRanges();
                if (ranges.Count > 0)
                {
                    sb.AppendLine("Các dải đang bị giữ (netsh)::");
                    foreach (var r in ranges) sb.AppendLine("   " + r);
                    sb.AppendLine();
                }

                sb.AppendLine("Cách xử lý:");
                sb.AppendLine("   • Đổi sang cổng khác (app có nút gợi ý cổng trống), hoặc");
                sb.AppendLine("   • Giải phóng dải: chạy PowerShell bằng Administrator rồi");
                sb.AppendLine("       net stop winnat");
                sb.AppendLine("       net start winnat");
                sb.AppendLine("     (sẽ mất tạm thời các cổng Hyper-V đang dùng)");
                return sb.ToString();
            }

            if (result.IsInUse)
            {
                sb.AppendLine(string.Format("Cổng {0} đang bị chương trình khác dùng.", port));
                sb.AppendLine("Hãy đóng chương trình đó hoặc chọn cổng khác.");
                return sb.ToString();
            }

            if (result.Error == SocketError.AccessDenied && port < 1024)
            {
                sb.AppendLine(string.Format("Cổng {0} nằm dưới 1024 nên cần quyền Administrator.", port));
                sb.AppendLine("Chuột phải file exe → Run as administrator, hoặc chọn cổng từ 1024 trở lên.");
                return sb.ToString();
            }

            sb.AppendLine(string.Format("Không mở được cổng {0}: {1}", port, result.Message));
            return sb.ToString();
        }
    }
}
