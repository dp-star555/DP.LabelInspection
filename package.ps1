param([string]$ModelDirectory, [string]$OutputDirectory = 'dist/0.2.0-preview.1')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
if (Test-Path $destination) { throw 'Output already exists. Choose a new OutputDirectory; no release is overwritten.' }
if ($ModelDirectory) { $ModelDirectory = (Resolve-Path $ModelDirectory).Path }
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}
Run-Dotnet @('restore','DP.LabelInspection.sln','--locked-mode')
foreach ($target in @('net48','net8.0-windows')) {
    foreach ($ui in @('WinForms','Wpf')) {
        $directory = Join-Path $destination "$target/$ui"
        Run-Dotnet @('publish',"samples/DP.LabelInspection.Demo.$ui/DP.LabelInspection.Demo.$ui.csproj",'-c','Release','-f',$target,'--no-restore','--self-contained','false','-o',$directory)
        foreach ($assembly in @('DP.Vision.dll','DP.Vision.UI.dll','DP.Vision.Algorithms.dll','DP.Vision.OpenCv.dll','DP.Vision.Onnx.dll','DP.Vision.Zxing.dll','DP.Vision.OnnxDetection.dll','DP.LabelInspection.Adapter.Vision.dll','DP.LabelInspection.Runtime.dll')) {
            if (!(Test-Path (Join-Path $directory $assembly))) { throw "Missing migrated runtime assembly: $assembly ($target/$ui)" }
        }
        foreach ($removed in @('DP.LabelInspection.Vision.OpenCv','DP.LabelInspection.Vision.OnnxDetection','DP.LabelInspection.Ocr.Onnx','DP.LabelInspection.Barcode.Zxing')) {
            if (Test-Path (Join-Path $directory "$removed.dll")) { throw "Removed assembly leaked into publish: $removed" }
        }
        # 仅保留已验证的Windows x64原生资源，不携带Android、iOS、macOS或x86载荷。
        $runtimes = Join-Path $directory 'runtimes'
        if (Test-Path $runtimes) { Get-ChildItem $runtimes -Directory | Where-Object Name -ne 'win-x64' | Remove-Item -Recurse -Force }
        $legacyX86 = Join-Path $directory 'dll/x86'
        if (Test-Path $legacyX86) { Remove-Item $legacyX86 -Recurse -Force }
        $legacyX64 = Join-Path $directory 'dll/x64'
        if (Test-Path $legacyX64) {
            foreach ($native in Get-ChildItem $legacyX64 -File) {
                $rootCopy = Join-Path $directory $native.Name
                if ((Test-Path $rootCopy) -and ((Get-FileHash $rootCopy).Hash -eq (Get-FileHash $native.FullName).Hash)) { Remove-Item $native.FullName }
            }
        }
        if ($ModelDirectory) {
            New-Item -ItemType Directory -Force (Join-Path $directory 'models') | Out-Null
            Copy-Item (Join-Path $ModelDirectory 'ch_PP-OCRv4_rec_infer.onnx') (Join-Path $directory 'models/rec.onnx')
            $detector = Join-Path $ModelDirectory 'ch_PP-OCRv4_det_infer.onnx'
            if (Test-Path $detector) { Copy-Item $detector (Join-Path $directory 'models/ch_PP-OCRv4_det_infer.onnx') }
        }
        $launcher = @('@echo off','setlocal','cd /d "%~dp0"','if exist "%~dp0models\rec.onnx" set "DP_LABEL_REC_MODEL=%~dp0models\rec.onnx"',('start "" /wait "%~dp0DP.LabelInspection.Demo.' + $ui + '.exe"'),'endlocal') -join "`r`n"
        Set-Content -Path (Join-Path $directory 'start.cmd') -Value $launcher -Encoding ASCII
    }
}
Copy-Item README.md,VALIDATION.md,FULL_DELIVERY.md,DATA_BINDING.md,BARCODE_PRINT.md,QR_PRINT.md,BARCODE_INK_LOSS.md,BARCODE_EVIDENCE_GROUPS.md,HALCON_CANVAS_FEASIBILITY.md,CANVAS_BENCHMARK.md,VISION_WORKBENCH_MIGRATION.md,QUICK_GLYPH_LIBRARY.md,ROI_EVIDENCE_POLICY.md,ROI_PARAMETER_GUIDE.md,CURRENT_INSPECTION_FLOW.md (Join-Path $destination '.')
Copy-Item ../DP.Vision/ALGORITHM_MIGRATION.md (Join-Path $destination 'DP.Vision_ALGORITHM_MIGRATION.md')
@'
Windows x64 only. Install .NET Framework 4.8 or .NET 8 Desktop Runtime as appropriate.
Native OpenCV/ONNX dependencies must remain alongside the delivered assemblies/runtimes folders.
Host native DLL conflicts, VC++ runtime requirements and licenses must be reviewed before deployment.
Production sample images, user libraries, reports and feedback are NOT included.
Models are included only when the caller explicitly provides ModelDirectory; model redistribution rights must be reviewed.
WinForms is the full workbench. WPF is a native image/ROI/result control and host sample, not editor-UI parity.
No HALCON runtime, license or adapter implementation is bundled.
'@ | Set-Content -Path (Join-Path $destination 'DEPLOYMENT.txt') -Encoding UTF8
$files = Get-ChildItem $destination -Recurse -File
$files | ForEach-Object {
    $relative = $_.FullName.Substring($destination.Length+1)
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$relative
} | Set-Content -Path (Join-Path $destination 'SHA256SUMS.txt') -Encoding UTF8
Write-Output "Published: $destination"
