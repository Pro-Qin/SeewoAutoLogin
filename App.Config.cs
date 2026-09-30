using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SeewoAutoLogin.Services;

namespace SeewoAutoLogin
{
    public partial class App
    {
        #region Config Persistence

        /// <summary>配置与日志的并发保护：UI 线程与网关 HTTP 线程都会读写配置/写日志</summary>
        private static readonly object ConfigIoLock = new object();
        private static readonly object LogIoLock = new object();
        private const int LogRotationBytes = 5 * 1024 * 1024;
        private const int LogRetentionDays = 14;
        private static DateTime _lastLogCleanupDate = DateTime.MinValue;

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeewoAutoLogin");

        private string ConfigPath => Path.Combine(AppDataDir, "config.json");

        /// <summary>数据目录（%LOCALAPPDATA%\SeewoAutoLogin）：供诊断包导出等外部功能读取日志与配置</summary>
        internal static string DataDirectory => AppDataDir;

        public void LoadConfig()
        {
            lock (ConfigIoLock)
            {
                var loaded = TryLoadConfigFile(ConfigPath)
                             ?? TryLoadConfigFile(ConfigPath + ".bak")
                             ?? TryLoadLatestBackup();

                if (loaded == null)
                {
                    _config = new PluginConfig();
                    return;
                }

                loaded.Accounts ??= new List<SeewoAccount>();
                loaded.Accounts.RemoveAll(a => a == null);
                _config = loaded;

                // 启动时迁移旧配置：明文/旧格式密码统一转为带前缀的 DPAPI 密文
                try
                {
                    if (EnsurePasswordsEncrypted()) SaveConfig();
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Config] 密码加密失败（配置未写入明文）: {ex.Message}");
                }
            }
        }

