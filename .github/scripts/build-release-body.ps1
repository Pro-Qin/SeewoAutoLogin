# 生成 GitHub Release 的正文（Windows 7 兼容分支）：
# 固定开头（产物怎么选 + Win7 前提）＋ CHANGELOG 里对应版本的段落
# 由 .github/workflows/release.yml 调用，输出 RELEASE_BODY.md
$ErrorActionPreference = 'Stop'

$version = (Select-String -Path SeewoAutoLogin.csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$tag = "v$version-win7"
Write-Host "发布版本: $tag"

# 从 CHANGELOG.md 取出 “## vX.Y.Z” 到下一个 “## ” 之间的内容（CHANGELOG 按主干版本号分段）
$notes = ''
$capture = $false
foreach ($line in Get-Content CHANGELOG.md -Encoding UTF8) {
    if ($line -match '^##\s') {
        if ($capture) { break }
        if ($line -like "*v$version*") { $capture = $true; continue }
    }
    if ($capture) { $notes += $line + "`n" }
}
$notes = $notes.Trim()
if (-not $notes) {
    Write-Host '警告：CHANGELOG 中没有找到该版本的段落，使用占位文案'
    $notes = '本次更新内容详见仓库提交记录。'
}

$body = @"
## ⚠️ 这是 Windows 7 兼容分支的构建

主干版本要求 Windows 10 19041 及以上；这一个是为了让 **Windows 7 SP1** 也能用，
代价是目标框架退到 .NET Framework 4.8，并且不能自动升级到主干版本（两者互不兼容）。

### 运行前提（缺一不可）

| 前提 | 说明 |
| --- | --- |
| Windows 7 **SP1** 64 位 | 32 位系统不支持；Win10/11 请改用主干版本 |
| .NET Framework 4.8 | Win7 默认不带；安装包会检测并提示下载，装完再运行安装包 |
| WebView2 运行时 109.0.1518.78 | Win7 上能用的最后一版；110 之后微软已不支持 Win7，装新版界面会白屏 |

## 三个文件怎么选

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| **SeewoAutoLogin_Setup_$tag.exe** | 约 21 MB | ⭐ **推荐**：机器能联网就用它，缺 WebView2 时程序会引导安装 |
| SeewoAutoLogin_Setup_$tag`_WithWebView2.exe | 约 230 MB | 内网或完全离线的机器（内置 109 版运行时） |
| SeewoAutoLogin_v$version`_Win7_Portable.zip | 约 8 MB | 免安装：**解压整个目录**后运行里面的 exe（.NET Framework 版不是单文件） |

三个是同一个程序。注意免安装版必须保留压缩包里的全部文件，
只把 exe 单独拷出来会缺依赖 DLL，启动即失败。

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
