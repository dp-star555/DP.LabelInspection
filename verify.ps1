param([string]$RecognitionModel, [string]$ProductionAssets, [string]$OcrOracle)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force artifacts | Out-Null
function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments = @())
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $Arguments" }
}
$previousModel = $env:DP_LABEL_REC_MODEL
if (($ProductionAssets -or $OcrOracle) -and !($RecognitionModel -and $ProductionAssets -and $OcrOracle)) { throw 'OCR regression requires RecognitionModel, ProductionAssets and OcrOracle together.' }
Start-Transcript -Path artifacts/verify.log -Force | Out-Null
try {
    Invoke-Checked dotnet @('restore', 'DP.LabelInspection.sln', '--locked-mode')
    Invoke-Checked dotnet @('build', 'DP.LabelInspection.sln', '-c', 'Release', '--no-restore', '--nologo')
    foreach ($target in @('net48', 'net8.0-windows')) {
        Invoke-Checked dotnet @('test', 'tests/DP.LabelInspection.Tests/DP.LabelInspection.Tests.csproj', '-c', 'Release', '-f', $target,
            '--no-build', '--nologo', '--logger', 'trx', '--results-directory', "artifacts/tests/$target", '--', 'RunConfiguration.TargetPlatform=x64')
    }
    $modelArguments = @()
    if ($RecognitionModel) { $modelArguments = @((Resolve-Path $RecognitionModel).Path) }
    Invoke-Checked './tools/DP.LabelInspection.CompatibilityProbe/bin/Release/net48/DP.LabelInspection.CompatibilityProbe.exe' $modelArguments |
        Tee-Object -FilePath artifacts/probe-net48.log
    Invoke-Checked dotnet (@('./tools/DP.LabelInspection.CompatibilityProbe/bin/Release/net8.0-windows/DP.LabelInspection.CompatibilityProbe.dll') + $modelArguments) |
        Tee-Object -FilePath artifacts/probe-net8.log
    if ($OcrOracle) {
        $regressionArguments = @($modelArguments[0], (Resolve-Path $ProductionAssets).Path, (Resolve-Path $OcrOracle).Path)
        Invoke-Checked './tools/DP.LabelInspection.OcrRegression/bin/Release/net48/DP.LabelInspection.OcrRegression.exe' $regressionArguments |
            Tee-Object -FilePath artifacts/ocr-net48.log
        Invoke-Checked dotnet (@('./tools/DP.LabelInspection.OcrRegression/bin/Release/net8.0-windows/DP.LabelInspection.OcrRegression.dll') + $regressionArguments) |
            Tee-Object -FilePath artifacts/ocr-net8.log
    }
    $env:DP_LABEL_REC_MODEL = if ($RecognitionModel) { $modelArguments[0] } else { $null }
    $png48 = Join-Path $PSScriptRoot 'artifacts/winforms-net48.png'
    $demo48 = Join-Path $PSScriptRoot 'samples/DP.LabelInspection.Demo.WinForms/bin/Release/net48/DP.LabelInspection.Demo.WinForms.exe'
    $process = Start-Process -FilePath $demo48 -ArgumentList @('--smoke', ('"' + $png48 + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'net48 WinForms smoke failed.' }
    Invoke-Checked dotnet @('./samples/DP.LabelInspection.Demo.WinForms/bin/Release/net8.0-windows/DP.LabelInspection.Demo.WinForms.dll', '--smoke', 'artifacts/winforms-net8.png')
    foreach ($name in @('net48', 'net8')) {
        $marker = "artifacts/winforms-$name.png.txt"
        if (!(Test-Path $marker) -or !((Get-Content $marker -Raw).Contains('roi_mouse_mapping=True'))) { throw "Missing UI verification: $name" }
        if ($RecognitionModel -and !((Get-Content $marker -Raw).Contains('ocr=A1020'))) { throw "Missing real OCR UI evidence: $name" }
    }
    $wpf48 = Join-Path $PSScriptRoot 'samples/DP.LabelInspection.Demo.Wpf/bin/Release/net48/DP.LabelInspection.Demo.Wpf.exe'
    $wpfPng48 = Join-Path $PSScriptRoot 'artifacts/wpf-net48.png'
    $wpf = Start-Process -FilePath $wpf48 -ArgumentList @('--smoke', ('"' + $wpfPng48 + '"')) -Wait -PassThru
    if ($wpf.ExitCode -ne 0) { throw 'net48 WPF smoke failed.' }
    Invoke-Checked dotnet @('./samples/DP.LabelInspection.Demo.Wpf/bin/Release/net8.0-windows/DP.LabelInspection.Demo.Wpf.dll','--smoke','artifacts/wpf-net8.png')
    foreach ($name in @('net48','net8')) {
        if (!((Get-Content "artifacts/wpf-$name.png.txt" -Raw).Contains('native_wpf=True'))) { throw "Missing WPF evidence: $name" }
    }
    Write-Output 'PASS: both frameworks, native OpenCV, WinForms ROI/report workflow and native WPF.'
    if ($ProductionAssets -and $OcrOracle) { Write-Output 'PASS: real OCR/physical appearance/discovery regressions and production glyph/library UI workflow.' }
    elseif ($RecognitionModel) { Write-Output 'PASS: model probe and real single-line OCR UI; full production regressions NOT RUN.' }
    else { Write-Output 'NOT RUN: real OCR/appearance/discovery regressions and model-dependent production UI workflow (no model configured).' }
} finally {
    $env:DP_LABEL_REC_MODEL = $previousModel
    Stop-Transcript | Out-Null
}
