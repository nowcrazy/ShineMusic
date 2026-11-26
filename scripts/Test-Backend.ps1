#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $PostgresPort = 45439,
    [string] $FfmpegBin,
    [string] $DockerPath,
    [string] $ResultsDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*\.trx$')]
    [string] $TrxFileName,
    [ValidateRange(30, 3600)]
    [int] $TestHangTimeoutSeconds = 120,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$taskIsWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSolution = Join-Path $taskRoot 'ShineMusicApi/ShineMusic.slnx'
if (-not (Test-Path -LiteralPath $taskSolution -PathType Leaf)) {
    throw 'Run this script from a ShineMusic repository checkout containing ShineMusicApi/ShineMusic.slnx.'
}

$taskDocker = $DockerPath
if (-not [string]::IsNullOrWhiteSpace($taskDocker) -and
    -not (Test-Path -LiteralPath $taskDocker -PathType Leaf)) {
    throw 'The supplied -DockerPath does not identify an existing Docker CLI executable.'
}
if ([string]::IsNullOrWhiteSpace($taskDocker)) {
    $taskDockerCommand = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $taskDocker = if ($null -ne $taskDockerCommand) { $taskDockerCommand.Source } else { $null }
}
if ([string]::IsNullOrWhiteSpace($taskDocker) -and $taskIsWindows) {
    $taskDockerCandidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Docker/Docker/resources/bin/docker.exe'),
        'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
    )
    $taskLocalAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if (-not [string]::IsNullOrWhiteSpace($taskLocalAppData)) {
        $taskDockerCandidates += Join-Path $taskLocalAppData 'Programs/DockerDesktop/resources/bin/docker.exe'
    }
    $taskDocker = $taskDockerCandidates | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf -ErrorAction SilentlyContinue
    } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($taskDocker)) {
    throw 'Docker CLI was not found. Install or start Docker Desktop, or make an existing Docker CLI available on this process PATH.'
}
$taskDotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $taskDotnetCommand) { throw '.NET SDK was not found on this process PATH.' }
$taskDotnet = $taskDotnetCommand.Source

& $taskDocker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not available. Start Docker Desktop or the configured Docker daemon, then retry.' }
$taskImage = 'postgres:16-alpine'
& $taskDocker image inspect --format '{{.Id}}' $taskImage *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'The local postgres:16-alpine image is missing. Run docker pull postgres:16-alpine, then retry. This script does not download images.'
}

$taskExecutableSuffix = if ($taskIsWindows) { '.exe' } else { '' }
if ([string]::IsNullOrWhiteSpace($FfmpegBin)) {
    $taskFfmpegCandidates = @(
        [Environment]::GetEnvironmentVariable('SHINEMUSIC_TEST_FFMPEG_BIN', 'Process'),
        (Join-Path $taskRoot 'ShineMusicApi/tools/ffmpeg/bin')
    ) + ([Environment]::GetEnvironmentVariable('PATH', 'Process') -split [System.IO.Path]::PathSeparator)
    $FfmpegBin = $taskFfmpegCandidates | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        (Test-Path -LiteralPath (Join-Path $_ "ffmpeg$taskExecutableSuffix") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_ "ffprobe$taskExecutableSuffix") -PathType Leaf)
    } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($FfmpegBin)) {
    throw 'FFmpeg tools were not found. Supply -FfmpegBin with a directory containing both ffmpeg and ffprobe, or install the local tools described in ShineMusicApi/tools/ffmpeg/README.md.'
}
$FfmpegBin = [System.IO.Path]::GetFullPath($FfmpegBin)
foreach ($taskTool in @('ffmpeg', 'ffprobe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $FfmpegBin "$taskTool$taskExecutableSuffix") -PathType Leaf)) {
        throw "The specified FFmpeg directory does not contain $taskTool$taskExecutableSuffix."
    }
}

