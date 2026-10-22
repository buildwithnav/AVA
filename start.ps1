# ── AVA Local Dev Launcher ────────────────────────────────────────────────────
# Builds Release, runs DLLs directly (avoids CLR debug assertions),
# waits for health checks, then launches Angular dev servers.
# ──────────────────────────────────────────────────────────────────────────────

$Root = $PSScriptRoot
$Jobs = @()
$ExitedPids = @{}

# ── Load .env file into current process environment ──────────────────────────
function Load-EnvFile {
    param([string]$Path)
    if (Test-Path $Path) {
        Get-Content $Path | ForEach-Object {
            $line = $_.Trim()
            if ($line -and -not $line.StartsWith('#')) {
                $parts = $line -split '=', 2
                if ($parts.Length -eq 2) {
                    [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim(), 'Process')
                }
            }
        }
    }
}

# ── Kill any process already on a port ────────────────────────────────────────
function Free-Port {
    param([int]$Port)
    $lines = netstat -ano | Select-String ":$Port\s"
    $owningPids = $lines | ForEach-Object {
        if ($_ -match '\s+(\d+)\s*$') { $matches[1] }
    } | Sort-Object -Unique
    foreach ($p in $owningPids) {
        if ($p -and $p -ne '0') {
            Write-Host "  Port $Port in use by PID $p - killing..." -ForegroundColor Yellow
            Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue
        }
    }
}

# ── Build a dotnet project in Release mode ────────────────────────────────────
function Build-DotnetProject {
    param([string]$Name, [string]$ProjectDir, [ConsoleColor]$Color)
    Write-Host "[$Name] Building Release..." -ForegroundColor $Color
    $csproj = Get-ChildItem -Path $ProjectDir -Filter "*.csproj" | Select-Object -First 1
    $result = & dotnet build $csproj.FullName -c Release --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$Name] BUILD FAILED:" -ForegroundColor Red
        $result | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "Build failed for $Name"
    }
    Write-Host "[$Name] Build succeeded" -ForegroundColor Green
}

# ── Start a dotnet service from Release DLL ───────────────────────────────────
function Start-DotnetService {
    param([string]$Name, [string]$ProjectDir, [int]$Port, [ConsoleColor]$Color)

    $logDir = Join-Path $Root "logs"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stdoutLog = Join-Path $logDir "$Name.stdout.log"
    $stderrLog = Join-Path $logDir "$Name.stderr.log"

    # Find the Release DLL
    $dllName = (Get-ChildItem -Path $ProjectDir -Filter "*.csproj" | Select-Object -First 1).BaseName + ".dll"
    $dllPath = Join-Path $ProjectDir "bin\Release\net9.0\$dllName"

    if (-not (Test-Path $dllPath)) {
        Write-Host "[$Name] DLL not found: $dllPath" -ForegroundColor Red
        throw "DLL not found for $Name"
    }

    Write-Host "[$Name] Starting on port $Port..." -ForegroundColor $Color

    $env:ASPNETCORE_URLS = "http://localhost:$Port"
    $env:ASPNETCORE_ENVIRONMENT = "Development"

    $job = Start-Process -PassThru -NoNewWindow -FilePath "dotnet" `
        -ArgumentList $dllPath `
        -WorkingDirectory $ProjectDir `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError  $stderrLog
    return $job
}

# ── Ensure a Python venv exists with dependencies installed ──────────────────
# ava-kb needs Python 3.10+ with heavy deps (pyarrow/duckdb/etc). A bare system
# Python (often 3.9) won't do, so bootstrap a dedicated .venv the first time.
function Ensure-PythonVenv {
    param([string]$Name, [string]$Dir, [ConsoleColor]$Color)

    $venvPython = Join-Path $Dir ".venv\Scripts\python.exe"
    if (Test-Path $venvPython) { return }  # already bootstrapped

    Write-Host "[$Name] No .venv found - creating one..." -ForegroundColor $Color

    # Find a base interpreter that is Python 3.10+ to create the venv.
    $baseExe  = $null
    $baseArgs = @()
    $candidates = @(
        @{ Exe = "py";      Pre = @("-3") },
        @{ Exe = "python3"; Pre = @() },
        @{ Exe = "python";  Pre = @() }
    )
    foreach ($c in $candidates) {
        try {
            $verArgs = @($c.Pre + @("--version"))
            $v = & $c.Exe @verArgs 2>&1
            if ($LASTEXITCODE -eq 0 -and "$v" -match "Python 3\.(\d+)" -and [int]$matches[1] -ge 10) {
                $baseExe = $c.Exe; $baseArgs = $c.Pre; break
            }
        } catch { }
    }
    if (-not $baseExe) {
        Write-Host "[$Name] ERROR: Python 3.10+ is required to create the venv. Install it or create $Dir\.venv manually." -ForegroundColor Red
        throw "Suitable Python not found for $Name venv"
    }

    $createArgs = @($baseArgs + @("-m", "venv", (Join-Path $Dir ".venv")))
    & $baseExe @createArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $venvPython)) {
        throw "Failed to create venv for $Name"
    }

    $req = Join-Path $Dir "requirements.txt"
    if (Test-Path $req) {
        Write-Host "[$Name] Installing Python dependencies (first run only, may take a minute)..." -ForegroundColor $Color
        & $venvPython -m pip install --upgrade pip --quiet
        & $venvPython -m pip install -r $req --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "pip install failed for $Name (see console output above)"
        }
    }
    Write-Host "[$Name] venv ready." -ForegroundColor Green
}

# ── Start a Python uvicorn service ───────────────────────────────────────────
function Start-PythonService {
    param([string]$Name, [string]$Dir, [int]$Port, [ConsoleColor]$Color)
    Write-Host "[$Name] Starting on port $Port..." -ForegroundColor $Color
    $logFile = Join-Path $Root "logs\$Name.log"
    New-Item -ItemType Directory -Path (Split-Path $logFile) -Force | Out-Null

    # Bootstrap the venv (create + install deps) on first run.
    Ensure-PythonVenv $Name $Dir $Color

    # Use the venv Python if available, otherwise try to find Python
    $venvPython = Join-Path $Dir ".venv\Scripts\python.exe"
    if (Test-Path $venvPython) {
        $pythonExe = $venvPython
        $pythonArgs = "-m","uvicorn","app.main:app","--host","0.0.0.0","--port",$Port
    } else {
        # Try to find Python in various locations
        $pythonCandidates = @("py", "python3", "python")
        $pythonExe = $null
        
        foreach ($candidate in $pythonCandidates) {
            try {
                $testResult = & $candidate --version 2>&1
                if ($LASTEXITCODE -eq 0 -and $testResult -match "Python \d+\.\d+") {
                    $pythonExe = $candidate
                    break
                }
            } catch { }
        }
        
        if (-not $pythonExe) {
            Write-Host "[$Name] ERROR: Python not found. Install Python 3.x or create a venv at $Dir\.venv" -ForegroundColor Red
            throw "Python not found"
        }
        
        $pythonArgs = @("-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", $Port)
        if ($pythonExe -eq "py") {
            $pythonArgs = @("-3") + $pythonArgs
        }
    }

    $job = Start-Process -PassThru -NoNewWindow -FilePath $pythonExe `
        -ArgumentList $pythonArgs `
        -WorkingDirectory $Dir `
        -RedirectStandardOutput $logFile
    return $job
}

