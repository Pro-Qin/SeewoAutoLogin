namespace SeewoAutoLogin.Services
{
    /// <summary>
    /// 后台静默更新对外的一份状态快照。
    ///
    /// 界面需要它做两件事：把手动点「下载新版本」和后台任务对齐（接手进度，还是掐断重下），
    /// 以及把后台的进度显示到前台。快照是值拷贝，读它不会跟下载线程抢锁。
    /// </summary>
    internal sealed class SilentUpdateSnapshot
    {
        /// <summary>idle / downloading / ready / installing</summary>
        public string Stage { get; set; } = "idle";

        /// <summary>给界面看的说明文本；下载阶段前台按 received/total 自己算百分比，不读这里。</summary>
        public string Text { get; set; } = "";

        public string Version { get; set; } = "";

        public long Received { get; set; }

        public long Total { get; set; }

        public double? Percent { get; set; }

        /// <summary>最近几秒的瞬时下载速率（字节/秒）。</summary>
        public double BytesPerSecond { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public bool IsDownloading => Stage == "downloading";

        public SilentUpdateSnapshot Clone() => (SilentUpdateSnapshot)MemberwiseClone();
    }
}
