#Requires -Version 7.0
<#
.SYNOPSIS
    nanoFramework용 검증 프로그램을 빌드하고 가상 nanoCLR에서 실행합니다.
.DESCRIPTION
    Tests\Common\VagabondK.nF.Virtual.Tests를 Visual Studio MSBuild(nanoFramework 확장 필요)로 빌드한 뒤
    `nanoclr run`으로 실행하고 출력의 "VIRTUAL PASS" 표식으로 성공 여부를 판정합니다.
    nanoclr는 프로그램 실패와 무관하게 0으로 종료하므로 표식이 없으면 실패로 처리합니다.
.PARAMETER MSBuild
    MSBuild.exe 경로입니다. 생략하면 vswhere로 찾습니다.
.PARAMETER TimeoutSeconds
    nanoCLR 실행 제한 시간입니다.
#>
param(
    [string]$MSBuild,
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'Tests\Common\VagabondK.nF.Virtual.Tests\VagabondK.nF.Virtual.Tests.nfproj'

if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'vswhere.exe was not found. Pass -MSBuild.' }
    $MSBuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $MSBuild) { throw 'MSBuild.exe was not found. Pass -MSBuild.' }
}
if (-not (Get-Command nanoclr -ErrorAction SilentlyContinue)) {
    throw "nanoclr was not found. Install it with 'dotnet tool install -g nanoclr'."
}

Write-Host '==> Build VagabondK.nF.Virtual.Tests' -ForegroundColor Cyan
& $MSBuild $project /t:Build /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$assemblies = (Get-ChildItem (Join-Path (Split-Path $project) 'bin\Release') -Filter *.pe).FullName
Write-Host "==> nanoclr run ($($assemblies.Count) assemblies)" -ForegroundColor Cyan
$log = New-TemporaryFile
try {
    $process = Start-Process nanoclr -ArgumentList (@('run', '--assemblies') + $assemblies) -NoNewWindow -PassThru `
        -RedirectStandardOutput $log.FullName
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id
        throw "nanoclr did not finish within $TimeoutSeconds seconds."
    }
    $output = Get-Content $log.FullName
}
finally {
    Remove-Item $log.FullName -ErrorAction SilentlyContinue
}

$report = @($output | Where-Object { $_ -match '^(ok |FAIL|VIRTUAL )' })
$report | Write-Host
$pass = $output | Where-Object { $_ -match '^VIRTUAL PASS' }
if (-not $pass -or ($output | Where-Object { $_ -match '^FAIL' })) {
    $output | Select-Object -Last 80 | Write-Host
    Write-Host 'VIRTUAL TESTS FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'VIRTUAL TESTS PASS' -ForegroundColor Green
