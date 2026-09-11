using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;

namespace SeewoAutoLogin
{
    public partial class DiagnosticWindow : Window
    {
        private readonly App _app;
        private readonly List<DiagItem> _items = new List<DiagItem>();

        public DiagnosticWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            DiagnosticResults.ItemsSource = _items;
        }

        private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            RunDiagnosticBtn.IsEnabled = false;
            _items.Clear();
            DiagnosticResults.Items.Refresh();

            await System.Threading.Tasks.Task.Run(() => RunAllChecks());

            DiagnosticResults.Items.Refresh();
            RunDiagnosticBtn.IsEnabled = true;

            var pass = _items.Count(i => i.Status == "pass");
            var fail = _items.Count(i => i.Status == "fail");
            SummaryText.Text = $"{pass}/{_items.Count} 项通过，{fail} 项异常";
        }

        private void RunAllChecks()
        {
            // 1. 网络连接
            CheckNetwork("id.seewo.com");
            CheckNetwork("edu.seewo.com");

            // 2. hosts 映射
            CheckHostsMapping();

            // 3. SSO 网关端口
            CheckGatewayPort();

            // 4. 账号
            CheckAccounts();

            // 5. 日志
            CheckLogs();

            // 6. WebView2 运行时（主界面依赖）
            CheckWebView2();
        }

        private void AddResult(string icon, string title, string detail, string status)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _items.Add(new DiagItem { Icon = icon, Title = title, Detail = detail, Status = status });
            });
        }

        private void CheckNetwork(string host)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(host, 3000);
                if (reply.Status == IPStatus.Success)
                    AddResult("✅", $"访问 {host}", $"延迟 {reply.RoundtripTime}ms", "pass");
                else
                    AddResult("❌", $"访问 {host}", "无法连通，请检查网络/代理", "fail");
            }
            catch
            {
                AddResult("⚠️", $"访问 {host}", "检测超时或被拦截", "fail");
            }
        }

        private void CheckHostsMapping()
        {
            try
            {
                var hostsPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "drivers", "etc", "hosts");
                if (System.IO.File.Exists(hostsPath))
                {
                    var content = System.IO.File.ReadAllText(hostsPath);
                    if (content.Contains("local.id.seewo.com"))
                        AddResult("✅", "Hosts 映射", "local.id.seewo.com → 127.0.0.1 已配置", "pass");
                    else
                        AddResult("❌", "Hosts 映射", "local.id.seewo.com 未配置（需管理员权限）", "fail");
                }
                else
                    AddResult("⚠️", "Hosts 文件", "hosts 文件不存在", "fail");
            }
            catch (Exception ex)
            {
                AddResult("❌", "Hosts 映射", $"读取失败: {ex.Message}", "fail");
            }
        }

        private void CheckGatewayPort()
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect("127.0.0.1", 24300, null, null);
                if (result.AsyncWaitHandle.WaitOne(1000))
                {
                    client.EndConnect(result);
                    AddResult("✅", "SSO 网关端口", "24300 端口已开放", "pass");
                }
                else
                    AddResult("❌", "SSO 网关端口", "24300 端口未开放，网关可能未启动", "fail");
            }
            catch
            {
                AddResult("❌", "SSO 网关端口", "24300 端口无响应", "fail");
            }
        }

        private void CheckAccounts()
        {
            var count = _app.Config.Accounts.Count;
            if (count > 0)
            {
                var active = _app.Config.Accounts.FirstOrDefault(a => a.Id == _app.Config.ActiveAccountId);
                var activeName = active?.DisplayName ?? active?.Username ?? "(无)";
                AddResult("✅", $"账号配置 ({count} 个)", $"当前账号: {activeName}", "pass");
            }
            else
                AddResult("⚠️", "账号配置", "暂无账号，SSO 网关无法提供登录", "fail");
        }

        private void CheckWebView2()
        {
            try
            {
                var version = SeewoAutoLogin.Services.WebView2Runtime.GetInstalledVersion();
                if (!string.IsNullOrWhiteSpace(version))
                    AddResult("✅", "WebView2 运行时", $"版本 {version}", "pass");
                else
                    AddResult("❌", "WebView2 运行时", "未安装（主界面会自动静默安装，失败时可手动安装）", "fail");

                var folder = SeewoAutoLogin.Services.WebView2Runtime.UserDataFolder;
                System.IO.Directory.CreateDirectory(folder);
                AddResult("✅", "WebView2 数据目录", folder, "pass");
            }
            catch (Exception ex)
            {
                AddResult("⚠️", "WebView2 运行时", $"检测失败: {ex.Message}", "fail");
            }
        }

        private void CheckLogs()
        {
            try
            {
                var logsDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeewoAutoLogin", "Logs");
                var todayLog = System.IO.Path.Combine(logsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (System.IO.File.Exists(todayLog))
                {
                    var lines = System.IO.File.ReadAllLines(todayLog);
                    AddResult("✅", "诊断日志", $"今日日志 {lines.Length} 行", "pass");
                }
                else
                    AddResult("✅", "诊断日志", "今日暂无日志", "pass");
            }
            catch (Exception ex)
            {
                AddResult("⚠️", "诊断日志", $"读取失败: {ex.Message}", "fail");
            }
        }
    }

    public class DiagItem
    {
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Status { get; set; }
    }
}
