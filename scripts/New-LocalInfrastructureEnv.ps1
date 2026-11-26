param(
    [string] $Path = (Join-Path $PSScriptRoot '..\.env')
)

$resolvedPath = [System.IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath $resolvedPath) {
    throw "Refusing to overwrite existing environment file: $resolvedPath"
}

$names = @('POSTGRES_PASSWORD', 'REDIS_PASSWORD', 'RABBITMQ_DEFAULT_PASS')
$lines = foreach ($name in $names) {
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 32
    try {
        $generator.GetBytes($bytes)
        "$name=$([BitConverter]::ToString($bytes).Replace('-', ''))"
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $generator.Dispose()
    }
}

[System.IO.File]::WriteAllLines($resolvedPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Created $resolvedPath with three distinct random secrets. Keep this file private."
