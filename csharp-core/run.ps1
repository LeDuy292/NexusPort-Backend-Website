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

$databaseUrl = [Environment]::GetEnvironmentVariable('DATABASE_URL', 'Process')
$poolSize = [Environment]::GetEnvironmentVariable('DB_POOL_MAX', 'Process')
if ([string]::IsNullOrWhiteSpace($poolSize)) {
    $poolSize = '5'
}

if (-not [string]::IsNullOrWhiteSpace($databaseUrl) -and [uri]::TryCreate($databaseUrl.Trim(), [UriKind]::Absolute, [ref]$null)) {
    $uri = [uri]$databaseUrl.Trim()
    $userInfo = $uri.UserInfo.Split(':', 2)
    $username = if ($userInfo.Length -gt 0) { [uri]::UnescapeDataString($userInfo[0]) } else { '' }
    $password = if ($userInfo.Length -gt 1) { [uri]::UnescapeDataString($userInfo[1]) } else { '' }
    $port = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
    $database = $uri.AbsolutePath.TrimStart('/')
    $disableSsl = $uri.Query -like '*sslmode=disable*'
    $sslPart = if ($disableSsl) { 'SSL Mode=Disable' } else { 'SSL Mode=Require;Trust Server Certificate=true' }
    $connectionString = "Host=$($uri.Host);Port=$port;Database=$database;Username=$username;Password=$password;$sslPart;Maximum Pool Size=$poolSize;Connection Idle Lifetime=15;Connection Pruning Interval=5"
} else {
    $hostName = [Environment]::GetEnvironmentVariable('DB_HOST', 'Process')
    if ([string]::IsNullOrWhiteSpace($hostName)) { $hostName = 'localhost' }
    $port = [Environment]::GetEnvironmentVariable('DB_PORT', 'Process')
    if ([string]::IsNullOrWhiteSpace($port)) { $port = '5432' }
    $database = [Environment]::GetEnvironmentVariable('DB_NAME', 'Process')
    if ([string]::IsNullOrWhiteSpace($database)) { $database = 'nexusport' }
    $username = [Environment]::GetEnvironmentVariable('DB_USER', 'Process')
    if ([string]::IsNullOrWhiteSpace($username)) { $username = 'postgres' }
    $password = [Environment]::GetEnvironmentVariable('DB_PASSWORD', 'Process')
    $sslMode = [Environment]::GetEnvironmentVariable('DB_SSL_MODE', 'Process')
    if ([string]::IsNullOrWhiteSpace($sslMode)) { $sslMode = 'Disable' }
    $connectionString = "Host=$hostName;Port=$port;Database=$database;Username=$username;Password=$password;SSL Mode=$sslMode;Maximum Pool Size=$poolSize;Connection Idle Lifetime=15;Connection Pruning Interval=5"
}

[Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $connectionString, 'Process')

# Hand a previous run of this checkout over to this terminal. Never stop a
# different application just because it happens to use the configured port.
$listenUrls = [Environment]::GetEnvironmentVariable('ASPNETCORE_URLS', 'Process')
if ([string]::IsNullOrWhiteSpace($listenUrls)) {
    $listenUrls = 'http://localhost:5000'
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $listenUrls, 'Process')
}
$apiExecutable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'src\NexusPort.Api\bin\Debug\net8.0\NexusPort.Api.exe'))
$listenPorts = @($listenUrls.Split(';') | ForEach-Object { ([uri]$_).Port } | Sort-Object -Unique)
foreach ($listenPort in $listenPorts) {
    $owners = @(Get-NetTCPConnection -LocalPort $listenPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($ownerId in $owners) {
        $apiProcess = Get-Process -Id $ownerId -ErrorAction Stop
        if (-not [string]::Equals($apiProcess.Path, $apiExecutable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Port $listenPort is used by $($apiProcess.ProcessName) (PID $ownerId). It is not this workspace API; stop it or choose another ASPNETCORE_URLS."
        }
        Write-Host "Restarting this workspace's NexusPort.Api (PID $ownerId, port $listenPort)..."
        Stop-Process -InputObject $apiProcess -ErrorAction Stop
        if (-not $apiProcess.WaitForExit(10000)) {
            throw "Previous NexusPort.Api did not stop. Retry after PID $ownerId has exited."
        }
    }
}

Write-Host "Starting NexusPort.Api with PostgreSQL ($database)..."
# This script loads the checkout's local settings. A deployed API can explicitly
# choose Production and --no-launch-profile instead.
if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_ENVIRONMENT)) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
}
& dotnet run --project $projectPath --no-launch-profile
exit $LASTEXITCODE
