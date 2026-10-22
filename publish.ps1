<#
.SYNOPSIS
    Builds and publishes all AVA projects for deployment.

.DESCRIPTION
    This script builds all .NET APIs, Angular apps, and packages the Python app
    into a 'publish' folder ready for deployment.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default is Release.

.PARAMETER SkipDotNet
    Skip building .NET projects.

.PARAMETER SkipAngular
    Skip building Angular projects.

.PARAMETER SkipPython
    Skip packaging Python project.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Configuration Debug
    .\publish.ps1 -SkipAngular
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipDotNet,
    [switch]$SkipAngular,
    [switch]$SkipPython
)

$ErrorActionPreference = "Stop"
$ScriptRoot = $PSScriptRoot
$PublishRoot = Join-Path $ScriptRoot "publish"

# Colors for output
function Write-Step { param($msg) Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Success { param($msg) Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Failure { param($msg) Write-Host "   [FAILED] $msg" -ForegroundColor Red }

# Clean and create publish folder
Write-Step "Preparing publish folder..."
if (Test-Path $PublishRoot) {
    Remove-Item -Path $PublishRoot -Recurse -Force
}
New-Item -Path $PublishRoot -ItemType Directory | Out-Null
Write-Success "Created $PublishRoot"

# .NET Projects
$dotnetProjects = @(
    @{ Name = "ava-api"; Path = "ava-api\Ava.Api\Ava.Api.csproj"; Note = "(includes Ava.Vectors)" },
    @{ Name = "ava-metaquery"; Path = "ava-metaquery\Ava.MetaQuery.Api\Ava.MetaQuery.Api.csproj"; Note = "(MetaQuery SQL execution API)" }
)

if (-not $SkipDotNet) {
    Write-Step "Building .NET projects ($Configuration)..."
    foreach ($proj in $dotnetProjects) {
        $projPath = Join-Path $ScriptRoot $proj.Path
        $outputPath = Join-Path $PublishRoot $proj.Name
        
        if (-not (Test-Path $projPath)) {
            Write-Host "   SKIPPED $($proj.Name) - $($proj.Path) not found" -ForegroundColor Yellow
            continue
        }
        
        $label = if ($proj.Note) { "$($proj.Name) $($proj.Note)" } else { $proj.Name }
        Write-Host "   Building $label..." -NoNewline
        
        try {
            # Framework-dependent deployment (requires .NET runtime on server, minimal files)
            dotnet publish $projPath -c $Configuration -o $outputPath --nologo -v q
            Write-Success $proj.Name
        }
        catch {
            Write-Failure $proj.Name
            throw "Failed to build $($proj.Name): $_"
        }
    }
}

# Angular Projects
$angularProjects = @(
    @{ Name = "ava-web"; Path = "ava-web" },
    @{ Name = "ava-orchestration-ui"; Path = "ava-orchestration-ui" }
)

if (-not $SkipAngular) {
    Write-Step "Building Angular projects..."

    # Ensure shared Lib can resolve node_modules from ava-orchestration-ui
    $libNodeModules = Join-Path $ScriptRoot "Lib\node_modules"
    $orchNodeModules = Join-Path $ScriptRoot "ava-orchestration-ui\node_modules"
    if (-not (Test-Path $libNodeModules) -and (Test-Path $orchNodeModules)) {
        New-Item -ItemType Junction -Path $libNodeModules -Target $orchNodeModules | Out-Null
        Write-Success "Created Lib\node_modules junction"
    }

    foreach ($proj in $angularProjects) {
        $projPath = Join-Path $ScriptRoot $proj.Path
        $outputPath = Join-Path $PublishRoot $proj.Name
        
        Write-Host "   Building $($proj.Name)..." -NoNewline
        
        Push-Location $projPath
        try {
            # Install dependencies if node_modules doesn't exist
            if (-not (Test-Path "node_modules")) {
                npm ci --silent 2>$null
            }
            
            # Build the Angular app
            npm run build -- --configuration production 2>$null
            
            # Copy browser-ready files to publish
            $browserPath = Join-Path (Join-Path (Join-Path $projPath "dist") $proj.Name) "browser"
            if (Test-Path $browserPath) {
                Copy-Item -Path $browserPath -Destination $outputPath -Recurse
                Write-Success $proj.Name
            }
            else {
                # Fallback: copy entire dist folder
                $distPath = Join-Path $projPath "dist"
                if (Test-Path $distPath) {
                    Copy-Item -Path $distPath -Destination $outputPath -Recurse
                    Write-Success "$($proj.Name) (fallback - no browser folder)"
                }
                else {
                    Write-Failure "$($proj.Name) - dist folder not found"
                }
            }
        }
        catch {
            Write-Failure $proj.Name
            throw "Failed to build $($proj.Name): $_"
        }
        finally {
            Pop-Location
        }
    }
}

# Python Project (ava-kb)
if (-not $SkipPython) {
    Write-Step "Packaging Python project (ava-kb)..."
    $kbSource = Join-Path $ScriptRoot "ava-kb"
    $kbOutput = Join-Path $PublishRoot "ava-kb"
    
    Write-Host "   Copying ava-kb..." -NoNewline
    
    try {
        New-Item -Path $kbOutput -ItemType Directory | Out-Null
        
        # Copy app folder and requirements
        Copy-Item -Path (Join-Path $kbSource "app") -Destination $kbOutput -Recurse
        Copy-Item -Path (Join-Path $kbSource "requirements.txt") -Destination $kbOutput
        
        # Copy .env if exists (for reference, not for production)
        $envFile = Join-Path $kbSource ".env.example"
        if (Test-Path $envFile) {
            Copy-Item -Path $envFile -Destination $kbOutput
        }
        
        Write-Success "ava-kb"
    }
    catch {
        Write-Failure "ava-kb"
        throw "Failed to package ava-kb: $_"
    }
}

# Summary
Write-Step "Publish completed!"
Write-Host "`nPublished artifacts in: $PublishRoot" -ForegroundColor Yellow
Write-Host ""
Get-ChildItem $PublishRoot -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $sizeStr = if ($size -gt 1MB) { "{0:N2} MB" -f ($size / 1MB) } else { "{0:N2} KB" -f ($size / 1KB) }
    Write-Host "   - $($_.Name): $sizeStr"
}
Write-Host ""
