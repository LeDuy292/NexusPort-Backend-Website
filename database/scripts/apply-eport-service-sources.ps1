param([switch]$Apply, [switch]$SeedSamples, [switch]$ValidateSamples)
$ErrorActionPreference = 'Stop'
$serviceSourceRepo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$serviceSourcePsql = 'C:\Program Files\PostgreSQL\17\bin\psql.exe'
if (($SeedSamples -or $ValidateSamples) -and -not $Apply) { throw 'Use -Apply with the explicit development sample option.' }
if ($SeedSamples -and $ValidateSamples) { throw 'Choose either sample validation with rollback or sample insertion.' }
$serviceSourceEnv = @{}
foreach ($serviceSourceLine in Get-Content -LiteralPath (Join-Path $serviceSourceRepo '.env')) {
 if ($serviceSourceLine -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { $serviceSourceEnv[$matches[1]]=$matches[2].Trim().Trim('"').Trim("'") }
}
if (($SeedSamples -or $ValidateSamples) -and $serviceSourceEnv['NODE_ENV'] -ne 'development') { throw 'Samples are allowed only for the configured development environment.' }
$serviceSourceConfig = Get-Content -LiteralPath (Join-Path $serviceSourceRepo 'csharp-core\src\NexusPort.Api\appsettings.Development.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$serviceSourceDb = [System.Data.Common.DbConnectionStringBuilder]::new()
$serviceSourceDb.set_ConnectionString($serviceSourceConfig.ConnectionStrings.DefaultConnection)
$env:PGHOST=$serviceSourceDb['host']; if(-not $env:PGHOST){$env:PGHOST=$serviceSourceDb['server']}
$env:PGPORT=$serviceSourceDb['port']; if(-not $env:PGPORT){$env:PGPORT='5432'}
$env:PGDATABASE=$serviceSourceDb['database']; $env:PGUSER=$serviceSourceDb['username']; if(-not $env:PGUSER){$env:PGUSER=$serviceSourceDb['user id']}
$env:PGPASSWORD=$serviceSourceDb['password']; $env:PGSSLMODE='require'; $env:PGCONNECT_TIMEOUT='12'; $env:PGCLIENTENCODING='UTF8'
if ($serviceSourceEnv['DB_TARGET'] -ne 'aiven' -or $env:PGHOST -ne $serviceSourceEnv['AIVEN_DB_HOST'] -or $env:PGDATABASE -ne $serviceSourceEnv['AIVEN_DB_NAME']) { throw 'C# target must match the configured development database. No alternate target is chosen.' }
$serviceSourcePsqlArgs = @('--no-password','-X','-q','-t','-A','-v','ON_ERROR_STOP=1')
function Read-ServiceSourceJson([string]$Query) {
 $serviceSourceResult = & $serviceSourcePsql @serviceSourcePsqlArgs -c "BEGIN READ ONLY; $Query ROLLBACK;" 2>&1
 if ($LASTEXITCODE -ne 0) { throw 'Service source read-only query failed; connection details withheld.' }
 return (($serviceSourceResult -join '') | ConvertFrom-Json)
}
try {
 $serviceSourceBefore = Read-ServiceSourceJson @'
SELECT json_build_object(
 'versions',(SELECT json_agg(version ORDER BY version) FROM nexusport_schema_versions),
 'bookingColumns',(SELECT json_agg(column_name) FROM information_schema.columns WHERE table_schema='public' AND table_name='export_bookings'),
 'companyCount',(SELECT count(DISTINCT cu.carrier_id) FROM carrier_users cu JOIN users u ON u.id=cu.user_id WHERE u.is_active AND u.role IN ('Transport Company','Carrier','Carrier Staff') AND (SELECT count(*) FROM carrier_users x WHERE x.user_id=u.id)=1)
)::text;
'@
 $serviceSourceMissing = @(Get-ChildItem -LiteralPath (Join-Path $serviceSourceRepo 'database\migrations') -Filter '*.sql' | Sort-Object Name | Where-Object {
   $serviceSourceVersion = ($_.BaseName.Split('_')[0..1] -join '_')
   $serviceSourceBefore.versions -notcontains $serviceSourceVersion -and -not ($serviceSourceVersion.EndsWith('_005') -and $serviceSourceBefore.versions -contains '005')
 } | Select-Object -ExpandProperty Name)
 [pscustomobject]@{Mode='Read-only inspection';MissingMigrations=$serviceSourceMissing;CompanyLinkPresent=($serviceSourceBefore.bookingColumns -contains 'carrier_id');EligibleCompanies=$serviceSourceBefore.companyCount} | ConvertTo-Json -Depth 3
 if (-not $Apply) { return }
 if ($serviceSourceMissing.Count -gt 0 -and ($serviceSourceMissing.Count -ne 1 -or $serviceSourceMissing[0] -ne '20261109_038_restore_service_booking_source.sql')) { throw 'Unexpected missing prerequisites; no schema or sample writes performed.' }
 if (($SeedSamples -or $ValidateSamples) -and ($serviceSourceBefore.companyCount -lt 1 -or $serviceSourceBefore.companyCount -gt 10)) { throw 'Expected one through ten explicitly linked development companies.' }
 $serviceSourceNewColumns = @('carrier_id','line_user_id','request_id','request_hash') | Where-Object { $serviceSourceBefore.bookingColumns -notcontains $_ }
 $serviceSourceExcluded = if ($serviceSourceNewColumns.Count) { 'ARRAY[' + (($serviceSourceNewColumns | ForEach-Object { "'$_'" }) -join ',') + ']::text[]' } else { 'ARRAY[]::text[]' }
 $serviceSourceFingerprints = @{}
 foreach ($serviceSourceTable in @('users','carriers','carrier_users','shipping_lines','container_types','containers','container_declarations','container_visits','container_visit_access','bookings','export_voyages','export_bookings','export_booking_containers','eport_services','eport_tariffs','eport_service_drafts','eport_service_registrations')) {
   $serviceSourceRemove = if ($serviceSourceTable -eq 'export_bookings') { $serviceSourceExcluded } else { 'ARRAY[]::text[]' }
   # Store only row hashes. No credentials, customer cargo or source documents are exported.
   $serviceSourceHashSnapshot = Read-ServiceSourceJson "SELECT json_build_object('hashes',COALESCE(json_agg(md5((to_jsonb(t)-$serviceSourceRemove)::text) ORDER BY md5((to_jsonb(t)-$serviceSourceRemove)::text)),'[]'::json))::text FROM $serviceSourceTable t;"
   $serviceSourceFingerprints[$serviceSourceTable] = @($serviceSourceHashSnapshot.hashes)
 }
 $serviceSourceMigration = Join-Path $serviceSourceRepo 'database\migrations\20261109_038_restore_service_booking_source.sql'
 if ($serviceSourceBefore.versions -notcontains '20261109_038') {
   & $serviceSourcePsql @serviceSourcePsqlArgs -f $serviceSourceMigration
   if ($LASTEXITCODE -ne 0) { throw 'Source repair migration038 failed.' }
 }
 $serviceSourceAfter = Read-ServiceSourceJson "SELECT json_build_object('recorded',EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261109_038'),'companyLink',EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='export_bookings' AND column_name='carrier_id'))::text;"
 if (-not $serviceSourceAfter.recorded -or -not $serviceSourceAfter.companyLink) { throw 'Migration038/source contract verification failed.' }
 if ($SeedSamples -or $ValidateSamples) {
   $serviceSourceSamplePath = Join-Path $serviceSourceRepo 'database\samples\20261109_eport_service_sources.sql'
   if ($ValidateSamples) {
     $serviceSourceSampleSql = Get-Content -LiteralPath $serviceSourceSamplePath -Raw -Encoding UTF8
     if ([regex]::Matches($serviceSourceSampleSql,'(?m)^COMMIT;\s*$').Count -ne 1 -or $serviceSourceSampleSql -notmatch '(?s)COMMIT;\s*\z') { throw 'Expected one final COMMIT for guarded rollback validation.' }
     $serviceSourceSamplePath = Join-Path $serviceSourceRepo '..\.catalog-work\validate-service-samples-rollback.sql'
     [regex]::Replace($serviceSourceSampleSql,'(?m)^COMMIT;\s*$','ROLLBACK;') | Set-Content -LiteralPath $serviceSourceSamplePath -Encoding UTF8
   }
   & $serviceSourcePsql @serviceSourcePsqlArgs -c "SET nexusport.allow_service_samples='development';" -f $serviceSourceSamplePath
   if ($LASTEXITCODE -ne 0) { throw 'Development sample transaction failed; inspect the reported SQL error before retry.' }
 }
 foreach ($serviceSourceTable in $serviceSourceFingerprints.Keys) {
   $serviceSourceRemove = if ($serviceSourceTable -eq 'export_bookings') { $serviceSourceExcluded } else { 'ARRAY[]::text[]' }
   $serviceSourceHashSnapshot = Read-ServiceSourceJson "SELECT json_build_object('hashes',COALESCE(json_agg(md5((to_jsonb(t)-$serviceSourceRemove)::text)),'[]'::json))::text FROM $serviceSourceTable t;"
   $serviceSourceCurrentHashes = @($serviceSourceHashSnapshot.hashes)
   foreach ($serviceSourceHash in @($serviceSourceFingerprints[$serviceSourceTable])) {
     if ($serviceSourceCurrentHashes -notcontains $serviceSourceHash) { throw "An existing row changed unexpectedly in $serviceSourceTable." }
   }
 }
 $serviceSourceReceipt = @{migration='20261109_038';applied=$true;developmentSamples=$SeedSamples.IsPresent;samplesValidatedWithRollback=$ValidateSamples.IsPresent;existingRowsPreserved=$true;sha256=(Get-FileHash -LiteralPath $serviceSourceMigration -Algorithm SHA256).Hash}
 $serviceSourceReceiptName = if ($ValidateSamples) { 'schema-038-validation-receipt.json' } else { 'schema-038-receipt.json' }
 $serviceSourceReceipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $serviceSourceRepo ('..\.catalog-work\'+$serviceSourceReceiptName)) -Encoding UTF8
 $serviceSourceReceipt | ConvertTo-Json
} finally {
 foreach ($serviceSourceEnvName in @('PGHOST','PGPORT','PGDATABASE','PGUSER','PGPASSWORD','PGSSLMODE','PGCONNECT_TIMEOUT','PGCLIENTENCODING')) { Remove-Item -LiteralPath ('Env:'+$serviceSourceEnvName) -ErrorAction SilentlyContinue }
}
