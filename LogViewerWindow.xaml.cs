using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SeewoAutoLogin
{
    /// <summary>
    /// 诊断日志查看器：按等级染色、按日期/等级/模块/关键字筛选、可调显示行数、随时导出。
    ///
    /// 打开即滚到最底：用户点"查看日志"九成是为了看刚刚发生的事，让他先手动拖到底太蠢。
    /// 但自动刷新时会保留滚动位置 —— 正在往上翻旧记录的人不该被刷新弹回底部。
    /// </summary>
    public partial class LogViewerWindow : Window
    {
        private sealed class LogLine
        {
            public string TimeText { get; set; } = "";
            public string LevelText { get; set; } = "";
            public Brush LevelBrush { get; set; }
            public string ModuleText { get; set; } = "";
            public string BodyText { get; set; } = "";
            public Brush BodyBrush { get; set; }
            public LogLevel Level { get; set; }
            public string Module { get; set; } = "";
        }

        private sealed class LogFileItem
        {
            public string Path { get; set; } = "";
            public DateTime Date { get; set; }

            public override string ToString()
            {
                var today = DateTime.Today;
                if (Date == today) return Date.ToString("yyyy-MM-dd") + "（今天）";
                if (Date == today.AddDays(-1)) return Date.ToString("yyyy-MM-dd") + "（昨天）";
                return Date.ToString("yyyy-MM-dd");
            }
        }

        private const string AllModules = "全部模块";
        private const int MaxParseLines = 20000;
        private const long MaxReadBytes = 4 * 1024 * 1024;
        private const long TailReadBytes = 2 * 1024 * 1024;

        private static readonly Regex LinePattern = new Regex(
            @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]\s*\[(?<level>INFO|WARN|ERROR|DEBUG)\]\s?(?<rest>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex ModulePattern = new Regex(@"^\[(?<module>[^\]]+)\]\s?", RegexOptions.Compiled);

        private static readonly Brush DebugBrush = MakeBrush("#8A8A8A");
        private static readonly Brush InfoBrush = MakeBrush("#4FC1FF");
        private static readonly Brush WarnBrush = MakeBrush("#F0B429");
        private static readonly Brush ErrorBrush = MakeBrush("#FF5C5C");
        private static readonly Brush NormalBodyBrush = MakeBrush("#D4D4D4");

        private readonly List<LogLine> _allLines = new List<LogLine>();
        private readonly DispatcherTimer _autoRefreshTimer;

        private string _currentPath = "";
        private DateTime _currentStamp = DateTime.MinValue;
        private long _currentLength = -1;
        private bool _initialized;
        private bool _updatingOptions;

        public LogViewerWindow()
        {
            InitializeComponent();

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _autoRefreshTimer.Tick += (_, __) => ReloadIfChanged();

            LevelCombo.SelectedIndex = 0;
            RowLimitCombo.SelectedIndex = 1;   // 默认 500 行：再多也就看不清了

            PopulateFiles();
            _initialized = true;
            LoadCurrentFile(scrollToEnd: true);
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoRefreshTimer.Stop();
            base.OnClosed(e);
        }

        // ===== 加载与解析 =====

        private static string LogsDirectory()
        {
            return Path.Combine(App.DataDirectory, "Logs");
        }

        private void PopulateFiles()
        {
            _updatingOptions = true;
            try
            {
                var items = new List<LogFileItem>();
                var dir = LogsDirectory();
                if (Directory.Exists(dir))
                {
                    foreach (var file in new DirectoryInfo(dir).GetFiles("*.log"))
                    {
                        var date = ParseLogFileDate(file.Name);
                        if (date == null) continue;
                        items.Add(new LogFileItem { Path = file.FullName, Date = date.Value });
                    }
                }

                // 今天的日志可能还没落盘（刚启动就点开）：补一个占位项，日期下拉不能是空的
                var today = DateTime.Today;
                if (!items.Any(i => i.Date == today))
                {
                    items.Add(new LogFileItem
                    {
                        Path = System.IO.Path.Combine(dir, today.ToString("yyyy-MM-dd") + ".log"),
                        Date = today
                    });
                }

                var ordered = items.OrderByDescending(i => i.Date).ToList();
                DateCombo.ItemsSource = ordered;
                DateCombo.SelectedIndex = 0;
            }
            catch
            {
                DateCombo.ItemsSource = new List<LogFileItem>();
            }
            finally
            {
                _updatingOptions = false;
            }
        }

        /// <summary>日志文件名就是日期（yyyy-MM-dd.log），滚动归档后带后缀，取前 10 个字符即可。</summary>
        private static DateTime? ParseLogFileDate(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length < 10) return null;
            return DateTime.TryParseExact(fileName.Substring(0, 10), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)
                ? date
                : (DateTime?)null;
        }

        private void LoadCurrentFile(bool scrollToEnd)
        {
            if (!(DateCombo.SelectedItem is LogFileItem item)) return;
            _currentPath = item.Path;

            ParseLines(ReadLogText(_currentPath));
            RefreshModuleOptions();
            ApplyFilter(scrollToEnd);

            try
            {
                var info = new FileInfo(_currentPath);
                _currentStamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                _currentLength = info.Exists ? info.Length : -1;
            }
            catch
            {
                _currentStamp = DateTime.MinValue;
                _currentLength = -1;
            }
        }

        private static string ReadLogText(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var offset = 0L;
                    // 日志写得很大时只读尾部：打开窗口不该因为几天的日志卡住，
                    // 而排查问题绝大多数时候只看最近的。
                    if (stream.Length > MaxReadBytes)
                        offset = Math.Max(0, stream.Length - TailReadBytes);

                    stream.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[stream.Length - offset];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    if (offset == 0) return text;

                    // 从半行开始解码会得到一条野记录，丢掉第一行
                    var newline = text.IndexOf('\n');
                    return newline >= 0 ? text.Substring(newline + 1) : text;
                }
            }
            catch
            {
                return "";
            }
        }

        private void ParseLines(string text)
        {
            _allLines.Clear();
            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Split('\n');
            var start = Math.Max(0, lines.Length - MaxParseLines);
            LogLine current = null;

            for (var i = start; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd('\r');
                if (raw.Length == 0) continue;

                var match = LinePattern.Match(raw);
                if (!match.Success)
                {
                    // 栈回溯、多行异常信息没有时间戳：并进上一条，别让它变成独立的"未知"记录
                    if (current != null) current.BodyText += "\n    " + raw;
                    continue;
                }

                var level = ParseLevel(match.Groups["level"].Value);
                var rest = match.Groups["rest"].Value;
                var module = "";
                var moduleMatch = ModulePattern.Match(rest);
                if (moduleMatch.Success)
                {
                    module = moduleMatch.Groups["module"].Value;
                    rest = rest.Substring(moduleMatch.Length);
                }

                current = new LogLine
                {
                    TimeText = match.Groups["time"].Value + " ",
                    LevelText = NameOf(level).PadRight(5) + " ",
                    LevelBrush = BrushFor(level),
                    ModuleText = module.Length > 0 ? "[" + module + "] " : "",
                    BodyText = rest,
                    BodyBrush = NormalBodyBrush,
                    Level = level,
                    Module = module
                };
                _allLines.Add(current);
            }
        }

        private static LogLevel ParseLevel(string text)
        {
            switch (text)
            {
                case "ERROR": return LogLevel.Error;
                case "WARN": return LogLevel.Warn;
                case "DEBUG": return LogLevel.Debug;
                default: return LogLevel.Info;
            }
        }

        private static string NameOf(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "ERROR";
                case LogLevel.Warn: return "WARN";
                case LogLevel.Debug: return "DEBUG";
                default: return "INFO";
            }
        }

        private static Brush BrushFor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return ErrorBrush;
                case LogLevel.Warn: return WarnBrush;
                case LogLevel.Debug: return DebugBrush;
                default: return InfoBrush;
            }
        }

        private static Brush MakeBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // ===== 筛选与渲染 =====

        private void RefreshModuleOptions()
        {
            var previous = ModuleCombo.SelectedItem as string;
            var modules = _allLines
                .Select(l => l.Module)
                .Where(m => m.Length > 0)
                .Distinct()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
            modules.Insert(0, AllModules);

            _updatingOptions = true;
            try
            {
                ModuleCombo.ItemsSource = modules;
                var index = previous != null ? modules.IndexOf(previous) : 0;
                ModuleCombo.SelectedIndex = index < 0 ? 0 : index;
            }
            finally
            {
                _updatingOptions = false;
            }
        }

        private void ApplyFilter(bool scrollToEnd)
        {
            var levelFilter = LevelCombo.SelectedIndex;
            var moduleFilter = ModuleCombo.SelectedItem as string;
            var search = (SearchBox.Text ?? "").Trim();

            var matched = new List<LogLine>();
            foreach (var line in _allLines)
            {
                switch (levelFilter)
                {
                    case 1: if (line.Level < LogLevel.Warn) continue; break;   // 问题 = 警告 + 错误
                    case 2: if (line.Level != LogLevel.Error) continue; break;
                    case 3: if (line.Level != LogLevel.Warn) continue; break;
                    case 4: if (line.Level != LogLevel.Info) continue; break;
                    case 5: if (line.Level != LogLevel.Debug) continue; break;
                }

                if (!string.IsNullOrEmpty(moduleFilter) && moduleFilter != AllModules
                    && !string.Equals(line.Module, moduleFilter, StringComparison.Ordinal))
                    continue;

                if (search.Length > 0
                    && line.BodyText.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                    && line.Module.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                matched.Add(line);
            }

            var limit = SelectedRowLimit();
            List<LogLine> shown = matched;
            if (limit > 0 && matched.Count > limit)
                shown = matched.GetRange(matched.Count - limit, limit);   // 留最新的 N 行

            LogList.ItemsSource = shown;
            UpdateStatus(matched.Count, shown.Count);
            if (scrollToEnd) ScrollToEndAfterLayout();
        }

        private int SelectedRowLimit()
        {
            switch (RowLimitCombo.SelectedIndex)
            {
                case 0: return 200;
                case 2: return 1000;
                case 3: return 2000;
                case 4: return 5000;
                case 5: return 0;          // 全部
                default: return 500;
            }
        }

        private void UpdateStatus(int matched, int shown)
        {
            var file = string.IsNullOrEmpty(_currentPath) ? "（未选择）" : System.IO.Path.GetFileName(_currentPath);
            var parts = new List<string> { "文件 " + file, "共解析 " + _allLines.Count + " 行" };
            if (matched != shown) parts.Add("匹配 " + matched + " 行");
            parts.Add("显示 " + shown + " 行");
            if (AutoRefreshCheck.IsChecked == true) parts.Add("自动刷新中（3 秒）");
            LogStatus.Text = string.Join(" · ", parts);
        }

        private void ScrollToEndAfterLayout()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (LogList.Items.Count == 0) return;
                    LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        private ScrollViewer FindScrollViewer()
        {
            return FindVisualChild<ScrollViewer>(LogList);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private bool IsNearBottom()
        {
            var viewer = FindScrollViewer();
            if (viewer == null) return true;
            return viewer.VerticalOffset >= viewer.ScrollableHeight - 24;
        }

        private void RestoreOffset(double offset)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var viewer = FindScrollViewer();
                if (viewer != null) viewer.ScrollToVerticalOffset(offset);
            }), DispatcherPriority.Loaded);
        }

        // ===== 自动刷新 =====

        private void ReloadIfChanged()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;

            long length;
            DateTime stamp;
            try
            {
                var info = new FileInfo(_currentPath);
                if (!info.Exists) return;
                length = info.Length;
                stamp = info.LastWriteTimeUtc;
            }
            catch
            {
                return;
            }

            if (length == _currentLength && stamp == _currentStamp) return;

            var atBottom = IsNearBottom();
            var offset = FindScrollViewer()?.VerticalOffset ?? 0;
            LoadCurrentFile(scrollToEnd: atBottom);
            // 正在往上翻旧记录时把滚动条放回原处，刷新不该把人弹走
            if (!atBottom) RestoreOffset(offset);
        }

        // ===== 交互 =====

        private void DateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || _updatingOptions) return;
            LoadCurrentFile(scrollToEnd: true);
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized || _updatingOptions) return;
            ApplyFilter(scrollToEnd: false);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized || _updatingOptions) return;
            ApplyFilter(scrollToEnd: false);
        }

        private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (_autoRefreshTimer == null) return;
            if (AutoRefreshCheck.IsChecked == true) _autoRefreshTimer.Start();
            else _autoRefreshTimer.Stop();
            if (_initialized) UpdateStatus(_allLines.Count, LogList.Items.Count);
        }

        private void RefreshLog_Click(object sender, RoutedEventArgs e)
        {
            var atBottom = IsNearBottom();
            var offset = FindScrollViewer()?.VerticalOffset ?? 0;
            LoadCurrentFile(scrollToEnd: atBottom);
            if (!atBottom) RestoreOffset(offset);
        }

        private async void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            string desktop;
            try { desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            catch { desktop = ""; }

            var dialog = new SaveFileDialog
            {
                Title = "导出日志",
                FileName = LogExportService.DefaultFileName(),
                DefaultExt = ".txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                InitialDirectory = desktop
            };
            if (dialog.ShowDialog(this) != true) return;

            ExportLogBtn.IsEnabled = false;
            LogStatus.Text = "正在导出日志…";
            try
            {
                var path = dialog.FileName;
                await Task.Run(() => LogExportService.Export(path, App.DataDirectory, ManagementWindow.AppVersion, null));
                LogStatus.Text = "已导出到 " + path;
                (Application.Current as App)?.WriteDiagnosticLog("[LogViewer] 日志已导出到 " + path);
            }
            catch (Exception ex)
            {
                LogStatus.Text = "导出失败：" + ex.Message;
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出日志", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ExportLogBtn.IsEnabled = true;
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            var name = System.IO.Path.GetFileName(_currentPath);
            if (!File.Exists(_currentPath))
            {
                LogStatus.Text = "日志文件不存在：" + name;
                return;
            }

            var result = MessageBox.Show(this, "确定要清空 " + name + " 的全部内容？此操作无法撤销。",
                "清空日志", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            try
            {
                using (new FileStream(_currentPath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite)) { }
                LoadCurrentFile(scrollToEnd: false);
                (Application.Current as App)?.WriteDiagnosticLog("[LogViewer] 用户清空了日志文件 " + name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "清空失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopySelected_Click(object sender, RoutedEventArgs e)
        {
            var lines = LogList.SelectedItems.OfType<LogLine>().ToList();
            CopyLines(lines, "已复制选中行");
        }

        private void CopyVisible_Click(object sender, RoutedEventArgs e)
        {
            var lines = (LogList.ItemsSource as List<LogLine>) ?? new List<LogLine>();
            CopyLines(lines, "已复制显示的全部行");
        }

        private void CopyLines(List<LogLine> lines, string what)
        {
            if (lines.Count == 0)
            {
                LogStatus.Text = "没有可复制的内容";
                return;
            }

            var text = string.Join(Environment.NewLine, lines.Select(l =>
                "[" + l.TimeText.Trim() + "] [" + l.LevelText.Trim() + "] " + l.ModuleText + l.BodyText));
            try
            {
                Clipboard.SetText(text);
                LogStatus.Text = what + "（" + lines.Count + " 行）";
            }
            catch (Exception ex)
            {
                LogStatus.Text = "复制失败：" + ex.Message;
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(string.IsNullOrEmpty(_currentPath) ? LogsDirectory() : _currentPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogStatus.Text = "打开目录失败：" + ex.Message;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
