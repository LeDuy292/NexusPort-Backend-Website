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

    if ($value.Length -ge 2 -and (
        ($value.StartsWith('"') -and $value.EndsWith('"')) -or
        ($value.StartsWith("'") -and $value.EndsWith("'")))) {
        $value = $value.Substring(1, $value.Length - 2)
    }

    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}

$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
if (-not [string]::IsNullOrWhiteSpace($env:DATABASE_URL)) {
    $databaseUri = $null
    if (-not [uri]::TryCreate($env:DATABASE_URL, [UriKind]::Absolute, [ref]$databaseUri) -or
        $databaseUri.Scheme -notin @('postgres', 'postgresql') -or
        -not $databaseUri.Host -or $databaseUri.AbsolutePath -in @('', '/')) {
        throw "DATABASE_URL in $envPath must be a PostgreSQL URL with a host and database name."
    }

    $credentials = $databaseUri.UserInfo -split ':', 2
    $connection['Host'] = $databaseUri.Host
    $connection['Port'] = if ($databaseUri.Port -gt 0) { $databaseUri.Port } else { 5432 }
    $connection['Database'] = [uri]::UnescapeDataString($databaseUri.AbsolutePath.TrimStart('/'))
    $connection['Username'] = [uri]::UnescapeDataString($credentials[0])
    if ($credentials.Length -eq 2) {
        $connection['Password'] = [uri]::UnescapeDataString($credentials[1])
    }

    $sslMode = $env:DB_SSL_MODE
    if ($databaseUri.Query -match '[?&]sslmode=([^&]+)') {
        $sslMode = $Matches[1]
    }
    if (-not [string]::IsNullOrWhiteSpace($sslMode)) {
        $connection['SSL Mode'] = $sslMode.Replace('-', '')
    }
} else {
    $requiredAivenVariables = @('AIVEN_DB_HOST', 'AIVEN_DB_PORT', 'AIVEN_DB_NAME', 'AIVEN_DB_USER', 'AIVEN_DB_PASSWORD')
    $missingVariables = $requiredAivenVariables | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_, 'Process'))
    }
    if ($missingVariables) {
        throw "Set DATABASE_URL in $envPath, or provide all Aiven settings. Missing: $($missingVariables -join ', ')"
    }

    $connection['Host'] = $env:AIVEN_DB_HOST
    $connection['Port'] = $env:AIVEN_DB_PORT
    $connection['Database'] = $env:AIVEN_DB_NAME
    $connection['Username'] = $env:AIVEN_DB_USER
    $connection['Password'] = $env:AIVEN_DB_PASSWORD
    $connection['SSL Mode'] = 'Require'
    $connection['Trust Server Certificate'] = $true
}

$connection['Maximum Pool Size'] = if ([string]::IsNullOrWhiteSpace($env:DB_POOL_MAX)) { 3 } else { $env:DB_POOL_MAX }
# Windows PowerShell adapts dictionary members as keys. Read the CLR property.
$env:ConnectionStrings__DefaultConnection = $connection.psbase.ConnectionString

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

Write-Host "Starting NexusPort.Api with PostgreSQL ($($connection['Database']))..."
# This script loads the checkout's local settings. A deployed API can explicitly
# choose Production and --no-launch-profile instead.
if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_ENVIRONMENT)) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
}
& dotnet run --project $projectPath --no-launch-profile
exit $LASTEXITCODE