        private PluginConfig TryLoadConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null) WriteDiagnosticLog($"[Config] 配置解析结果为空: {path}");
                return loaded;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 配置读取失败({Path.GetFileName(path)}): {ex.Message}");
                return null;
            }
        }

        private PluginConfig TryLoadLatestBackup()
        {
            try
            {
                var latest = Services.ConfigBackupService.List().FirstOrDefault();
                if (latest == null) return null;
                if (!Services.ConfigBackupService.TryRead(latest.Name, out var json, out _)) return null;
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded != null)
                    WriteDiagnosticLog($"[Config] 主配置与 .bak 均不可用，已回退到备份 {latest.Name}");
                return loaded;
            }
            catch { return null; }
        }

        public void SaveConfig()
        {
            lock (ConfigIoLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // 加密明文/旧格式密码；加密失败会抛异常，由外层 catch 中止本次保存，绝不把明文写盘
                    EnsurePasswordsEncrypted();

                    var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });

                    // 原子写：先写临时文件，再替换，避免进程中断留下半截 JSON（会导致账号全部丢失）
                    var temp = ConfigPath + ".tmp";
                    File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                    if (File.Exists(ConfigPath))
                    {
                        try { File.Replace(temp, ConfigPath, ConfigPath + ".bak", ignoreMetadataErrors: true); }
                        catch
                        {
                            File.Copy(temp, ConfigPath, overwrite: true);
                            try { File.Delete(temp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(temp, ConfigPath);
                    }

                    Services.ConfigBackupService.Archive(json);
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"保存配置失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 将账号密码统一转为带前缀的 DPAPI 密文（兼容旧版无前缀密文与历史明文）。
        /// 加密失败抛出异常，由调用方中止保存，避免明文落盘。
        /// </summary>
        private bool EnsurePasswordsEncrypted()
        {
            bool changed = false;
            foreach (var acct in _config.Accounts)
            {
                if (string.IsNullOrEmpty(acct.Password)) continue;
                if (Services.SecureStore.IsV2Encrypted(acct.Password)) continue;

                string plaintext;
                if (Services.SecureStore.IsLegacyEncrypted(acct.Password))
                {
                    // 旧 dpapi: 格式：先解出明文，再用 v2 随机主密钥重新加密
                    plaintext = Services.SecureStore.Decrypt(acct.Password);
                }
                else
                {
                    // 旧版无前缀：可能是 DPAPI 密文，也可能是明文；先尝试解密
                    plaintext = Services.SecureStore.TryDecryptLegacy(acct.Password) ?? acct.Password;
                }

                acct.Password = Services.SecureStore.Encrypt(plaintext); // 失败抛异常，中止保存
                changed = true;
            }
            if (changed) _config.PasswordEncrypted = true;
            return changed;
        }

        /// <summary>
        /// 卸载清理：移除 hosts 中的 local.id.seewo.com 映射，并删除本应用数据目录。
        /// </summary>
        private void CleanupForUninstall()
        {
            // 1) 结束开机自启（计划任务 + 注册表启动项），否则会残留指向已删除 exe 的启动项
            try
            {
                if (Services.AutoStartService.Disable(out var autoStartError))
                    WriteDiagnosticLog("[Uninstall] 已移除开机自启");
                else
                    WriteDiagnosticLog($"[Uninstall] 移除开机自启失败: {autoStartError}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 移除开机自启异常: {ex.Message}");
            }

            // 2) hosts：只删除本程序写入的行（带标记或精确匹配的旧行），按原编码原子写回
            try
            {
                if (Services.HostsFileService.RemoveLoopbackMapping(out var hostsError))
                    WriteDiagnosticLog($"[Uninstall] 已移除 hosts 中的 {Services.HostsFileService.HostName} 映射（原始备份：{Services.HostsFileService.BackupPath}）");
                else
                    WriteDiagnosticLog($"[Uninstall] 清理 hosts 失败（可能需要管理员权限）: {hostsError}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 清理 hosts 异常: {ex.Message}");
            }

            // 3) 删除数据目录
            try
            {
                if (Directory.Exists(AppDataDir))
                {
                    Directory.Delete(AppDataDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Uninstall] 删除数据目录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载窗口图标（嵌入资源里的 app.ico）。
        /// 窗口若不显式设置 Icon，只会沿用 exe 图标并受 Windows 图标缓存影响，
        /// 换图标后任务栏仍显示旧图标，所以这里直接给出图像。
        /// </summary>
        internal static System.Windows.Media.Imaging.BitmapSource? LoadWindowIcon()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return null;

                // 关键：ico 里打包了 16~256 多个尺寸，直接交给 BitmapImage 会取到第一帧（通常是 16px），
                // 任务栏和 Alt+Tab 再把它放大，于是图标又小又糊。这里按尺寸挑一帧合适的。
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                var frame = decoder.Frames
                    .Where(f => f.PixelWidth <= 64)          // 32/48 足够任务栏使用
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault()
                    ?? decoder.Frames.OrderBy(f => f.PixelWidth).First();   // 兜底取最小帧

                frame.Freeze();
                return frame;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取用户协议正文（嵌入资源）。欢迎界面的协议页与「关于」页的弹窗共用这一份，
        /// 保证两处内容永远一致。
        /// </summary>
        internal static string LoadTermsText()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("Resources.terms.txt", StringComparison.Ordinal));
                if (name == null) return "";

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) return "";
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 恢复出厂设置：清空账号、扫码凭据、配置与备份，回到首次安装状态。
        /// 日志保留（便于排障），下次启动会重新走欢迎界面。
        /// </summary>
        internal void FactoryReset()
        {
            try { _authService?.Logout(); } catch { }

            foreach (var folder in new[] { "Sessions", "Backups" })
            {
                try
                {
                    var path = Path.Combine(AppDataDir, folder);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog($"[Reset] 清理 {folder} 失败: {ex.Message}");
                }
            }

            lock (ConfigIoLock)
            {
                _config = new PluginConfig();
            }
            SaveConfig();
            WriteDiagnosticLog("[Reset] 已恢复出厂设置（账号、设置与备份均已清空）");
        }

        /// <summary>列出配置备份（供设置页「配置备份」区域展示）</summary>
        internal List<Services.ConfigBackupService.BackupItem> ListConfigBackups()
            => Services.ConfigBackupService.List();

        /// <summary>从指定备份恢复配置</summary>
        internal bool RestoreConfigBackup(string name, out string error)
        {
            error = null;
            if (!Services.ConfigBackupService.TryRead(name, out var json, out error)) return false;
            try
            {
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null)
                {
                    error = "备份内容无法解析";
                    return false;
                }
                loaded.Accounts ??= new List<SeewoAccount>();
                loaded.Accounts.RemoveAll(a => a == null);
                lock (ConfigIoLock) { _config = loaded; }
                SaveConfig();
                WriteDiagnosticLog($"[Config] 已从备份 {name} 恢复配置（{_config.Accounts.Count} 个账号）");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                WriteDiagnosticLog($"[Config] 恢复备份失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出配置到文件
        /// </summary>
        internal void ExportConfig(string filePath)
        {
            try
            {
                // 导出前同样做加密迁移：避免历史遗留的明文密码被写进导出文件
                lock (ConfigIoLock)
                {
                    EnsurePasswordsEncrypted();
                    var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json);
                }
                WriteDiagnosticLog($"[Config] 配置已导出到 {filePath}");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 导出失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 从文件导入配置
        /// </summary>
        internal bool ImportConfig(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<PluginConfig>(json);
                if (loaded == null) return false;
                loaded.Accounts ??= new List<SeewoAccount>();
                // 校验导入内容：剔除空账号与非法 Id（Id 会进入前端 DOM 与 SSO 请求路径）
                loaded.Accounts.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 64);
                foreach (var account in loaded.Accounts)
                {
                    account.HealthState = "";
                    account.HealthMessage = "";
                }
                lock (ConfigIoLock) { _config = loaded; }
                SaveConfig();
                WriteDiagnosticLog($"[Config] 已从 {filePath} 导入配置 ({_config.Accounts.Count} 个账号)");
                return true;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"[Config] 导入失败: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