# ── Start a Node.js service ────────────────────────────────────────────────────
function Start-NodeService {
    param(
        [string]$Name,
        [string]$Dir,
        [int]$Port,
        [ConsoleColor]$Color,
        [bool]$AlwaysBuild = $false
    )

    $logDir = Join-Path $Root "logs"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stdoutLog = Join-Path $logDir "$Name.stdout.log"
    $stderrLog = Join-Path $logDir "$Name.stderr.log"

    # Install dependencies if node_modules missing
    if (-not (Test-Path (Join-Path $Dir "node_modules"))) {
        Write-Host "[$Name] Installing npm dependencies..." -ForegroundColor $Color
        Push-Location $Dir
        & npm ci --ignore-scripts 2>&1 | Out-Null
        Pop-Location
    }

    # Build if dist missing (or always build when explicitly requested)
    if ($AlwaysBuild -or -not (Test-Path (Join-Path $Dir "dist"))) {
        Write-Host "[$Name] Building TypeScript..." -ForegroundColor $Color
        Push-Location $Dir
        & npx.cmd tsc 2>&1 | Out-Null
        Pop-Location
    }

    Write-Host "[$Name] Starting on port $Port..." -ForegroundColor $Color

    $job = Start-Process -PassThru -NoNewWindow -FilePath "node" `
        -ArgumentList "dist/index.js","--http","--port",$Port,"--host","0.0.0.0" `
        -WorkingDirectory $Dir `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError  $stderrLog
    return $job
}

# ── Start an ng serve ─────────────────────────────────────────────────────────
function Start-NgService {
    param([string]$Name, [string]$Dir, [int]$Port, [ConsoleColor]$Color)
    Write-Host "[$Name] Starting on port $Port..." -ForegroundColor $Color
    $job = Start-Process -PassThru -NoNewWindow -FilePath "npx.cmd" `
        -ArgumentList "ng","serve","--port",$Port `
        -WorkingDirectory $Dir
    return $job
}

# ── Poll /health until 200 (with timeout) ────────────────────────────────────
function Wait-Healthy {
    param(
        [string]$Name,
        [string]$Url,
        [ConsoleColor]$Color,
        [int]$TimeoutSeconds = 60
    )
    Write-Host "[$Name] Waiting for $Url ..." -ForegroundColor $Color
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                Write-Host "[$Name] Healthy ($([int]$sw.Elapsed.TotalSeconds)s)" -ForegroundColor Green
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
    Write-Host "[$Name] TIMEOUT after ${TimeoutSeconds}s - check logs\$Name.stderr.log" -ForegroundColor Red
}

# ══════════════════════════════════════════════════════════════════════════════
# ── Main ─────────────────────────────────────────────────────────────────────
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "`n════════════════════════════════════════" -ForegroundColor White
Write-Host "  AVA - Starting all services" -ForegroundColor White
Write-Host "════════════════════════════════════════`n" -ForegroundColor White

# ── Load environment files ────────────────────────────────────────────────────
Write-Host "Loading .env files..." -ForegroundColor Yellow
Load-EnvFile "$Root\ava-kb\.env"
Load-EnvFile "$Root\ava-api\.env"
Write-Host ".env files loaded.`n" -ForegroundColor Green

# ── Free all ports first ─────────────────────────────────────────────────────
Write-Host "Freeing ports..." -ForegroundColor Yellow
Free-Port 8100
Free-Port 5010
Free-Port 5140
Free-Port 6200
Free-Port 6201
Free-Port 9090
Write-Host "Ports clear.`n" -ForegroundColor Green

# ── Build all .NET projects ───────────────────────────────────────────────────
Write-Host "Building .NET projects...`n" -ForegroundColor Yellow
Build-DotnetProject "ava-api"   "$Root\ava-api\Ava.Api"            Blue
Build-DotnetProject "ava-metaquery" "$Root\ava-metaquery\Ava.MetaQuery.Api" Cyan
Write-Host ""

# ── Start services ────────────────────────────────────────────────────────────

# 1. ava-kb (local Python service)
$Jobs += Start-PythonService "ava-kb" "$Root\ava-kb" 8100 Magenta
Wait-Healthy                 "ava-kb" "http://localhost:8100/health" Magenta

# 2. ava-api (depends on ava-kb)
$Jobs += Start-DotnetService "ava-api"   "$Root\ava-api\Ava.Api"            5010 Blue
Wait-Healthy                 "ava-api"   "http://localhost:5010/health"      Blue

# 3. ava-metaquery (Oracle/CDS.Codex SQL execution API — optional/non-blocking).
#    Boots in degraded mode (health only) if Oracle/Codex isn't configured locally.
$Jobs += Start-DotnetService "ava-metaquery" "$Root\ava-metaquery\Ava.MetaQuery.Api" 5140 Cyan
Wait-Healthy                 "ava-metaquery" "http://localhost:5140/health"  Cyan 30

# 5. Angular apps
$Jobs += Start-NgService "ava-web"          "$Root\ava-web"              6200 Yellow
$Jobs += Start-NgService "orchestration-ui" "$Root\ava-orchestration-ui" 6201 DarkCyan

# 6. AVA loader (build element bundle, then serve for embedded integration)
Write-Host "`n[ava-loader] Building element bundle..." -ForegroundColor DarkYellow
$buildResult = & npm run build:element --prefix "$Root\ava-web" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ava-loader] BUILD FAILED:" -ForegroundColor Red
    $buildResult | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "Element build failed"
}
Write-Host "[ava-loader] Build succeeded" -ForegroundColor Green

