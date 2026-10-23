# ============================================================
# PC Booster v3.9.8
# SPDX-License-Identifier: GPL-3.0-or-later
# scripts/gate.ps1
# 本地/CI 通用生产门禁：构建(警告即错误) → 单元测试 → 空 catch → 分层审计基线
# 用法： pwsh scripts/gate.ps1 [-SkipTests]
# ============================================================
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $fail = 0

    Write-Host "`n[1/4] 构建（警告视为错误）" -ForegroundColor Cyan
    dotnet build PCBooster.csproj -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { Write-Host "构建失败" -ForegroundColor Red; $fail++ }

    if (-not $SkipTests) {
        Write-Host "`n[2/4] 单元测试" -ForegroundColor Cyan
        dotnet test tests\PCBooster.Tests\PCBooster.Tests.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "测试失败" -ForegroundColor Red; $fail++ }
    }
    else {
        Write-Host "`n[2/4] 单元测试（已跳过）" -ForegroundColor Yellow
    }

    Write-Host "`n[3/4] 空 catch 检查（吞异常 = 缺陷）" -ForegroundColor Cyan
    $sources = Get-ChildItem $root -Recurse -Include *.cs -File | Where-Object {
        $_.FullName -notmatch '\\(bin|obj|out)\\' -and $_.FullName -notmatch '\\tests\\'
    }
    $emptyCatches = 0
    foreach ($f in $sources) {
        $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        $emptyCatches += ([regex]::Matches($text, 'catch\s*(\([^)]*\))?\s*\{\s*\}')).Count
    }
    if ($emptyCatches -gt 0) { Write-Host "发现 $emptyCatches 处空 catch" -ForegroundColor Red; $fail++ }
    else { Write-Host "空 catch: 0" -ForegroundColor Green }

    Write-Host "`n[4/4] 分层审计（对比基线）" -ForegroundColor Cyan
    $baselineCycles = 2
    $baselineViolations = 10

    node scripts\parse_cs3.mjs | Out-Null
    Start-Sleep -Seconds 2
    node scripts\audit_report.mjs | Out-Null
    Start-Sleep -Seconds 2

    $report = Join-Path $root 'audit_report.txt'
    if (-not (Test-Path $report)) { Write-Host "审计报告缺失" -ForegroundColor Red; $fail++ }
    else {
        $text = Get-Content $report -Raw -Encoding UTF8
        $cycles = 0; $violations = 0
        if ($text -match '(?m)^cycle:\s*(\d+)') { $cycles = [int]$Matches[1] }
        if ($text -match '(?m)^violations:\s*(\d+)') { $violations = [int]$Matches[1] }

        Write-Host "cycles=$cycles (基线 $baselineCycles)  violations=$violations (基线 $baselineViolations)"
        if ($cycles -gt $baselineCycles) { Write-Host "循环依赖增加" -ForegroundColor Red; $fail++ }
        if ($violations -gt $baselineViolations) { Write-Host "分层违规增加" -ForegroundColor Red; $fail++ }
    }

    Write-Host ""
    if ($fail -eq 0) { Write-Host "门禁通过 ✅" -ForegroundColor Green; exit 0 }
    Write-Host "门禁失败：$fail 项 ❌" -ForegroundColor Red; exit 1
}
finally {
    Pop-Location
}
