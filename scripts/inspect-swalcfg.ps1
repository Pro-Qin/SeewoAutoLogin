<#
.SYNOPSIS
    检查（并试解）SeewoAutoLogin 的 .swalcfg 配置包。

.DESCRIPTION
    只读操作：不修改原文件、不写任何东西到磁盘。
    用来回答两个问题：这个包结构是否完好？用某个口令能不能解开、里面到底有几个账号？

    包结构（与 Services/ConfigPackageService.cs 保持一致）：
        0..7    ASCII "SWALCFG1"
        8..11   int32 版本号（当前只支持 1）
        12..27  salt(16)
        28..39  nonce(12)
        40..55  tag(16)
        56..     AES-256-GCM 密文（UTF-8 JSON 载荷）
    密钥 = PBKDF2-SHA256(口令, salt, 200000 次, 32 字节)。

.EXAMPLE
    pwsh -File scripts/inspect-swalcfg.ps1 -Path .\SeewoAutoLogin-配置.swalcfg

.EXAMPLE
    pwsh -File scripts/inspect-swalcfg.ps1 -Path .\x.swalcfg -Password 'my-pass' -ShowPassword
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Password,
    [switch]$ShowPassword
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) { Write-Host "文件不存在：$Path" -ForegroundColor Red; exit 1 }

$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
$headerLen = 56
if ($bytes.Length -lt $headerLen) {
    Write-Host "文件只有 $($bytes.Length) 字节，连头部都不完整，已损坏。" -ForegroundColor Red
    exit 1
}

$magic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 8)
if ($magic -ne 'SWALCFG1') {
    Write-Host "不是 SeewoAutoLogin 配置包（头部为 '$magic'）。" -ForegroundColor Red
    exit 1
}

$version = [BitConverter]::ToInt32($bytes, 8)
$salt = [byte[]]($bytes[12..27])
$nonce = [byte[]]($bytes[28..39])
$tag = [byte[]]($bytes[40..55])
$cipher = [byte[]]($bytes[$headerLen..($bytes.Length - 1)])

Write-Host "结构校验：OK" -ForegroundColor Green
Write-Host "  格式      : $magic"
Write-Host "  版本      : $version（当前程序支持 1）"
Write-Host "  文件大小  : $($bytes.Length) 字节（密文 $($cipher.Length) 字节）"

if ($version -ne 1) {
    Write-Host "  版本不是 1，新程序会拒绝导入，需要对应版本的客户端才能读。" -ForegroundColor Yellow
}

if ([string]::IsNullOrEmpty($Password)) {
    Write-Host ""
    Write-Host "未提供口令，只能校验结构。要确认里面的账号是否完好，请加 -Password。" -ForegroundColor Yellow
    exit 0
}

# 解密：口令错会抛 CryptographicException（GCM 校验不过）
$key = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
    $Password, $salt, 200000, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
$plain = [byte[]]::new($cipher.Length)
$aes = [System.Security.Cryptography.AesGcm]::new($key, 16)
try { $aes.Decrypt($nonce, $cipher, $tag, $plain) }
catch [System.Security.Cryptography.CryptographicException] {
    Write-Host ""
    Write-Host "口令不对，或文件内容被改动过（GCM 校验失败）。" -ForegroundColor Red
    exit 2
}
finally { $aes.Dispose() }

$json = [System.Text.Encoding]::UTF8.GetString($plain)
$obj = $json | ConvertFrom-Json

Write-Host ""
Write-Host "解密成功，载荷 $($plain.Length) 字节。" -ForegroundColor Green
Write-Host "  包类型    : $($obj.Kind)"
Write-Host "  导出时间  : $($obj.CreatedAtUtc)"
Write-Host "  导出程序  : $($obj.AppVersion)"
Write-Host ""

$accounts = @($obj.Accounts)
Write-Host "账号 $($accounts.Count) 个：" -ForegroundColor Cyan
$i = 0
foreach ($a in $accounts) {
    $i++
    $pwdLen = if ($null -eq $a.Password) { 0 } else { $a.Password.Length }
    $shown = if ($ShowPassword) { $a.Password } elseif ($pwdLen -gt 0) { "已保存（$pwdLen 字符）" } else { "（空，扫码账号）" }
    Write-Host ("  {0}. {1}" -f $i, $a.Username)
    Write-Host ("     显示名: {0}" -f $a.DisplayName)
    Write-Host ("     密码  : {0}" -f $shown)
    Write-Host ("     标签  : {0}" -f $a.Tags)
}

$settings = $obj.Settings
if ($null -ne $settings) {
    $props = @($settings.PSObject.Properties)
    if ($props.Count -gt 0) {
        Write-Host ""
        Write-Host "设置 $($props.Count) 项：" -ForegroundColor Cyan
        foreach ($p in $props) { Write-Host ("  {0} = {1}" -f $p.Name, $p.Value) }
    }
}

if (-not $ShowPassword) { Write-Host ""; Write-Host "（密码默认隐藏，加 -ShowPassword 可显示明文）" -ForegroundColor DarkGray }
