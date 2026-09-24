using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 凭据有效期观测。
    ///
    /// 希沃签发的凭据（无论密码换来的令牌还是扫码得到的 tokenId）都不告诉我们有效期。
    /// 但每次自动续期都是一次实测：成功了说明「这个年龄的凭据还能用」，
    /// 失败了说明「这个年龄已经过期」。把两侧的观测记下来，就能夹出真实有效期的范围，
    /// 从而回答两个实际问题：程序该多久续期一次，以及关机多久之内还能自动救回来。
    ///
    /// 数据只写在本机，不参与任何上传。
    /// </summary>
    internal sealed class CredentialLifetimeTracker
    {
        /// <summary>单条观测</summary>
        internal sealed class Sample
        {
            public DateTimeOffset AtUtc { get; set; }
            /// <summary>true=扫码凭据，false=密码账号</summary>
            public bool IsQr { get; set; }
            /// <summary>续期时凭据已经存在的小时数</summary>
            public double AgeHours { get; set; }
            public bool Success { get; set; }
        }

        private const int MaxSamples = 400;
        private readonly string _path;
        private readonly object _gate = new();
        private List<Sample> _samples = new();

        public CredentialLifetimeTracker(string dataDirectory)
        {
            _path = Path.Combine(dataDirectory, "credential-lifetime.json");
            Load();
        }

        /// <summary>记录一次续期观测</summary>
        public void Record(bool isQr, double ageHours, bool success)
        {
            try
            {
                lock (_gate)
                {
                    _samples.Add(new Sample
                    {
                        AtUtc = DateTimeOffset.UtcNow,
                        IsQr = isQr,
                        AgeHours = Math.Max(0, ageHours),
                        Success = success,
                    });

                    if (_samples.Count > MaxSamples)
                        _samples = _samples.OrderByDescending(s => s.AtUtc).Take(MaxSamples).ToList();
                }
                Save();
            }
            catch
            {
                // 观测失败不影响主流程
            }
        }

        /// <summary>
        /// 推算有效期区间：已知能续期的最大年龄、已知失败的最小年龄。
        /// 两者都为空表示样本还不足。
        /// </summary>
        public (double? SafeHours, double? FailedHours, int SampleCount) GetBounds()
        {
            lock (_gate)
            {
                var ok = _samples.Where(s => s.Success).ToList();
                var bad = _samples.Where(s => !s.Success).ToList();
                return (
                    ok.Count > 0 ? ok.Max(s => s.AgeHours) : null,
                    bad.Count > 0 ? bad.Min(s => s.AgeHours) : null,
                    _samples.Count);
            }
        }

        /// <summary>生成一句给日志/界面看的中文摘要</summary>
        public string DescribeBounds()
        {
            var (safe, failed, count) = GetBounds();
            if (count < 3) return $"观测样本不足（{count} 条）";

            var parts = new List<string>();
            if (safe.HasValue) parts.Add($"{safe.Value:F1} 小时内均可续期");
            if (failed.HasValue) parts.Add($"{failed.Value:F1} 小时时已失效");
            if (parts.Count == 0) return "暂无结论";

            var text = "凭据有效期观测：" + string.Join("，", parts) + $"（样本 {count} 条）";
            if (safe.HasValue && failed.HasValue && failed.Value <= safe.Value)
                text += "；区间倒挂，可能是网络原因导致的续期失败，结论仅供参考";
            return text;
        }

        /// <summary>
        /// 关机前是否值得提醒用户「记得开一次程序」：
        /// 只有当我们已经知道凭据在某个年龄会失效时才有意义。
        /// </summary>
        public bool ShouldWarnBeforeShutdown(TimeSpan offlineForecast)
        {
            var (safe, failed, _) = GetBounds();
            if (!failed.HasValue || !safe.HasValue) return false;
            // 预计离线时长已经逼近「已知会失效」的年龄，就值得提一句
            return offlineForecast.TotalHours + safe.Value >= failed.Value * 0.8;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                _samples = JsonSerializer.Deserialize<List<Sample>>(json) ?? new List<Sample>();
            }
            catch
            {
                _samples = new List<Sample>();
            }
        }

        private void Save()
        {
            try
            {
                List<Sample> snapshot;
                lock (_gate) snapshot = _samples.ToList();
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}