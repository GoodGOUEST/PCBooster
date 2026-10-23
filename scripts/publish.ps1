// ============================================================
// PC Booster v3.9.8
// SPDX-License-Identifier: GPL-3.0-or-later
// scripts/publish.ps1
// 发布脚本：产出单文件自包含包（x64 / arm64），打印大小与哈希
// 用法： pwsh scripts/publish.ps1 [-Runtime x64|arm64|all]
// ============================================================
param([ValidateSet('x64', 'arm64', 'all')][string]$Runtime = 'x64')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $profiles = switch ($Runtime) {
        'x64' { @('SingleFileX64') }
        'arm64' { @('SingleFileArm64') }
        default { @('SingleFileX64', 'SingleFileArm64') }
    }

    foreach ($p in $profiles) {
        Write-Host "`n=== 发布 $p ===" -ForegroundColor Cyan
        dotnet publish PCBooster.csproj -c Release -p:PublishProfile=$p
        if ($LASTEXITCODE -ne 0) { throw "发布失败：$p" }
    }

    Write-Host "`n=== 产物 ===" -ForegroundColor Cyan
    Get-ChildItem (Join-Path $root 'out') -Recurse -Filter 'PCBooster.exe' | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        '{0,-46} {1,7:N1} MB  SHA256 {2}' -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB), $hash
    }
}
finally {
    Pop-Location
}
