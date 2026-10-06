[CmdletBinding()]
param(
    [string]$MigrationsDirectory = "..\database\migrations",
    [string]$TargetEnvFile = ".env",
    [ValidateSet("require", "prefer", "disable")]
    [string]$SslMode = "require"
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

function Read-DotEnv {
    param([Parameter(Mandatory)][string]$Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path)) {
        return $values
    }

    Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            $values[$matches[1].Trim()] = $matches[2].Trim()
        }
    }

    return $values
}

$psql = Resolve-PostgresTool "psql"
$resolvedMigrationsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $MigrationsDirectory))
$migrationFiles = Get-ChildItem -LiteralPath $resolvedMigrationsDirectory -Filter "*.sql" -File |
    Sort-Object Name

if (-not $migrationFiles) {
    throw "No SQL migrations found in $resolvedMigrationsDirectory"
}

$targetUrl = $env:NEXUSPORT_DEPLOY_DATABASE_URL
$targetConfig = Read-DotEnv $TargetEnvFile
if ([string]::IsNullOrWhiteSpace($targetUrl)) {
    $aivenKeys = @("AIVEN_DB_HOST", "AIVEN_DB_PORT", "AIVEN_DB_NAME", "AIVEN_DB_USER", "AIVEN_DB_PASSWORD")
    $hasAivenConfig = ($aivenKeys | Where-Object { -not $targetConfig.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($targetConfig[$_]) }).Count -eq 0

    if ($hasAivenConfig) {
        $escapedUser = [Uri]::EscapeDataString($targetConfig.AIVEN_DB_USER)
        $escapedPassword = [Uri]::EscapeDataString($targetConfig.AIVEN_DB_PASSWORD)
        $targetUrl = "postgresql://${escapedUser}:${escapedPassword}@$($targetConfig.AIVEN_DB_HOST):$($targetConfig.AIVEN_DB_PORT)/$($targetConfig.AIVEN_DB_NAME)"
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
    $effectiveSslMode = if (-not $PSBoundParameters.ContainsKey("SslMode") -and $targetConfig.AIVEN_DB_SSLMODE) {
        $targetConfig.AIVEN_DB_SSLMODE
    }
    else {
        $SslMode
    }
    $env:PGSSLMODE = $effectiveSslMode

    $versionTableExists = & $psql -X -A -t `
        -h $targetUri.Host `
        -p $targetUri.Port `
        -U $targetUser `
        -d $targetDatabase `
        -v ON_ERROR_STOP=1 `
        -c "SELECT to_regclass('public.nexusport_schema_versions') IS NOT NULL;"

    if ($LASTEXITCODE -ne 0) {
        throw "Could not connect to the target database."
    }

    $appliedVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($versionTableExists.Trim() -eq "t") {
        $versionRows = & $psql -X -A -t `
            -h $targetUri.Host `
            -p $targetUri.Port `
            -U $targetUser `
            -d $targetDatabase `
            -v ON_ERROR_STOP=1 `
            -c "SELECT version FROM nexusport_schema_versions ORDER BY version;"

        if ($LASTEXITCODE -ne 0) {
            throw "Could not read nexusport_schema_versions."
        }

        $versionRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            [void]$appliedVersions.Add($_.Trim())
        }
    }

    $appliedCount = 0
    foreach ($migrationFile in $migrationFiles) {
        if ($migrationFile.BaseName -notmatch '^(\d{8}_\d{3})_') {
            throw "Migration filename does not start with YYYYMMDD_NNN_: $($migrationFile.Name)"
        }

        $version = $matches[1]
        if ($appliedVersions.Contains($version)) {
            Write-Host "SKIP  $version ($($migrationFile.Name))"
            continue
        }

        Write-Host "APPLY $version ($($migrationFile.Name))"
        & $psql -X `
            -h $targetUri.Host `
            -p $targetUri.Port `
            -U $targetUser `
            -d $targetDatabase `
            -v ON_ERROR_STOP=1 `
            -f $migrationFile.FullName

        if ($LASTEXITCODE -ne 0) {
            throw "Migration $version failed. Later migrations were not attempted."
        }

        $verified = & $psql -X -A -t `
            -h $targetUri.Host `
            -p $targetUri.Port `
            -U $targetUser `
            -d $targetDatabase `
            -v ON_ERROR_STOP=1 `
            -c "SELECT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '$version');"

        if ($LASTEXITCODE -ne 0 -or $verified.Trim() -ne "t") {
            throw "Migration $version ran but was not recorded in nexusport_schema_versions."
        }

        [void]$appliedVersions.Add($version)
        $appliedCount++
    }

    Write-Host "Migration run complete. Applied: $appliedCount; already present: $($migrationFiles.Count - $appliedCount)."
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGSSLMODE -ErrorAction SilentlyContinue
    $targetUrl = $null
    $targetPassword = $null
}
