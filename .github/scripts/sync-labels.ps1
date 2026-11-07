<#
.SYNOPSIS
    把 .github/labels.yml 同步到 GitHub 仓库标签。

.DESCRIPTION
    标签以 labels.yml 为准：文件里有的会被创建或更新（颜色、描述），
    文件里没有的默认标签（bug/enhancement 之类）保持不动 —— 删标签会让已打标的 Issue 掉标签，
    这一步不该由脚本顺手做掉。

    需要 gh CLI 且已 gh auth login。没有 gh 时脚本只打印将要执行的操作，不做任何改动。
    注意：不要用 $args 之类的自动变量存参数。

.EXAMPLE
    pwsh .github/scripts/sync-labels.ps1
    pwsh .github/scripts/sync-labels.ps1 -DryRun
#>
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$yml = Join-Path $root '.github/labels.yml'
if (-not (Test-Path $yml)) { throw "找不到 $yml" }

# labels.yml 是固定三行一组的结构，不必为它引入 YAML 依赖
$labels = @()
$current = $null
foreach ($line in Get-Content $yml -Encoding UTF8) {
    if ($line -match '^\s*-\s*name:\s*(.+?)\s*$') {
        if ($current) { $labels += $current }
        $current = [ordered]@{ name = $Matches[1].Trim('"', "'") }
        continue
    }
    if (-not $current) { continue }
    if ($line -match '^\s*color:\s*(.+?)\s*$') { $current.color = $Matches[1].Trim('"', "'") }
    elseif ($line -match '^\s*description:\s*(.+?)\s*$') { $current.description = $Matches[1].Trim('"', "'") }
}
if ($current) { $labels += $current }

Write-Host ('读取到 {0} 个标签' -f $labels.Count)

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host '未安装 gh CLI。以下是将要执行的操作，装好 gh 并登录后再跑一次即可真正同步：'
    foreach ($l in $labels) {
        Write-Host ('  gh label create "{0}" --color {1} --description "{2}" --force' -f $l.name, $l.color, $l.description)
    }
    exit 0
}

foreach ($l in $labels) {
    $ghArgs = @('label', 'create', $l.name, '--color', $l.color, '--description', $l.description, '--force')
    if ($DryRun) {
        Write-Host ('[dry-run] gh ' + ($ghArgs -join ' '))
        continue
    }
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning ('标签同步失败：{0}' -f $l.name) }
}

Write-Host '标签同步完成'
