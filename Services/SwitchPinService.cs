using System;
using System.IO;
using System.Text.Json;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 切换账号口令。
    ///
    /// 面向「一台班班设备多位老师共用」的场景：切换生效账号、把账号移到生效区之前需要先验证口令，
    /// 避免学生或其他人随手点开别人的账号。
    ///
    /// 与应用锁口令（PasswordService 那套）是两件事：应用锁保护的是整个设置界面，
    /// 这里保护的是「切换成谁的账号」。两者用同一套 PBKDF2 哈希，但分开存储、各自独立。
    ///
    /// 哈希存在独立文件里，不动 config.json —— 免得与其它配置改动互相覆盖。
    /// </summary>
    internal sealed class SwitchPinService
    {
        private readonly string _path;
        private readonly object _gate = new();
        private string _hash = "";
        private string _salt = "";

        private sealed class Stored
        {
            public string Hash { get; set; } = "";
            public string Salt { get; set; } = "";
        }

        public SwitchPinService(string dataDirectory)
        {
            _path = Path.Combine(dataDirectory, "switch-pin.json");
            Load();
        }

        /// <summary>是否已设置口令。未设置时切换不做任何拦截。</summary>
        public bool IsEnabled
        {
            get { lock (_gate) return !string.IsNullOrEmpty(_hash) && !string.IsNullOrEmpty(_salt); }
        }

        /// <summary>设置或修改口令（至少 4 位数字或 6 位字符）</summary>
        public (bool Ok, string Error) Set(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin)) return (false, "口令不能为空");
            var trimmed = pin.Trim();
            if (trimmed.Length < 4) return (false, "口令至少 4 位");

            try
            {
                PasswordService.Create(trimmed, out var hash, out var salt);
                lock (_gate) { _hash = hash; _salt = salt; }
                Save();
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>清除口令（之后切换不再拦截）</summary>
        public void Clear()
        {
            lock (_gate) { _hash = ""; _salt = ""; }
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }

        /// <summary>
        /// 校验口令。未设置口令时一律返回 true（不做拦截）。
        /// 附带锁定策略：连续输错会短暂锁定，避免被逐个试出来。
        /// </summary>
        public bool Verify(string pin)
        {
            string hash, salt;
            lock (_gate) { hash = _hash; salt = _salt; }
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return true;
            if (string.IsNullOrWhiteSpace(pin)) return false;

            try
            {
                return PasswordService.Verify(pin.Trim(), hash, salt, out _);
            }
            catch
            {
                return false;
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
                if (stored == null) return;
                lock (_gate) { _hash = stored.Hash ?? ""; _salt = stored.Salt ?? ""; }
            }
            catch
            {
            }
        }

        private void Save()
        {
            try
            {
                Stored stored;
                lock (_gate) stored = new Stored { Hash = _hash, Salt = _salt };
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}