Write-Host "[ava-loader] Starting on port 9090..." -ForegroundColor DarkYellow
$env:NODE_OPTIONS = "--no-deprecation"
$Jobs += Start-Process -PassThru -NoNewWindow -FilePath "npx.cmd" `
    -ArgumentList "--yes","http-server","dist\element\browser","--port","9090","--cors","-s" `
    -WorkingDirectory "$Root\ava-web"
Remove-Item Env:\NODE_OPTIONS -ErrorAction SilentlyContinue

Write-Host "`n════════════════════════════════════════" -ForegroundColor Green
Write-Host "  All services running:" -ForegroundColor Green
Write-Host "════════════════════════════════════════" -ForegroundColor Green
Write-Host "  ava-kb             http://localhost:8100" -ForegroundColor Magenta
Write-Host "  ava-api            http://localhost:5010" -ForegroundColor Blue
Write-Host "  ava-metaquery      http://localhost:5140  (Swagger: /swagger)" -ForegroundColor Cyan
Write-Host "  ava-web            http://localhost:6200" -ForegroundColor Yellow
Write-Host "  orchestration-ui   http://localhost:6201" -ForegroundColor DarkCyan
Write-Host "  ava-loader         http://localhost:9090" -ForegroundColor DarkYellow
Write-Host ""
Write-Host "  Logs in: $Root\logs\" -ForegroundColor DarkGray
Write-Host "`nPress Ctrl+C to stop all services.`n" -ForegroundColor White

# ── Cleanup on Ctrl+C ─────────────────────────────────────────────────────────
try {
    while ($true) {
        Start-Sleep -Seconds 5
        foreach ($job in $Jobs) {
            if ($job.HasExited -and -not $ExitedPids.ContainsKey($job.Id)) {
                $ExitedPids[$job.Id] = $true
                Write-Host "[PID $($job.Id)] exited with code $($job.ExitCode)" -ForegroundColor Yellow
            }
        }
    }
} finally {
    Write-Host "`nShutting down all services..." -ForegroundColor Yellow
    foreach ($job in $Jobs) {
        if (-not $job.HasExited) {
            Stop-Process -Id $job.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "All services stopped." -ForegroundColor Green
}
