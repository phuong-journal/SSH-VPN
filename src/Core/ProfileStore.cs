using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SshTunnelManager.Core
{
    /// <summary>
    /// Quản lý file cấu hình: %APPDATA%\SshTunnelManager\sessions.json
    /// Mật khẩu được mã hoá bằng DPAPI (CurrentUser) nên chỉ tài khoản Windows đã lưu mới đọc được.
    /// </summary>
    public static class ProfileStore
    {
        private static readonly object Padlock = new object();

        public static string FolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SshTunnelManager");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(FolderPath, "sessions.json"); }
        }

        public static string LogFolderPath
        {
            get { return Path.Combine(FolderPath, "logs"); }
        }

        public static List<SessionProfile> Load()
        {
            lock (Padlock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new List<SessionProfile>();
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<SessionProfile>();

                    var serializer = new JavaScriptSerializer();
                    var loaded = serializer.Deserialize<ProfileFile>(json);
                    if (loaded == null || loaded.Sessions == null) return new List<SessionProfile>();
                    return loaded.Sessions;
                }
                catch (Exception)
                {
                    // File hỏng: giữ lại bản cũ để người dùng cứu, rồi trả danh sách rỗng.
                    try
                    {
                        if (File.Exists(FilePath))
                            File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                    }
                    catch
                    {
                        // ignored
                    }
                    return new List<SessionProfile>();
                }
            }
        }

        public static void Save(List<SessionProfile> sessions)
        {
            lock (Padlock)
            {
                Directory.CreateDirectory(FolderPath);
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = serializer.Serialize(new ProfileFile { Sessions = sessions });

                // Ghi ra file tạm rồi thay thế, tránh mất dữ liệu nếu ghi dở.
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, FilePath + ".bak", true);
                else
                    File.Move(temp, FilePath);
            }
        }

        public class ProfileFile
        {
            public List<SessionProfile> Sessions { get; set; }
        }

        // ---------- Lưu kích thước / vị trí cửa sổ ----------

        private static string UiPath
        {
            get { return Path.Combine(FolderPath, "ui.json"); }
        }

        public class UiState
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public bool Maximized { get; set; }
            public int ListWidth { get; set; }
            public int FormHeight { get; set; }
        }

        public static UiState LoadUi()
        {
            try
            {
                if (!File.Exists(UiPath)) return new UiState();
                var serializer = new JavaScriptSerializer();
                var state = serializer.Deserialize<UiState>(File.ReadAllText(UiPath, Encoding.UTF8));
                return state ?? new UiState();
            }
            catch
            {
                return new UiState();
            }
        }

        public static void SaveUi(UiState state)
        {
            if (state == null) return;
            try
            {
                Directory.CreateDirectory(FolderPath);
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(UiPath, serializer.Serialize(state), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        // ---------- Mã hoá mật khẩu bằng DPAPI ----------

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return null;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch
            {
                return null;
            }
        }

        public static string Unprotect(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return null;
            try
            {
                byte[] enc = Convert.FromBase64String(protectedBase64);
                byte[] data = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return null;
            }
        }
    }
}
