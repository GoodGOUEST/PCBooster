# 根据源码目录生成分层架构 SVG（可重复执行，图与代码保持一致）
# 用法： powershell -ExecutionPolicy Bypass -File scripts\gen-layers-svg.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $root 'docs\architecture-layers.svg'

$bands = @(
    @{ Name = 'UI  (layer 3)';           Desc = '呈现层：托盘 + 7 个页面 + 自绘控件';           Fill = '#e8f0fe'; Stroke = '#1a73e8'; Color = '#174ea6';
       Items = @('MainForm','DashboardPage','BoosterPage','SchedulerPage','AdvancedControlPage','GamePage','RulesPage','DecisionPage','AIPage','UiToolkit','Theme','AppIcon','AppServices') },
    @{ Name = 'Core / Intelligence  (layer 2)'; Desc = '调度协调与决策：双心跳、场景、降级、预测、留痕'; Fill = '#e6f4ea'; Stroke = '#137333'; Color = '#0d652d';
       Items = @('SchedulerCoordinator','ForegroundWatcher','SchedulerContext','SchedulerDegradationEngine','StepSkipper','ModuleHealthMonitor','AdaptiveScheduler','DecisionEffectTracker','DecisionNecessityEvaluator','BoosterActions','PreloadRegistry','PresumedDataSource','DataCollector','IntelligenceEngine','ModelEngine','FeedbackLoop','LearningScheduler','LearningData') },
    @{ Name = 'Execution  (layer 1)';    Desc = '执行层：唯一会"动手"改系统状态的地方';                 Fill = '#fef7e0'; Stroke = '#b06000'; Color = '#8a5200';
       Items = @('PowerSaver','PowerTuning (FrequencyManager)','PowerTuning (InstructionSetOptimizer)','PowerTuning (WorkingSetLock)','MemoryReclaimer','AutoJobManager','JobObjectApi','ProcessRuleEngine','PreloadProtection','UnifiedPreloader','WorkloadGuard') },
    @{ Name = 'Infrastructure  (layer 0)'; Desc = '基础设施：Win32 封装、配置、日志、事件、拓扑'; Fill = '#f3e8fd'; Stroke = '#8430ce'; Color = '#5b1c96';
       Items = @('NativeApi','ProcessSafeHelper','ProcessMatcher','CpuTopology','CpuTopologyEx','CacheManager','AppConfig','AppPaths','AppEnvironment','AppLogger','EventBus','Events','SafeTimer','Resilience','PersistenceManager','AtomicFile','GameCatalog','ModuleRegistry','StartupHelper','StringHelper','MathToolkit','Constants') },
    @{ Name = 'Windows（系统边界）'; Desc = '电源方案 / 亲和性 / Job 对象 / 工作集 / WMI / 注册表 / 性能计数器'; Fill = '#f1f3f4'; Stroke = '#5f6368'; Color = '#3c4043';
       Items = @() }
)

function Get-ChipWidth([string]$text) {
    $w = 0.0
    foreach ($ch in $text.ToCharArray()) { if ([int]$ch -gt 255) { $w += 12.0 } else { $w += 6.6 } }
    return [math]::Round($w + 18, 0)
}

$mx = 40; $maxW = 1180; $gap = 10; $chipH = 26
$y = 90
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("<svg xmlns='http://www.w3.org/2000/svg' width='1260' height='__H__' viewBox='0 0 1260 __H__' font-family='Segoe UI, Microsoft YaHei, sans-serif'>")
[void]$sb.AppendLine("  <rect width='1260' height='__H__' fill='#ffffff'/>")
[void]$sb.AppendLine("  <text x='40' y='38' font-size='22' font-weight='600' fill='#202124'>PC Booster 分层架构</text>")
[void]$sb.AppendLine("  <text x='40' y='62' font-size='13' fill='#5f6368'>只允许向下依赖（UI → Core/Intelligence → Execution → Infrastructure）；反向依赖由 scripts/audit 记为 violation</text>")

foreach ($b in $bands) {
    $rows = @(); $cur = @(); $curW = 0
    foreach ($it in $b.Items) {
        $w = Get-ChipWidth $it
        if (($curW + $w) -gt ($maxW - $mx)) { if ($cur.Count) { $rows += ,$cur }; $cur = @(); $curW = 0 }
        $cur += ,@($it, $w); $curW += $w + $gap
    }
    if ($cur.Count) { $rows += ,$cur }

    $bandH = 46 + ($rows.Count * ($chipH + 8))
    [void]$sb.AppendLine("  <rect x='$mx' y='$y' width='1180' height='$bandH' rx='10' fill='$($b.Fill)' stroke='$($b.Stroke)' stroke-width='1.5'/>")
    [void]$sb.AppendLine("  <text x='$($mx + 16)' y='$($y + 26)' font-size='15' font-weight='700' fill='$($b.Color)'>$($b.Name)</text>")
    [void]$sb.AppendLine("  <text x='$($mx + 16 + (Get-ChipWidth $b.Name))' y='$($y + 26)' font-size='12' fill='#5f6368'>$($b.Desc)</text>")

    $ry = $y + 40
    foreach ($row in $rows) {
        $cx = $mx + 16
        foreach ($pair in $row) {
            $name = $pair[0]; $w = $pair[1]
            [void]$sb.AppendLine("  <rect x='$cx' y='$ry' width='$w' height='$chipH' rx='6' fill='#ffffff' stroke='$($b.Stroke)' stroke-opacity='0.55'/>")
            [void]$sb.AppendLine("  <text x='$([math]::Round($cx + 9,0))' y='$($ry + 17)' font-size='11.5' fill='#202124'>$name</text>")
            $cx += $w + $gap
        }
        $ry += $chipH + 8
    }

    $nextY = $y + $bandH
    if ($b.Name -ne 'Windows（系统边界）') {
        [void]$sb.AppendLine("  <path d='M $($mx + 24) $($nextY + 4) L $($mx + 24) $($nextY + 18)' stroke='#9aa0a6' stroke-width='2'/>")
        [void]$sb.AppendLine("  <path d='M $($mx + 19) $($nextY + 14) L $($mx + 24) $($nextY + 20) L $($mx + 29) $($nextY + 14)' fill='none' stroke='#9aa0a6' stroke-width='2'/>")
        $y = $nextY + 24
    } else { $y = $nextY }
}

$noteY = $y + 16
[void]$sb.AppendLine("  <rect x='$mx' y='$noteY' width='1180' height='78' rx='8' fill='#fde7e9' stroke='#c5221f' stroke-width='1.2'/>")
[void]$sb.AppendLine("  <text x='$($mx + 16)' y='$($noteY + 24)' font-size='13' font-weight='700' fill='#a50e0e'>四条写路径共用同一道闸门（改执行层必读）</text>")
[void]$sb.AppendLine("  <text x='$($mx + 16)' y='$($noteY + 46)' font-size='12' fill='#3c4043'>优先级写入 · 亲和性绑定 · Job 限速 · 工作集 Trim  →  ProcessMatcher.IsSystemProcess / IsProtected / ProcessSafeHelper.HasVisibleWindow</text>")
[void]$sb.AppendLine("  <text x='$($mx + 16)' y='$($noteY + 66)' font-size='12' fill='#3c4043'>系统进程 · 显卡链路 · 存储缓存链路 · 安全/更新链路 · 反作弊 · 有可见窗口的程序：一律不写</text>")
$total = $noteY + 78 + 24

$svg = $sb.ToString().Replace('__H__', $total)
[System.IO.File]::WriteAllText($outFile, $svg, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已生成: $outFile  (高度 $total px)"
