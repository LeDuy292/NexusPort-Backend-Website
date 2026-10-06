[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$envFile = Join-Path $repositoryRoot ".env"
$apiProject = Join-Path $repositoryRoot "csharp-core\src\NexusPort.Api\NexusPort.Api.csproj"

if (-not (Test-Path -LiteralPath $envFile)) {
    throw "Local .env file not found: $envFile"
}

$settings = @{}
Get-Content -LiteralPath $envFile | ForEach-Object {
    if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
        $settings[$matches[1].Trim()] = $matches[2].Trim()
    }
}

foreach ($required in @("AIVEN_DB_HOST", "AIVEN_DB_PORT", "AIVEN_DB_NAME", "AIVEN_DB_USER", "AIVEN_DB_PASSWORD")) {
    if (-not $settings.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($settings[$required])) {
        throw "Missing $required in $envFile"
    }
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ConnectionStrings__DefaultConnection = "Host=$($settings.AIVEN_DB_HOST);Port=$($settings.AIVEN_DB_PORT);Database=$($settings.AIVEN_DB_NAME);Username=$($settings.AIVEN_DB_USER);Password=$($settings.AIVEN_DB_PASSWORD);SSL Mode=Require;Maximum Pool Size=8"

try {
    & dotnet run --project $apiProject
}
finally {
    Remove-Item Env:ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
}
