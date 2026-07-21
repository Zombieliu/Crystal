param(
    [switch]$ServerOnly,
    [switch]$ClientOnly,
    [switch]$SmokeTest,
    [int]$ServerWaitSeconds = 30
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $projectRoot "Build\\Server\\Debug"
$clientDir = Join-Path $projectRoot "Build\\Client\\Debug"
$serverExe = Join-Path $serverDir "Server.exe"
$clientExe = Join-Path $clientDir "Client.exe"
$serverDb = Join-Path $serverDir "Server.MirDB"
$smokeTestProject = Join-Path $projectRoot "tools\\ProtocolSmokeTest\\ProtocolSmokeTest.csproj"

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: $Path"
    }
}

function Wait-ForServerPort {
    param(
        [int]$Seconds
    )

    $deadline = (Get-Date).AddSeconds($Seconds)

    while ((Get-Date) -lt $deadline) {
        $listener = Get-NetTCPConnection -LocalPort 7000 -State Listen -ErrorAction SilentlyContinue
        if ($listener) {
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

Assert-FileExists -Path $serverExe -Label "Server executable"
Assert-FileExists -Path $clientExe -Label "Client executable"
Assert-FileExists -Path $serverDb -Label "Server database"

if ($SmokeTest) {
    Assert-FileExists -Path $smokeTestProject -Label "Smoke test project"
}

if (-not $ClientOnly) {
    $server = Start-Process -FilePath $serverExe -WorkingDirectory $serverDir -PassThru
    Write-Host "Server started. PID=$($server.Id)"

    if (-not (Wait-ForServerPort -Seconds $ServerWaitSeconds)) {
        throw "Server did not open 127.0.0.1:7000 within $ServerWaitSeconds second(s)."
    }

    Write-Host "Server is listening on 127.0.0.1:7000"
}

if (-not $ServerOnly) {
    $client = Start-Process -FilePath $clientExe -WorkingDirectory $clientDir -PassThru
    Write-Host "Client started. PID=$($client.Id)"
}

if ($SmokeTest) {
    dotnet run --no-build --project $smokeTestProject -c Debug
}
