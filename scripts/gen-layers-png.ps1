# 根据源码目录生成分层架构 PNG（可重复执行）
# 用法： powershell -ExecutionPolicy Bypass -File scripts\gen-layers-png.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outFile = Join-Path $root 'docs\architecture-layers.png'

$bands = @(
    @{ Name = 'UI  (layer 3)';                     Desc = '呈现层：托盘 + 7 个页面 + 自绘控件';              Fill = '#e8f0fe'; Stroke = '#1a73e8'; Color = '#174ea6';
       Items = @('MainForm','DashboardPage','BoosterPage','SchedulerPage','AdvancedControlPage','GamePage','RulesPage','DecisionPage','AIPage','UiToolkit','Theme','AppIcon','AppServices') },
    @{ Name = 'Core / Intelligence  (layer 2)';    Desc = '调度协调与决策：双心跳、场景、降级、预测、留痕';   Fill = '#e6f4ea'; Stroke = '#137333'; Color = '#0d652d';
       Items = @('SchedulerCoordinator','ForegroundWatcher','SchedulerContext','SchedulerDegradationEngine','StepSkipper','ModuleHealthMonitor','AdaptiveScheduler','DecisionEffectTracker','DecisionNecessityEvaluator','BoosterActions','PreloadRegistry','PresumedDataSource','DataCollector','IntelligenceEngine','ModelEngine','FeedbackLoop','LearningScheduler','LearningData') },
    @{ Name = 'Execution  (layer 1)';              Desc = '执行层：唯一会动手改系统状态的地方';               Fill = '#fef7e0'; Stroke = '#b06000'; Color = '#8a5200';
       Items = @('PowerSaver','FrequencyManager','InstructionSetOptimizer','WorkingSetLock','MemoryReclaimer','AutoJobManager','JobObjectApi','ProcessRuleEngine','PreloadProtection','UnifiedPreloader','WorkloadGuard') },
    @{ Name = 'Infrastructure  (layer 0)';         Desc = '基础设施：Win32 封装、配置、日志、事件、拓扑';     Fill = '#f3e8fd'; Stroke = '#8430ce'; Color = '#5b1c96';
       Items = @('NativeApi','ProcessSafeHelper','ProcessMatcher','CpuTopology','CpuTopologyEx','CacheManager','AppConfig','AppPaths','AppEnvironment','AppLogger','EventBus','Events','SafeTimer','Resilience','PersistenceManager','AtomicFile','GameCatalog','ModuleRegistry','StartupHelper','StringHelper','MathToolkit','Constants') },
    @{ Name = 'Windows（系统边界）';                Desc = '电源方案 / 亲和性 / Job 对象 / 工作集 / WMI / 注册表 / 性能计数器'; Fill = '#f1f3f4'; Stroke = '#5f6368'; Color = '#3c4043';
       Items = @() }
)

$width = 1260; $marginX = 40; $maxContent = 1160; $gap = 9; $chipH = 26; $chipGap = 8

$titleFont  = New-Object System.Drawing.Font('Microsoft YaHei', 18, [System.Drawing.FontStyle]::Bold)
$subFont    = New-Object System.Drawing.Font('Microsoft YaHei', 10)
$bandFont   = New-Object System.Drawing.Font('Microsoft YaHei', 11.5, [System.Drawing.FontStyle]::Bold)
$descFont   = New-Object System.Drawing.Font('Microsoft YaHei', 9)
$chipFont   = New-Object System.Drawing.Font('Microsoft YaHei', 8.8)
$noteFont   = New-Object System.Drawing.Font('Microsoft YaHei', 9)
$noteBold   = New-Object System.Drawing.Font('Microsoft YaHei', 10, [System.Drawing.FontStyle]::Bold)

# 先用一个临时位图测量文本
$probe = New-Object System.Drawing.Bitmap(10, 10)
$pg = [System.Drawing.Graphics]::FromImage($probe)
$pg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

function ChipWidth($text) { return [math]::Ceiling($pg.MeasureString($text, $chipFont).Width) + 18 }

