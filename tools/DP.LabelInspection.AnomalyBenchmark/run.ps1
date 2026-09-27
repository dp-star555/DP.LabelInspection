# 逐字符异常检测对比评估：导出字符数据集并运行方法B → 变差模型（numpy）→ anomalib（PatchCore、PaDiM）→ HALCON变差模型 → 汇总报告。
# 用法（在仓库根目录）：
#   .\tools\DP.LabelInspection.AnomalyBenchmark\run.ps1 -Config D:\labels\bench.json -Out D:\bench-out
#   先自检流程：.\tools\DP.LabelInspection.AnomalyBenchmark\run.ps1 -Synthetic -Out D:\bench-synth
param(
    [string]$Config,
    [Parameter(Mandatory = $true)][string]$Out,
    [switch]$Synthetic,
    [switch]$SkipAnomalib,
    [switch]$SkipHalcon,
    [string]$Python = "python",
    [string]$AnomalibArgs = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tool = Join-Path $root "tools\DP.LabelInspection.AnomalyBenchmark\DP.LabelInspection.AnomalyBenchmark.csproj"
$halcon = Join-Path $root "tools\DP.LabelInspection.AnomalyBenchmark.Halcon\DP.LabelInspection.AnomalyBenchmark.Halcon.csproj"
$py = Join-Path $PSScriptRoot "python"

function Invoke-Step([string]$title, [scriptblock]$body) {
    Write-Host "== $title" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { throw "$title 失败（退出码 $LASTEXITCODE）" }
}

if ($Synthetic) {
    $Config = Join-Path $Out "synthetic\bench.json"
    Invoke-Step "生成合成标签" { dotnet run --project $tool -c Release -f net8.0-windows -- synth (Join-Path $Out "synthetic") }
    $Out = Join-Path $Out "result"
}
if (-not $Config) { throw "请指定 -Config（或用 -Synthetic 自检）。" }

Invoke-Step "导出字符并运行方法B" { dotnet run --project $tool -c Release -f net8.0-windows -- export $Config $Out }
Invoke-Step "变差模型（numpy）" { & $Python (Join-Path $py "variation_bench.py") $Out }
if (-not $SkipAnomalib) {
    Invoke-Step "anomalib PatchCore / PaDiM" { & $Python (Join-Path $py "anomalib_bench.py") $Out $AnomalibArgs.Split(" ", [StringSplitOptions]::RemoveEmptyEntries) }
}
if (-not $SkipHalcon) {
    if ($env:HALCONROOT) {
        Invoke-Step "HALCON变差模型" { dotnet run --project $halcon -c Release -- $Out }
    } else {
        Write-Host "未设置HALCONROOT，跳过HALCON（可用 -SkipHalcon 关闭此提示）。" -ForegroundColor Yellow
    }
}
Invoke-Step "汇总（阈值=留一最大值×1.5）" { & $Python (Join-Path $py "compare.py") $Out }
Invoke-Step "汇总（阈值=留一第90百分位×1.5）" { & $Python (Join-Path $py "compare.py") $Out --rule p90 --out-name report-p90 }
Invoke-Step "汇总（留一最大值×1.5，加组中位数下限）" { & $Python (Join-Path $py "compare.py") $Out --floor group-median --out-name report-floor }
Write-Host "完成：$(Join-Path $Out 'report.html')" -ForegroundColor Green