$taskRunId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $taskRoot 'ShineMusicApi/.artifacts/test-results'
}
elseif (-not [System.IO.Path]::IsPathRooted($ResultsDirectory) -or
    ($taskIsWindows -and $ResultsDirectory -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2})')) {
    $ResultsDirectory = Join-Path $taskRoot $ResultsDirectory
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
$null = New-Item -ItemType Directory -Path $ResultsDirectory -Force
$taskResultName = if ([string]::IsNullOrWhiteSpace($TrxFileName)) {
    'backend-isolation-{0}-{1}.trx' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), $taskRunId.Substring(0, 8)
} else { $TrxFileName }
$taskResultPath = Join-Path $ResultsDirectory $taskResultName
$taskContainerName = "shinemusic-test-$taskRunId"
$taskDatabase = "shinemusic_test_$taskRunId"
$taskOwnerLabel = 'shinemusic.test-owner'
$taskPasswordBytes = [byte[]]::new(32)
$taskRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $taskRandom.GetBytes($taskPasswordBytes) } finally { $taskRandom.Dispose() }
$taskPassword = [BitConverter]::ToString($taskPasswordBytes).Replace('-', '')
$taskEnvironmentNames = @('POSTGRES_USER', 'POSTGRES_PASSWORD', 'POSTGRES_DB',
    'SHINEMUSIC_TEST_POSTGRES_CONNECTION', 'SHINEMUSIC_TEST_FFMPEG_BIN')
$taskPreviousEnvironment = @{}
foreach ($taskName in $taskEnvironmentNames) {
    $taskPreviousEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
}
$taskRunAttempted = $false
$taskRunFailed = $false
$taskContainerId = $null
$taskCleanupError = $null