# 预排版：计算每层行数与总高
$layout = @()
$y = 96
foreach ($b in $bands) {
    $rows = @(); $cur = @(); $curW = 0
    foreach ($it in $b.Items) {
        $w = ChipWidth $it
        if (($curW + $w) -gt ($maxContent - 32)) { if ($cur.Count) { $rows += ,$cur }; $cur = @(); $curW = 0 }
        $cur += ,@($it, $w); $curW += $w + $gap
    }
    if ($cur.Count) { $rows += ,$cur }
    $h = if ($rows.Count -gt 0) { 44 + ($rows.Count * ($chipH + $chipGap)) } else { 62 }
    $layout += ,@{ Band = $b; Rows = $rows; Y = $y; H = $h }
    $y += $h + 26
}
$totalH = $y + 96

$bmp = New-Object System.Drawing.Bitmap($width, $totalH)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.Clear([System.Drawing.Color]::White)

function Brush($hex) { return New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($hex)) }
function Pen2($hex, $w) { return New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml($hex), $w) }

function RoundRect($gr, $fill, $stroke, $w2, $x, $y2, $wd, $ht, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y2, $d, $d, 180, 90)
    $p.AddArc($x + $wd - $d, $y2, $d, $d, 270, 90)
    $p.AddArc($x + $wd - $d, $y2 + $ht - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y2 + $ht - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    if ($fill) { $gr.FillPath($fill, $p) }
    if ($stroke) { $gr.DrawPath($stroke, $p) }
    $p.Dispose()
}

$g.DrawString('PC Booster 分层架构', $titleFont, (Brush '#202124'), $marginX, 28)
$g.DrawString('只允许向下依赖：UI → Core / Intelligence → Execution → Infrastructure；反向依赖会被 scripts/audit 记为 violation', $subFont, (Brush '#5f6368'), $marginX, 62)

foreach ($L in $layout) {
    $b = $L.Band
    RoundRect $g (Brush $b.Fill) (Pen2 $b.Stroke 1.6) $null $marginX $L.Y 1160 $L.H 10
    $g.DrawString($b.Name, $bandFont, (Brush $b.Color), ($marginX + 16), ($L.Y + 10))
    $nameW = $g.MeasureString($b.Name, $bandFont).Width
    if ($b.Desc) { $g.DrawString($b.Desc, $descFont, (Brush '#5f6368'), ($marginX + 16 + $nameW + 14), ($L.Y + 13)) }

    $ry = $L.Y + 34
    foreach ($row in $L.Rows) {
        $cx = $marginX + 16
        foreach ($pair in $row) {
            $name = $pair[0]; $w = $pair[1]
            RoundRect $g (Brush '#ffffff') (Pen2 $b.Stroke 1) $null $cx $ry $w $chipH 6
            $g.DrawString($name, $chipFont, (Brush '#202124'), ($cx + 9), ($ry + 5))
            $cx += $w + $gap
        }
        $ry += $chipH + $chipGap
    }

    if ($b.Name -notlike 'Windows*') {
        $ax = $marginX + 26; $ay = $L.Y + $L.H + 3
        $g.DrawLine((Pen2 '#9aa0a6' 2), $ax, $ay, $ax, ($ay + 14))
        $pts = @(
            (New-Object System.Drawing.PointF($ax, ($ay + 20))),
            (New-Object System.Drawing.PointF(($ax - 5), ($ay + 12))),
            (New-Object System.Drawing.PointF(($ax + 5), ($ay + 12)))
        )
        $g.FillPolygon((Brush '#9aa0a6'), $pts)
    }
}

# 底部：统一闸门说明
$noteY = $totalH - 88
RoundRect $g (Brush '#fde7e9') (Pen2 '#c5221f' 1.2) $null $marginX $noteY 1160 72 8
$g.DrawString('四条写路径共用同一道闸门（改执行层必读）', $noteBold, (Brush '#a50e0e'), ($marginX + 16), ($noteY + 10))
$g.DrawString('优先级写入 · 亲和性绑定 · Job 限速 · 工作集 Trim  →  ProcessMatcher.IsSystemProcess / IsProtected / ProcessSafeHelper.HasVisibleWindow', $noteFont, (Brush '#3c4043'), ($marginX + 16), ($noteY + 32))
$g.DrawString('系统进程 · 显卡链路 · 存储缓存链路 · 安全/更新链路 · 反作弊进程 · 有可见窗口的程序：一律不写', $noteFont, (Brush '#3c4043'), ($marginX + 16), ($noteY + 50))

$g.Dispose()
$bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose(); $probe.Dispose()
Write-Host "已生成: $outFile  (${width}x${totalH})"
