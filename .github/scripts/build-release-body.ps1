# 生成 GitHub Release 的正文：固定开头（三个产物的说明 + 推荐）＋ CHANGELOG 里对应版本的段落
# 由 .github/workflows/release.yml 调用，输出 RELEASE_BODY.md
$ErrorActionPreference = 'Stop'

$version = (Select-String -Path SeewoAutoLogin.csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$tag = "v$version"
Write-Host "发布版本: $tag"

# 从 CHANGELOG.md 取出 “## vX.Y.Z” 到下一个 “## ” 之间的内容
$notes = ''
$capture = $false
foreach ($line in Get-Content CHANGELOG.md -Encoding UTF8) {
    if ($line -match '^##\s') {
        if ($capture) { break }
        if ($line -like "*$tag*") { $capture = $true; continue }
    }
    if ($capture) { $notes += $line + "`n" }
}
$notes = $notes.Trim()
if (-not $notes) {
    Write-Host '警告：CHANGELOG 中没有找到该版本的段落，使用占位文案'
    $notes = '本次更新内容详见仓库提交记录。'
}

$body = @"
## 三个文件怎么选

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| **SeewoAutoLogin_Setup_$tag.exe** | 约 9 MB | ⭐ **推荐下载这一个**：机器能联网就用它 |
| SeewoAutoLogin_Setup_$tag`_WithWebView2.exe | 约 215 MB | 内网或完全离线的机器 |
| SeewoAutoLogin.exe | 约 37 MB | 免安装的单文件版本 |

三个是同一个程序，区别只在于要不要把 WebView2 运行时一并打包：程序界面依赖它渲染，
而 Windows 10 / 11 通常已自带，所以推荐包只有 9 MB —— 万一系统里没有，程序会自动补装。
只有在完全无法联网的机器上，才需要下载 215 MB 的那个离线版本。

下载完成后，可以对照 ``SHA256SUMS.txt`` 校验文件是否完整。

---

## 本次更新（$tag）

$notes

---

## 关于

- 免费、无广告、无账号体系；账号与密码只以加密形式保存在本机，不上传任何数据
- 完全开源（GPL-3.0），代码、使用说明与问题反馈都在仓库里
- 非希沃官方工具，与希沃及其关联公司无隶属关系
"@

[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'RELEASE_BODY.md'), $body, (New-Object System.Text.UTF8Encoding($false)))
Write-Host '已生成 RELEASE_BODY.md'
