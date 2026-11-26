[CmdletBinding()]
param(
    [switch] $DatabaseOnly,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$projectPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\ShineMusicApi\ShineMusic.Api\ShineMusic.Api.csproj'))
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw 'The ShineMusic API project could not be found relative to this script.'
}
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop

# Load only the existing local infrastructure credentials. Environment variables
# set by the loader are inherited by the dotnet child in this same process.
& (Join-Path $PSScriptRoot 'Load-LocalInfrastructureEnv.ps1')

$applicationArguments = @('--create-database')
if (-not $DatabaseOnly) {
    $applicationArguments += '--create-admin'
}

# The default flow creates missing tables and roles, then asks interactively for
# administrator details. It exits without starting the Web host or workers.
& $dotnet.Source run --project $projectPath --configuration $Configuration -- @applicationArguments
exit $LASTEXITCODE
