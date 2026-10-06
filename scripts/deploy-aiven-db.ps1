[CmdletBinding()]
param(
    [string]$SourceEnvFile = ".env",
    [string]$BackupDirectory = "..\..\db-backups",
    [switch]$Restore
)

$ErrorActionPreference = "Stop"

function Resolve-PostgresTool {
    param([Parameter(Mandatory)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $installed = Get-ChildItem "C:\Program Files\PostgreSQL" -Recurse -Filter "$Name.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\bin\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $installed) {
        throw "Cannot find $Name. Install PostgreSQL command-line tools first."
    }

    return $installed.FullName
}

function Read-DotEnv {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Source environment file not found: $Path"
    }

    $values = @{}
    Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            $values[$matches[1].Trim()] = $matches[2].Trim()
        }
    }

    foreach ($required in @("DB_HOST", "DB_PORT", "DB_NAME", "DB_USER", "DB_PASSWORD")) {
        if (-not $values.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($values[$required])) {
            throw "Missing $required in $Path"
        }
    }

    return $values
}

function ConvertFrom-SecureValue {
    param([Parameter(Mandatory)][Security.SecureString]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$pgDump = Resolve-PostgresTool "pg_dump"
$pgRestore = Resolve-PostgresTool "pg_restore"
$psql = Resolve-PostgresTool "psql"
$source = Read-DotEnv $SourceEnvFile

$resolvedBackupDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $BackupDirectory))
New-Item -ItemType Directory -Path $resolvedBackupDirectory -Force | Out-Null
$backupPath = Join-Path $resolvedBackupDirectory ("nexusport-pre-aiven-{0}.backup" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

try {
    $env:PGPASSWORD = $source.DB_PASSWORD
    & $pgDump `
        -h $source.DB_HOST `
        -p $source.DB_PORT `
        -U $source.DB_USER `
        -d $source.DB_NAME `
        --format=custom `
        --compress=9 `
        --no-owner `
        --no-privileges `
        --file=$backupPath

    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "Backup created: $backupPath"

if (-not $Restore) {
    Write-Host "Backup-only mode complete. Re-run with -Restore to deploy to Aiven."
    exit 0
}

$targetUrl = $env:NEXUSPORT_DEPLOY_DATABASE_URL
if ([string]::IsNullOrWhiteSpace($targetUrl)) {
    $aivenKeys = @("AIVEN_DB_HOST", "AIVEN_DB_PORT", "AIVEN_DB_NAME", "AIVEN_DB_USER", "AIVEN_DB_PASSWORD")
    $hasAivenConfig = ($aivenKeys | Where-Object { -not $source.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($source[$_]) }).Count -eq 0

    if ($hasAivenConfig) {
        $escapedUser = [Uri]::EscapeDataString($source.AIVEN_DB_USER)
        $escapedPassword = [Uri]::EscapeDataString($source.AIVEN_DB_PASSWORD)
        $targetUrl = "postgresql://${escapedUser}:${escapedPassword}@$($source.AIVEN_DB_HOST):$($source.AIVEN_DB_PORT)/$($source.AIVEN_DB_NAME)"
    }
    else {
        $secureUrl = Read-Host "Paste the Aiven Service URI" -AsSecureString
        $targetUrl = ConvertFrom-SecureValue $secureUrl
    }
}

try {
    $targetUri = [Uri]$targetUrl
    $credentials = $targetUri.UserInfo -split ':', 2
    if ($targetUri.Scheme -notin @("postgres", "postgresql") -or $credentials.Count -ne 2) {
        throw "The Aiven Service URI is not a valid PostgreSQL connection string."
    }

    $targetUser = [Uri]::UnescapeDataString($credentials[0])
    $targetPassword = [Uri]::UnescapeDataString($credentials[1])
    $targetDatabase = $targetUri.AbsolutePath.TrimStart('/')

    $env:PGPASSWORD = $targetPassword
    $env:PGSSLMODE = if ($source.AIVEN_DB_SSLMODE) { $source.AIVEN_DB_SSLMODE } else { "require" }

    $targetInfo = & $psql -X -A -t `
        -h $targetUri.Host `
        -p $targetUri.Port `
        -U $targetUser `
        -d $targetDatabase `
        -v ON_ERROR_STOP=1 `
        -c "SELECT current_setting('server_version_num'), count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';"

    if ($LASTEXITCODE -ne 0) {
        throw "Could not connect to the Aiven database."
    }

    $targetParts = $targetInfo.Trim() -split '\|', 2
    $targetMajor = [Math]::Floor(([int]$targetParts[0]) / 10000)
    $targetTableCount = [int]$targetParts[1]
    $sourceMajor = 18

    if ($targetMajor -lt $sourceMajor) {
        throw "Target PostgreSQL major version $targetMajor is older than source version $sourceMajor. Create an Aiven PostgreSQL $sourceMajor service or perform a reviewed downgrade migration."
    }

    if ($targetTableCount -gt 0) {
        throw "Safety stop: target database already contains $targetTableCount public tables. This script only restores into an empty database."
    }

    & $pgRestore `
        -h $targetUri.Host `
        -p $targetUri.Port `
        -U $targetUser `
        -d $targetDatabase `
        --no-owner `
        --no-privileges `
        --exit-on-error `
        $backupPath

    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE"
    }

    & $psql -X `
        -h $targetUri.Host `
        -p $targetUri.Port `
        -U $targetUser `
        -d $targetDatabase `
        -v ON_ERROR_STOP=1 `
        -c "SELECT version, description, applied_at FROM nexusport_schema_versions ORDER BY version;" `
        -c "SELECT pg_size_pretty(pg_database_size(current_database())) AS database_size, count(*) AS public_tables FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';"

    if ($LASTEXITCODE -ne 0) {
        throw "Post-restore verification failed."
    }

    Write-Host "Aiven restore and schema verification completed successfully."
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGSSLMODE -ErrorAction SilentlyContinue
    $targetUrl = $null
    $targetPassword = $null
}
