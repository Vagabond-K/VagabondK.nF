#Requires -Version 7.0
param(
    [string]$GoPath,
    [string]$PythonPath,
    [string]$Dnp3PyPath,
    [switch]$ReportKnownLimits
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tools = Join-Path $root 'bin\protocol-tools\extra-peers'
if (-not $GoPath) { $GoPath = Join-Path $tools 'go\bin\go.exe' }
if (-not $PythonPath) { $PythonPath = Join-Path $tools 'python\python.exe' }
if (-not $Dnp3PyPath) { $Dnp3PyPath = Join-Path $tools 'dnp3py' }
foreach ($path in @($GoPath, $PythonPath, $Dnp3PyPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing prerequisite: $path. See Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\README.md." }
}
$GoPath = (Resolve-Path -LiteralPath $GoPath).Path
$PythonPath = (Resolve-Path -LiteralPath $PythonPath).Path
$Dnp3PyPath = (Resolve-Path -LiteralPath $Dnp3PyPath).Path
$revision = & git -C $Dnp3PyPath rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne 'c00d5df22e779df72f7caa4402c3f34957eb276d') {
    throw 'Expected dnp3py v0.6.0 pinned commit c00d5df22e779df72f7caa4402c3f34957eb276d.'
}
$changes = & git -C $Dnp3PyPath status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Refusing modified independent dnp3py sources.' }
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$oldToolchain, $oldGoPath, $oldCache = $env:GOTOOLCHAIN, $env:GOPATH, $env:GOCACHE
try {
    $env:GOTOOLCHAIN = 'local'
    $env:GOPATH = Join-Path $tools 'gopath'
    $env:GOCACHE = Join-Path $tools 'gocache'
    $goProject = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\go-peer'
    $executable = Join-Path $tools 'go-peer.exe'
    & $GoPath -C $goProject mod download
    if ($LASTEXITCODE -ne 0) { throw 'Independent Go dependency restore failed.' }
    & $GoPath -C $goProject mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Independent Go source checksum verification failed.' }
    & $GoPath -C $goProject build -mod=readonly -o $executable .
    if ($LASTEXITCODE -ne 0) { throw 'Independent Go peer build failed.' }
    $project = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\VagabondK.nF.DNP3.ExtraPeers.csproj'
    & dotnet build $project -c Release --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'VagabondK.nF comparison driver build failed.' }
    $fixture = Join-Path $root 'Tests\DNP3\VagabondK.nF.DNP3.ExtraPeers\python-peer.py'
    $arguments = @($executable, $PythonPath, $fixture, (Join-Path $Dnp3PyPath 'src'))
    if ($ReportKnownLimits) { $arguments += '--report-known-limits' }
    $log = Join-Path $tools ('numeric-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
    & dotnet run --project $project -c Release --no-build -- @arguments 2>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "Strict independent verification failed. Evidence: $log" }
    "Peer checks finished; review timestamp limitations. Evidence: $log"
}
finally {
    $env:GOTOOLCHAIN = $oldToolchain
    $env:GOPATH = $oldGoPath
    $env:GOCACHE = $oldCache
}
