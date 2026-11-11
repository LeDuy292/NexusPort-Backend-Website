param([switch]$Apply,[ValidateSet('dauboquay','carrier01')][string]$Username='dauboquay')
$ErrorActionPreference='Stop'
$Username=$Username.ToLowerInvariant()
$demoPrefix=if($Username -eq 'carrier01'){'e9111101'}else{'e9111100'}
$demoRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$demoVars=@{}
foreach($demoLine in Get-Content -LiteralPath (Join-Path $demoRepo '.env')) {
 if($demoLine -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$'){$demoVars[$matches[1]]=$matches[2].Trim().Trim('"').Trim("'")}
}
if($demoVars['NODE_ENV'] -ne 'development' -or $demoVars['DB_TARGET'] -ne 'aiven'){throw 'Refusing non-Development database. This uses the same .env target as csharp-core/run.ps1.'}
$demoPrevious=@{}
foreach($demoName in @('PGPASSWORD','PGSSLMODE','PGCONNECT_TIMEOUT','PGCLIENTENCODING')){$demoPrevious[$demoName]=[Environment]::GetEnvironmentVariable($demoName,'Process')}
try {
 $env:PGPASSWORD=$demoVars['AIVEN_DB_PASSWORD'];$env:PGSSLMODE='require';$env:PGCONNECT_TIMEOUT='15';$env:PGCLIENTENCODING='UTF8'
 $demoPsql='C:\Program Files\PostgreSQL\17\bin\psql.exe'
 $demoArgs=@('-h',$demoVars['AIVEN_DB_HOST'],'-p',$demoVars['AIVEN_DB_PORT'],'-U',$demoVars['AIVEN_DB_USER'],'-d',$demoVars['AIVEN_DB_NAME'],'-X','-A','-t','-q','-v','ON_ERROR_STOP=1')
 function Read-DemoJson([string]$Sql){
  $demoOutput=& $demoPsql @demoArgs -c "BEGIN READ ONLY; $Sql ROLLBACK;" 2>&1
  if($LASTEXITCODE -ne 0){throw 'Read-only Development inspection failed; connection details withheld.'}
  return (($demoOutput -join '') | ConvertFrom-Json)
 }
 $demoVersions=Read-DemoJson "SELECT COALESCE(json_agg(version ORDER BY version),'[]'::json)::text FROM nexusport_schema_versions;"
 $demoMissing=@(Get-ChildItem -LiteralPath (Join-Path $demoRepo 'database\migrations') -Filter '*.sql' | Where-Object {
  $demoVersion=($_.BaseName.Split('_')[0..1] -join '_')
  $demoVersions -notcontains $demoVersion -and -not($demoVersion.EndsWith('_005') -and $demoVersions -contains '005')
 } | Select-Object -ExpandProperty Name)
 $demoSummarySql=@"
 SELECT jsonb_build_object('sample','DEMO-EPORT-IMPORT-20261111','targetUsername','$Username',
 'containers',(SELECT COALESCE(jsonb_agg(jsonb_build_object('number',c.container_no,'type',t.code,'visitId',v.id,'seal',v.seal_number,'validUntil',v.valid_to,'visitStatus',v.visit_status) ORDER BY c.container_no),'[]'::jsonb)
 FROM container_visits v JOIN containers c ON c.id=v.container_id JOIN container_types t ON t.id=c.container_type_id WHERE v.id::text LIKE '$demoPrefix-%'),
 'tariffs',(SELECT COALESCE(jsonb_agg(jsonb_build_object('size',t.size,'equipment',t.equipment,'amount',t.amount,'until',t.effective_to)),'[]'::jsonb) FROM eport_tariffs t JOIN eport_services s ON s.id=t.service_id WHERE s.code='IMPORT_PICKUP' AND t.is_active AND current_date BETWEEN t.effective_from AND COALESCE(t.effective_to,'infinity'::date)))::text;
"@
 if(-not $Apply){[pscustomobject]@{Mode='Read-only inspection';MissingMigrations=$demoMissing;CurrentSamples=(Read-DemoJson $demoSummarySql)} | ConvertTo-Json -Depth 6;return}
 if($demoMissing.Count -gt 0 -or $demoVersions -notcontains '20261110_041'){throw 'Required migrations are missing. No sample data was inserted.'}
 $demoTables=@('users','carriers','container_types','containers','container_declarations','container_visits','container_visit_access','export_bookings','export_booking_containers','eport_tariffs','eport_catalog_audit','eport_service_drafts','eport_service_registrations','eport_service_order_states','eport_service_order_events','eport_service_payments')
 $demoHashFields=@()
 foreach($demoTable in $demoTables){
  $demoIdColumn=if($demoTable -eq 'container_visit_access'){'container_visit_id'}elseif($demoTable -eq 'eport_service_order_states'){'draft_id'}else{'id'}
  $demoHashFields+="'$demoTable',(SELECT md5(COALESCE(string_agg(md5(to_jsonb(t)::text),'' ORDER BY md5(to_jsonb(t)::text)),'')) FROM $demoTable t WHERE $demoIdColumn`::text NOT LIKE '$demoPrefix-%')"
 }
 $demoHashSql='SELECT jsonb_build_object('+($demoHashFields -join ',')+')::text;'
 $demoBefore=Read-DemoJson $demoHashSql
 $demoSample=Join-Path $demoRepo 'database\samples\20261111_eport_import_demo.sql'
 $demoSeedOutput=& $demoPsql @demoArgs -c "SET nexusport.allow_service_samples='development'; SET nexusport.import_demo_username='$Username';" -f $demoSample 2>&1
 if($LASTEXITCODE -ne 0){throw 'Demo seed transaction failed; details withheld. No partial sample transaction was committed.'}
 $demoAfter=Read-DemoJson $demoHashSql
 foreach($demoTable in $demoTables){if($demoBefore.$demoTable -ne $demoAfter.$demoTable){throw "Existing $demoTable rows changed during verification, possibly concurrent application activity. Samples were already committed; inspect before retrying."}}
 [pscustomobject]@{Applied=$true;ExistingRowsPreserved=$true;CustomsClearanceGranted=$false;Summary=(Read-DemoJson $demoSummarySql);SampleSha256=(Get-FileHash -LiteralPath $demoSample -Algorithm SHA256).Hash} | ConvertTo-Json -Depth 7
}finally{foreach($demoName in $demoPrevious.Keys){[Environment]::SetEnvironmentVariable($demoName,$demoPrevious[$demoName],'Process')}}
