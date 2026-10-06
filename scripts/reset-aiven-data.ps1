[CmdletBinding()]
param(
    [switch]$ConfirmReset,
    [string]$TargetEnvFile = ".env",
    [string]$BackupDirectory = "..\..\db-backups"
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmReset) {
    throw "Destructive operation blocked. Re-run with -ConfirmReset after reviewing the target and backup location."
}

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
        throw "Target environment file not found: $Path"
    }

    $values = @{}
    Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            $values[$matches[1].Trim()] = $matches[2].Trim()
        }
    }

    foreach ($required in @("AIVEN_DB_HOST", "AIVEN_DB_PORT", "AIVEN_DB_NAME", "AIVEN_DB_USER", "AIVEN_DB_PASSWORD")) {
        if (-not $values.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($values[$required])) {
            throw "Missing $required in $Path"
        }
    }

    return $values
}

$pgDump = Resolve-PostgresTool "pg_dump"
$psql = Resolve-PostgresTool "psql"
$target = Read-DotEnv $TargetEnvFile
$resetSql = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\database\maintenance\reset_to_default_accounts.sql"))
$resolvedBackupDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $BackupDirectory))
New-Item -ItemType Directory -Path $resolvedBackupDirectory -Force | Out-Null
$backupPath = Join-Path $resolvedBackupDirectory ("nexusport-aiven-before-default-seed-{0}.backup" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

try {
    $env:PGPASSWORD = $target.AIVEN_DB_PASSWORD
    $env:PGSSLMODE = if ($target.AIVEN_DB_SSLMODE) { $target.AIVEN_DB_SSLMODE } else { "require" }

    $identity = & $psql -X -A -t `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        -v ON_ERROR_STOP=1 `
        -c "SELECT current_database(), count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE';"

    if ($LASTEXITCODE -ne 0) {
        throw "Could not connect to the configured Aiven database."
    }

    $identityParts = $identity.Trim() -split '\|', 2
    if ($identityParts[0] -ne $target.AIVEN_DB_NAME -or [int]$identityParts[1] -lt 1) {
        throw "Target verification failed. Refusing to reset an unexpected or empty database."
    }

    & $pgDump `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        --format=custom `
        --compress=9 `
        --no-owner `
        --no-privileges `
        --file=$backupPath

    if ($LASTEXITCODE -ne 0) {
        throw "Safety backup failed. No data was deleted."
    }

    Write-Host "Safety backup created: $backupPath"

    & $psql -X `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        -v ON_ERROR_STOP=1 `
        -f $resetSql

    if ($LASTEXITCODE -ne 0) {
        throw "Reset failed. Use the safety backup if the transaction did not roll back cleanly."
    }

    & $psql -X `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        -v ON_ERROR_STOP=1 `
        -c "SELECT username, role, is_active FROM users ORDER BY username;" `
        -c "SELECT (SELECT count(*) FROM users) AS users, (SELECT count(*) FROM carriers) AS carriers, (SELECT count(*) FROM carrier_users) AS carrier_users, (SELECT count(*) FROM nexusport_schema_versions) AS schema_versions;" `
        -c "SELECT 'bookings' AS table_name, count(*) FROM bookings UNION ALL SELECT 'containers', count(*) FROM containers UNION ALL SELECT 'drivers', count(*) FROM drivers UNION ALL SELECT 'vessels', count(*) FROM vessels UNION ALL SELECT 'yard_blocks', count(*) FROM yard_blocks ORDER BY table_name;"

    if ($LASTEXITCODE -ne 0) {
        throw "Post-reset verification failed."
    }

    $unexpectedData = & $psql -X -A -t `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        -v ON_ERROR_STOP=1 `
        -c "CREATE TEMP TABLE reset_nonempty(table_name text, row_count bigint); DO `$verify`$ DECLARE item record; rows_found bigint; BEGIN FOR item IN SELECT schemaname, tablename FROM pg_tables WHERE schemaname = 'public' AND tablename NOT IN ('users', 'carriers', 'carrier_users', 'nexusport_schema_versions') LOOP EXECUTE format('SELECT count(*) FROM %I.%I', item.schemaname, item.tablename) INTO rows_found; IF rows_found > 0 THEN INSERT INTO reset_nonempty VALUES (item.tablename, rows_found); END IF; END LOOP; END `$verify`$; SELECT table_name || ':' || row_count FROM reset_nonempty ORDER BY table_name;"

    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify all application tables after reset."
    }
    if (-not [string]::IsNullOrWhiteSpace(($unexpectedData -join ""))) {
        throw "Unexpected application data remains after reset: $($unexpectedData -join ', ')"
    }

    $validPasswords = & $psql -X -A -t `
        -h $target.AIVEN_DB_HOST `
        -p $target.AIVEN_DB_PORT `
        -U $target.AIVEN_DB_USER `
        -d $target.AIVEN_DB_NAME `
        -v ON_ERROR_STOP=1 `
        -c "SELECT count(*) FROM users WHERE crypt('NexusPort@2026', password) = password;"

    if ($LASTEXITCODE -ne 0 -or [int]$validPasswords.Trim() -ne 7) {
        throw "Default account password verification failed."
    }

    Write-Host "Reset verification complete: no unexpected application data; 7 default account passwords verified."
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGSSLMODE -ErrorAction SilentlyContinue
}
