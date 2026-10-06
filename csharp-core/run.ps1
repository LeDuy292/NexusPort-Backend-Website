$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'
$projectPath = Join-Path $PSScriptRoot 'src\NexusPort.Api\NexusPort.Api.csproj'

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "Missing local environment file: $envPath"
}

Get-Content -LiteralPath $envPath | ForEach-Object {
    $line = $_.Trim()
    if (-not $line -or $line.StartsWith('#')) {
        return
    }

    $separatorIndex = $line.IndexOf('=')
    if ($separatorIndex -lt 1) {
        return
    }

    $name = $line.Substring(0, $separatorIndex).Trim()
    $value = $line.Substring($separatorIndex + 1).Trim()

    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
        ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    }

    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}

$requiredVariables = @(
    'AIVEN_DB_HOST',
    'AIVEN_DB_PORT',
    'AIVEN_DB_NAME',
    'AIVEN_DB_USER',
    'AIVEN_DB_PASSWORD'
)

$missingVariables = $requiredVariables | Where-Object {
    [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_, 'Process'))
}

if ($missingVariables.Count -gt 0) {
    throw "Missing Aiven settings in .env: $($missingVariables -join ', ')"
}

$hostName = [Environment]::GetEnvironmentVariable('AIVEN_DB_HOST', 'Process')
$port = [Environment]::GetEnvironmentVariable('AIVEN_DB_PORT', 'Process')
$database = [Environment]::GetEnvironmentVariable('AIVEN_DB_NAME', 'Process')
$username = [Environment]::GetEnvironmentVariable('AIVEN_DB_USER', 'Process')
$password = [Environment]::GetEnvironmentVariable('AIVEN_DB_PASSWORD', 'Process')
$poolSize = [Environment]::GetEnvironmentVariable('DB_POOL_MAX', 'Process')

if ([string]::IsNullOrWhiteSpace($poolSize)) {
    $poolSize = '5'
}

$connectionString = "Host=$hostName;Port=$port;Database=$database;Username=$username;Password=$password;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=$poolSize"
[Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $connectionString, 'Process')

Write-Host "Starting NexusPort.Api with Aiven PostgreSQL ($database)..."
& dotnet run --project $projectPath
exit $LASTEXITCODE