try {
    [Environment]::SetEnvironmentVariable('POSTGRES_USER', 'shinemusic', 'Process')
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD', $taskPassword, 'Process')
    [Environment]::SetEnvironmentVariable('POSTGRES_DB', $taskDatabase, 'Process')
    # Pass environment variable names only. The random password never appears in CLI arguments or output.
    $taskRunAttempted = $true
    $taskNativeErrorPreference = $ErrorActionPreference
    try {
        # Windows PowerShell turns native stderr into PowerShell error records.
        # Check Docker's exit code below so cleanup retains the original failure.
        $ErrorActionPreference = 'Continue'
        $taskRunOutput = & $taskDocker run --detach --rm --pull=never --name $taskContainerName `
            --label "$taskOwnerLabel=$taskRunId" --publish "127.0.0.1:${PostgresPort}:5432" `
            --env POSTGRES_USER --env POSTGRES_PASSWORD --env POSTGRES_DB $taskImage 2>&1
    }
    finally { $ErrorActionPreference = $taskNativeErrorPreference }
    if ($LASTEXITCODE -ne 0) {
        throw "Could not start the isolated PostgreSQL container. Check Docker and ensure 127.0.0.1:$PostgresPort is free; use -PostgresPort to choose another port."
    }
    $taskContainerId = ($taskRunOutput -join '').Trim()
    if ($taskContainerId -notmatch '^[a-f0-9]{64}$') { throw 'Docker did not return the expected container ID.' }

    Write-Host "Waiting for isolated PostgreSQL on 127.0.0.1:$PostgresPort."
    $taskDeadline = [System.Diagnostics.Stopwatch]::StartNew()
    $taskReady = $false
    while ($taskDeadline.Elapsed.TotalSeconds -lt 30) {
        & $taskDocker exec $taskContainerId pg_isready --username shinemusic --dbname $taskDatabase --timeout 1 *> $null
        if ($LASTEXITCODE -eq 0) { $taskReady = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $taskReady) { throw 'The isolated PostgreSQL container did not become ready within 30 seconds.' }

    $taskConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $taskConnection['Host'] = '127.0.0.1'
    $taskConnection['Port'] = $PostgresPort
    $taskConnection['Database'] = $taskDatabase
    $taskConnection['Username'] = 'shinemusic'
    $taskConnection['Password'] = $taskPassword
    [Environment]::SetEnvironmentVariable('SHINEMUSIC_TEST_POSTGRES_CONNECTION', $taskConnection.get_ConnectionString(), 'Process')
    [Environment]::SetEnvironmentVariable('SHINEMUSIC_TEST_FFMPEG_BIN', $FfmpegBin, 'Process')

    if (-not $SkipBuild) {
        & $taskDotnet build $taskSolution --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Backend Release build failed.' }
    }
    & $taskDotnet test $taskSolution --configuration Release --no-build --nologo `
        --blame-hang-timeout "${TestHangTimeoutSeconds}s" --blame-hang-dump-type none `
        --logger "trx;LogFileName=$taskResultName" --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed. See the test output above.' }
    if (-not (Test-Path -LiteralPath $taskResultPath -PathType Leaf)) {
        throw 'Backend tests completed without creating the requested TRX report.'
    }
    Write-Host "TRX report: $taskResultPath"
}
catch {
    $taskRunFailed = $true
    throw
}
finally {
    foreach ($taskName in $taskEnvironmentNames) {
        if ($null -eq $taskPreviousEnvironment[$taskName]) {
            # PowerShell converts $null to an empty string for this overload. On modern
            # .NET that creates an empty variable instead of removing an absent one.
            [Environment]::SetEnvironmentVariable($taskName, [NullString]::Value, 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable($taskName, $taskPreviousEnvironment[$taskName], 'Process')
        }
    }
    if ($taskRunAttempted) {
        $taskNativeErrorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            # JSON avoids embedded native-argument quotes on Windows PowerShell.
            $taskActualLabels = & $taskDocker container inspect --format '{{json .Config.Labels}}' $taskContainerName 2>$null
            $taskInspectExit = $LASTEXITCODE
            $taskActualId = & $taskDocker container inspect --format '{{.Id}}' $taskContainerName 2>$null
            $taskIdInspectExit = $LASTEXITCODE
            $taskActualOwner = $null
            if ($taskInspectExit -eq 0) {
                $taskLabels = ($taskActualLabels -join "`n") | ConvertFrom-Json -ErrorAction Stop
                $taskActualOwner = $taskLabels.$taskOwnerLabel
            }
            # Verify ownership before deleting by ID; preserve existing containers.
            if ($taskInspectExit -eq 0 -and $taskIdInspectExit -eq 0 -and
                $taskActualOwner -eq $taskRunId -and ($taskActualId -join '').Trim() -match '^[a-f0-9]{64}$') {
                & $taskDocker container rm --force (($taskActualId -join '').Trim()) *> $null
                if ($LASTEXITCODE -ne 0) {
                    $taskCleanupError = "Could not remove this run's PostgreSQL container: $taskContainerName"
                }
                else {
                    $taskRemainingId = & $taskDocker container ls --all --quiet --no-trunc --filter "id=$(($taskActualId -join '').Trim())" 2>$null
                    if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($taskRemainingId -join '').Trim())) {
                        $taskCleanupError = "Could not verify PostgreSQL container cleanup: $taskContainerName"
                    }
                    else { Write-Host "Removed this run's isolated PostgreSQL container: $taskContainerName" }
                }
            }
            elseif ($taskInspectExit -eq 0) {
                $taskCleanupError = "The PostgreSQL container ownership check did not match; no container was removed: $taskContainerName"
            }
            elseif (-not [string]::IsNullOrWhiteSpace($taskContainerId)) {
                $taskRemainingId = & $taskDocker container ls --all --quiet --no-trunc --filter "id=$taskContainerId" 2>$null
                if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($taskRemainingId -join '').Trim())) {
                    $taskCleanupError = "Could not verify PostgreSQL container cleanup: $taskContainerName"
                }
            }
        }
        catch { $taskCleanupError = "Could not verify PostgreSQL container cleanup: $taskContainerName" }
        finally { $ErrorActionPreference = $taskNativeErrorPreference }
        if (-not [string]::IsNullOrWhiteSpace($taskCleanupError)) {
            if ($taskRunFailed) { Write-Warning $taskCleanupError }
            else { throw $taskCleanupError }
        }
    }
}
