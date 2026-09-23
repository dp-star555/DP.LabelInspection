param([string]$OutputDirectory,[int]$Rounds=2)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){throw 'Supply a new OutputDirectory.'}
if(Test-Path $OutputDirectory){throw 'Output directory already exists.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$env:PATH="$env:HALCONROOT\bin\$env:HALCONARCH;"+$env:PATH
$exe=Join-Path $PSScriptRoot 'bin/Release/net48/DP.LabelInspection.CanvasBenchmark.exe'
$cases=@('image_vga','image_4mp','image_16mp','region_2k','region_100k','xld_10k','xld_100k','mixed')
for($round=1;$round -le $Rounds;$round++){
    $backends=if($round%2 -eq 1){@('halcon','unified')}else{@('unified','halcon')}
    foreach($case in $cases){foreach($backend in $backends){
        $prefix=Join-Path $OutputDirectory "$case-$backend-r$round"
        Write-Output "RUN $case $backend round=$round"
        $process=Start-Process -FilePath $exe -ArgumentList @($backend,$case,('"'+$prefix+'"')) -PassThru -RedirectStandardOutput ($prefix+'.log') -RedirectStandardError ($prefix+'.error.log')
        $null=$process.Handle # 保留进程句柄，确保Windows PowerShell能够可靠读取ExitCode。
        if(-not $process.WaitForExit(120000)){$process.Kill();throw "Benchmark timeout: $prefix"}
        $process.WaitForExit()
        if($process.ExitCode -ne 0){throw "Benchmark failed: $prefix"}
        Start-Sleep -Milliseconds 350
    }}
}
$system=[ordered]@{
    cpu=Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
    memory=Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory
    os=Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version
    displays=Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate
    session=$env:SESSIONNAME
    halconRoot=$env:HALCONROOT
    rounds=$Rounds
    notes='Sequential fresh processes, reversed backend order in round 2. Small transient topmost benchmark window. No desktop captures; only control buffers are saved.'
}
$system | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'environment.json')
Write-Output 'PASS benchmark suite completed.'
