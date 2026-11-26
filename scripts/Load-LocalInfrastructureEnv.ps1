param(
    [string] $Path = (Join-Path $PSScriptRoot '..\.env')
)

$ErrorActionPreference = 'Stop'
$envFile = [System.IO.Path]::GetFullPath($Path)

if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    throw 'The root .env file is missing. Run scripts/New-LocalInfrastructureEnv.ps1 first.'
}

$secrets = @{}
foreach ($line in [System.IO.File]::ReadAllLines($envFile)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -lt 1) { continue }
    $name = $line.Substring(0, $separator).Trim()
    if ($name -in @('POSTGRES_PASSWORD', 'REDIS_PASSWORD', 'RABBITMQ_DEFAULT_PASS')) {
        $secrets[$name] = $line.Substring($separator + 1).Trim()
    }
}

foreach ($name in @('POSTGRES_PASSWORD', 'REDIS_PASSWORD', 'RABBITMQ_DEFAULT_PASS')) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        throw "The root .env must contain a non-empty value for $name."
    }
}

$redisPassword = $secrets['REDIS_PASSWORD']
if ($redisPassword.Contains(',')) {
    throw 'The root .env value for REDIS_PASSWORD cannot contain a comma because Redis connection strings use commas as separators.'
}

$database = New-Object System.Data.Common.DbConnectionStringBuilder
# Use CLR accessors: PowerShell treats this builder as IDictionary for property access.
$database.set_ConnectionString('Host=127.0.0.1;Port=5432;Database=shinemusic;Username=shinemusic')
$database['Password'] = $secrets['POSTGRES_PASSWORD']

[Environment]::SetEnvironmentVariable(
    'Database__ConnectionString',
    $database.get_ConnectionString(),
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'Redis__ConnectionString',
    "127.0.0.1:6379,password=$($secrets['REDIS_PASSWORD'])",
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'RabbitMQ__HostName',
    '127.0.0.1',
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'RabbitMQ__Password',
    $secrets['RABBITMQ_DEFAULT_PASS'],
    [EnvironmentVariableTarget]::Process)

Write-Host 'Loaded local infrastructure credentials into this PowerShell process. Start the API and Worker from this window.'
