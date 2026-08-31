# ============================================================
# NexusPort RBAC — Test Script (PowerShell)
# Chay: .\test-rbac.ps1
# ============================================================

$BASE = "http://localhost:3001/api"
$PWD_DEFAULT = "NexusPort@2026"

function Write-Green($t)  { Write-Host $t -ForegroundColor Green }
function Write-Red($t)    { Write-Host $t -ForegroundColor Red }
function Write-Cyan($t)   { Write-Host $t -ForegroundColor Cyan }
function Write-Yellow($t) { Write-Host $t -ForegroundColor Yellow }
function Write-DGray($t)  { Write-Host $t -ForegroundColor DarkGray }

# Helper: gui HTTP request, tra ve status code (khong bao gio throw)
function Invoke-Safe {
    param([string]$Method, [string]$Uri, [hashtable]$Headers = @{}, [string]$Body = "", [string]$CT = "")
    try {
        $params = @{ Method = $Method; Uri = $Uri; UseBasicParsing = $true; ErrorAction = "Stop" }
        if ($Headers.Count)  { $params.Headers = $Headers }
        if ($Body -ne "")    { $params.Body = $Body }
        if ($CT -ne "")      { $params.ContentType = $CT }
        $res = Invoke-WebRequest @params
        return @{ Code = [int]$res.StatusCode; Content = $res.Content }
    }
    catch [System.Net.WebException] {
        $httpResp = $_.Exception.Response
        if ($httpResp) {
            $code = [int]$httpResp.StatusCode
            $reader = New-Object System.IO.StreamReader($httpResp.GetResponseStream())
            $content = $reader.ReadToEnd()
            $reader.Close()
            return @{ Code = $code; Content = $content }
        }
        return @{ Code = 0; Content = "" }
    }
    catch {
        return @{ Code = 0; Content = "" }
    }
}

$USERS = @(
    [pscustomobject]@{ role = "Administrator";     username = "admin";        password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Dispatcher";        username = "dispatcher01"; password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Gate Officer";      username = "gate01";       password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Yard Operator";     username = "yard01";       password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Berth Staff";       username = "berth01";      password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Transport Company"; username = "carrier01";    password = $PWD_DEFAULT }
    [pscustomobject]@{ role = "Driver";            username = "driver01";     password = $PWD_DEFAULT }
)

$ENDPOINTS = @(
    [pscustomobject]@{ path="/admin/users";           label="Admin Users          (Admin only)";                       allowed=@("Administrator") }
    [pscustomobject]@{ path="/admin/dashboard";       label="Admin Dashboard      (Admin + Dispatcher)";               allowed=@("Administrator","Dispatcher") }
    [pscustomobject]@{ path="/dispatcher/operations"; label="Dispatcher Ops       (Admin + Dispatcher)";               allowed=@("Administrator","Dispatcher") }
    [pscustomobject]@{ path="/gate/check-in";         label="Gate Check-in        (Admin + Gate)";                     allowed=@("Administrator","Gate Officer") }
    [pscustomobject]@{ path="/yard/containers";       label="Yard Containers      (Admin + Yard)";                     allowed=@("Administrator","Yard Operator") }
    [pscustomobject]@{ path="/berth/schedule";        label="Berth Schedule       (Admin + Berth)";                    allowed=@("Administrator","Berth Staff") }
    [pscustomobject]@{ path="/transport/bookings";    label="Transport Bookings   (Admin + Carrier + Dispatcher)";     allowed=@("Administrator","Transport Company","Dispatcher") }
    [pscustomobject]@{ path="/driver/trips";          label="Driver Trips         (Admin + Driver)";                   allowed=@("Administrator","Driver") }
    [pscustomobject]@{ path="/profile/me";            label="Profile Me           (All roles)";                        allowed=@("Administrator","Dispatcher","Gate Officer","Yard Operator","Berth Staff","Transport Company","Driver") }
)

# ─── BUOC 1: Login ────────────────────────────────────────────────────────────
Write-Cyan "`n===== BUOC 1: Dang nhap lay token ====="
$tokens = @{}

foreach ($u in $USERS) {
    $body = (@{ username = $u.username; password = $u.password } | ConvertTo-Json -Compress)
    $r = Invoke-Safe -Method Post -Uri "$BASE/auth/login" -Body $body -CT "application/json"
    if ($r.Code -eq 200) {
        $json = ($r.Content | ConvertFrom-Json)
        $tokens[$u.role] = $json.data.token
        Write-Green "  [OK]   $($u.role.PadRight(22)) ($($u.username))"
    } else {
        Write-Red "  [FAIL] $($u.role.PadRight(22)) ($($u.username)) -- HTTP $($r.Code)"
        Write-Red "         $($r.Content)"
    }
}

# ─── BUOC 2: Test tung endpoint ───────────────────────────────────────────────
Write-Cyan "`n===== BUOC 2: Test RBAC ====="
$pass = 0; $fail = 0

foreach ($ep in $ENDPOINTS) {
    Write-Yellow "`n  >> $($ep.label)"

    foreach ($u in $USERS) {
        $token       = $tokens[$u.role]
        $shouldAllow = $ep.allowed -contains $u.role
        $expected    = if ($shouldAllow) { 200 } else { 403 }
        $label       = $u.role.PadRight(22)

        if (-not $token) { Write-DGray "     $label --> [SKIP]"; continue }

        $r    = Invoke-Safe -Method Get -Uri "$BASE$($ep.path)" -Headers @{ Authorization = "Bearer $token" }
        $code = $r.Code

        if ($code -eq $expected) {
            $pass++
            if ($code -eq 200) { Write-Green "     $label --> [PASS] 200 OK" }
            else               { Write-Host  "     $label --> [PASS] 403 Forbidden" -ForegroundColor DarkGreen }
        } else {
            $fail++
            Write-Red "     $label --> [FAIL] got $code, expected $expected"
        }
    }
}

# ─── BUOC 3: Khong co token ───────────────────────────────────────────────────
Write-Cyan "`n===== BUOC 3: Khong co token --> 401 ====="
$r    = Invoke-Safe -Method Get -Uri "$BASE/admin/users"
$code = $r.Code
if ($code -eq 401) { $pass++; Write-Green "  [PASS] 401 Unauthorized (dung)" }
else               { $fail++; Write-Red   "  [FAIL] got $code (expected 401)" }

# ─── KET QUA ──────────────────────────────────────────────────────────────────
$total = $pass + $fail
Write-Cyan "`n=========================================="
if ($fail -eq 0) { Write-Green "  PASS $pass/$total -- RBAC hoat dong chinh xac!" }
else             { Write-Red   "  $pass/$total PASS | $fail/$total FAIL" }
Write-Cyan "=========================================="
