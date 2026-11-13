param([switch]$Install)
$ErrorActionPreference = 'Stop'
$runtimeDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../outputs/local-rabbitmq'))
$nodeEnvPath = Join-Path $PSScriptRoot '../.env'
$brokerUrl = $env:RABBITMQ_URL
if (-not $brokerUrl -and (Test-Path -LiteralPath $nodeEnvPath)) {
  $line = Get-Content -LiteralPath $nodeEnvPath | Where-Object { $_ -match '^RABBITMQ_URL=' } | Select-Object -First 1
  if ($line) { $brokerUrl = ($line -replace '^RABBITMQ_URL=', '').Trim().Trim('"').Trim("'") }
}
if (-not $brokerUrl) { $brokerUrl = 'amqp://guest:guest@127.0.0.1:5672' }
$broker = [uri]$brokerUrl
if ($broker.Host -notin @('localhost','127.0.0.1','::1')) { Write-Output 'Using configured remote RabbitMQ.'; exit 0 }
$port = if ($broker.Port -gt 0) { $broker.Port } else { 5672 }
function Test-BrokerPort {
  $socket = [Net.Sockets.TcpClient]::new()
  try { $attempt = $socket.ConnectAsync('127.0.0.1',$port); return ($attempt.Wait(500) -and $socket.Connected) }
  catch { return $false } finally { $socket.Dispose() }
}
if (Test-BrokerPort) { Write-Output "RabbitMQ listener already available on port $port."; exit 0 }
$erlangDirectory = Join-Path $runtimeDirectory 'erlang'
$rabbitDirectory = Join-Path $runtimeDirectory 'server/rabbitmq_server-4.3.4'
if (-not (Test-Path -LiteralPath (Join-Path $rabbitDirectory 'sbin/rabbitmq-server.bat')) -or -not (Test-Path -LiteralPath (Join-Path $erlangDirectory 'bin/erl.exe'))) {
  if (-not $Install) { throw 'RabbitMQ is missing. Run npm run dev:rabbitmq -- -Install once, then npm run dev.' }
  New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
  $packages = @(
    @{ Url='https://github.com/erlang/otp/releases/download/OTP-27.3.4.19/otp_win64_27.3.4.19.zip'; Archive='otp.zip'; Destination=$erlangDirectory },
    @{ Url='https://github.com/rabbitmq/rabbitmq-server/releases/download/v4.3.4/rabbitmq-server-windows-4.3.4.zip'; Archive='rabbitmq.zip'; Destination=(Join-Path $runtimeDirectory 'server') }
  )
  foreach ($package in $packages) {
    $archivePath = Join-Path $runtimeDirectory $package.Archive
    & curl.exe -L --fail --retry 3 --connect-timeout 15 --silent --show-error --output $archivePath $package.Url
    if ($LASTEXITCODE -ne 0) { throw 'Official RabbitMQ/Erlang package download failed.' }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $package.Destination -Force
  }
}
$env:ERLANG_HOME = $erlangDirectory
$env:RABBITMQ_BASE = Join-Path $runtimeDirectory 'data'
$env:RABBITMQ_CONFIG_FILE = Join-Path $env:RABBITMQ_BASE 'rabbitmq'
$env:RABBITMQ_NODENAME = 'nexusport@localhost'
$env:RABBITMQ_NODE_IP_ADDRESS = '127.0.0.1'
$env:RABBITMQ_NODE_PORT = [string]$port
New-Item -ItemType Directory -Force -Path $env:RABBITMQ_BASE | Out-Null
$auth = $broker.UserInfo.Split(':',2)
$rabbitUser = if ($auth.Count -ge 1 -and $auth[0]) { [uri]::UnescapeDataString($auth[0]) } else { 'guest' }
$rabbitPassword = if ($auth.Count -eq 2) { [uri]::UnescapeDataString($auth[1]) } else { 'guest' }
if ($rabbitUser -match '[\r\n]' -or $rabbitPassword -match '[\r\n]') { throw 'Invalid RabbitMQ credentials.' }
@("listeners.tcp.1 = 127.0.0.1:$port", "default_user = $rabbitUser", "default_pass = $rabbitPassword") | Set-Content -LiteralPath ($env:RABBITMQ_CONFIG_FILE+'.conf') -Encoding ASCII
$serverScript = Join-Path $rabbitDirectory 'sbin/rabbitmq-server.bat'
Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/c',('""'+$serverScript+'""')) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:RABBITMQ_BASE 'launcher.log') -RedirectStandardError (Join-Path $env:RABBITMQ_BASE 'launcher-error.log') | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds(45)
while ([DateTime]::UtcNow -lt $deadline) {
  if (Test-BrokerPort) { Write-Output "RabbitMQ started on 127.0.0.1:$port. Data: $($env:RABBITMQ_BASE)"; exit 0 }
  Start-Sleep -Milliseconds 500
}
throw "RabbitMQ did not become ready. Check logs in $($env:RABBITMQ_BASE)."
