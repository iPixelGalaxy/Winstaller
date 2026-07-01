<#
.SYNOPSIS
    Winstaller Release Script - Builds, versions, and publishes Winstaller

.DESCRIPTION
    This script:
    1. Auto-bumps the patch version number
    2. Builds and publishes as a single-file executable
    3. Creates a ZIP archive
    4. Updates version.txt
    5. Uploads to copyparty server

.PARAMETER BumpMajor
    Bump major version instead of patch (1.0.0 -> 2.0.0)

.PARAMETER BumpMinor
    Bump minor version instead of patch (1.0.0 -> 1.1.0)

.PARAMETER NoBump
    Skip version bump (rebuild current version)

.PARAMETER NoUpload
    Skip upload to server (just build locally)

.PARAMETER OpenBrowser
    Open browser for manual upload instead of auto-uploading

.EXAMPLE
    .\release.ps1
    # Bumps patch, builds, and uploads

.EXAMPLE
    .\release.ps1 -BumpMinor
    # Bumps minor version and uploads

.EXAMPLE
    .\release.ps1 -OpenBrowser
    # Builds and opens browser for manual upload
#>

param(
    [switch]$BumpMajor,
    [switch]$BumpMinor,
    [switch]$NoBump,
    [switch]$NoUpload,
    [switch]$OpenBrowser
)

$ErrorActionPreference = "Stop"

# Configuration
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$ProjectDir = Split-Path -Parent $ScriptDir
if (-not $ProjectDir -or -not (Test-Path (Join-Path $ProjectDir "Winstaller.csproj"))) {
    $ProjectDir = "D:\ReinstallFiles\Winstaller"
}
$CsprojPath = Join-Path $ProjectDir "Winstaller.csproj"
$PublishDir = Join-Path $ProjectDir "publish"
$ServerBaseUrl = "https://copyparty.arimodu.dev/winstaller"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Winstaller Release Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Read current version
Write-Host "[1/6] Reading current version..." -ForegroundColor Yellow

if (-not (Test-Path $CsprojPath)) {
    Write-Error "Project file not found: $CsprojPath"
    exit 1
}

$csprojContent = Get-Content $CsprojPath -Raw
if ($csprojContent -match '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]
    $currentVersion = "$major.$minor.$patch"
    Write-Host "      Current version: $currentVersion" -ForegroundColor Gray
} else {
    Write-Error "Could not find version in csproj"
    exit 1
}

# Step 2: Bump version
Write-Host "[2/6] Bumping version..." -ForegroundColor Yellow

if ($NoBump) {
    $newVersion = $currentVersion
    Write-Host "      Skipping bump, using: $newVersion" -ForegroundColor Gray
} elseif ($BumpMajor) {
    $major++
    $minor = 0
    $patch = 0
    $newVersion = "$major.$minor.$patch"
    Write-Host "      Major bump: $currentVersion -> $newVersion" -ForegroundColor Green
} elseif ($BumpMinor) {
    $minor++
    $patch = 0
    $newVersion = "$major.$minor.$patch"
    Write-Host "      Minor bump: $currentVersion -> $newVersion" -ForegroundColor Green
} else {
    $patch++
    $newVersion = "$major.$minor.$patch"
    Write-Host "      Patch bump: $currentVersion -> $newVersion" -ForegroundColor Green
}

# Update csproj with new version
if (-not $NoBump) {
    $newCsprojContent = $csprojContent -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newVersion</Version>"
    Set-Content -Path $CsprojPath -Value $newCsprojContent -NoNewline
    Write-Host "      Updated $CsprojPath" -ForegroundColor Gray
}

# Step 3: Build and publish
Write-Host "[3/6] Building and publishing..." -ForegroundColor Yellow

# Clean publish directory
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# Run dotnet publish
$publishArgs = @(
    "publish"
    $CsprojPath
    "-c", "Release"
    "-r", "win-x64"
    "--self-contained", "true"
    "-p:PublishSingleFile=true"
    "-p:EnableCompressionInSingleFile=true"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:PublishReadyToRun=true"
    "-o", $PublishDir
)

Write-Host "      Running: dotnet $($publishArgs -join ' ')" -ForegroundColor Gray
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "      Published to: $PublishDir" -ForegroundColor Gray

# Verify the executable exists
$exePath = Join-Path $PublishDir "Winstaller.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Winstaller.exe not found in publish output"
    exit 1
}

$exeSize = (Get-Item $exePath).Length / 1MB
Write-Host "      Winstaller.exe size: $([math]::Round($exeSize, 2)) MB" -ForegroundColor Gray

# Step 4: Create ZIP
Write-Host "[4/6] Creating ZIP archive..." -ForegroundColor Yellow

$zipFileName = "$newVersion.zip"
$zipPath = Join-Path $ProjectDir $zipFileName

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path "$PublishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "      Created: $zipPath ($([math]::Round($zipSize, 2)) MB)" -ForegroundColor Gray

# Step 5: Create version.txt
Write-Host "[5/6] Creating version.txt..." -ForegroundColor Yellow

$versionTxtPath = Join-Path $ProjectDir "version.txt"
Set-Content -Path $versionTxtPath -Value $newVersion -NoNewline
Write-Host "      Created: $versionTxtPath" -ForegroundColor Gray

# Step 6: Upload to server
Write-Host "[6/6] Uploading to server..." -ForegroundColor Yellow

$uploadSuccess = $false

if ($NoUpload) {
    Write-Host "      Skipping upload (--NoUpload specified)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Files ready for manual upload:" -ForegroundColor Cyan
    Write-Host "  - $zipPath" -ForegroundColor White
    Write-Host "  - $versionTxtPath" -ForegroundColor White
    Write-Host "  Upload to: $ServerBaseUrl/" -ForegroundColor White
} elseif ($OpenBrowser) {
    Write-Host "      Opening browser for manual upload..." -ForegroundColor Gray
    Write-Host ""
    Write-Host "Please upload these files:" -ForegroundColor Cyan
    Write-Host "  1. $zipPath" -ForegroundColor White
    Write-Host "  2. $versionTxtPath" -ForegroundColor White
    Write-Host ""

    # Open the folder containing the files
    Start-Process "explorer.exe" -ArgumentList "/select,`"$zipPath`""

    # Open the browser to the upload location
    Start-Process "$ServerBaseUrl/"

    Write-Host "Browser opened. Please drag and drop the files to upload." -ForegroundColor Yellow
} else {
    # Upload directly (no auth required)
    try {
        # Delete old version.txt from server first (keep old ZIPs)
        # Copyparty uses POST to filepath?delete
        Write-Host "      Deleting old version.txt from server..." -ForegroundColor Gray
        $versionUrl = "$ServerBaseUrl/version.txt"
        $deleteUrl = "$versionUrl`?delete"
        try {
            $response = Invoke-RestMethod -Uri $deleteUrl -Method Post -ErrorAction SilentlyContinue
            if ($response -match "deleted 1 files") {
                Write-Host "      Deleted old version.txt" -ForegroundColor Gray
            }
        } catch {
            Write-Host "      No existing version.txt to delete (or delete failed)" -ForegroundColor Gray
        }

        # Upload ZIP
        Write-Host "      Uploading $zipFileName..." -ForegroundColor Gray
        $zipUrl = "$ServerBaseUrl/$zipFileName"
        Invoke-RestMethod -Uri $zipUrl -Method Put -InFile $zipPath -ContentType "application/zip"
        Write-Host "      Uploaded: $zipUrl" -ForegroundColor Green

        # Upload version.txt
        Write-Host "      Uploading version.txt..." -ForegroundColor Gray
        Invoke-RestMethod -Uri $versionUrl -Method Put -InFile $versionTxtPath -ContentType "text/plain"
        Write-Host "      Uploaded: $versionUrl" -ForegroundColor Green

        $uploadSuccess = $true
    } catch {
        Write-Host "      Upload failed: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "Files are ready for manual upload:" -ForegroundColor Cyan
        Write-Host "  - $zipPath" -ForegroundColor White
        Write-Host "  - $versionTxtPath" -ForegroundColor White
        Write-Host "  Upload to: $ServerBaseUrl/" -ForegroundColor White
    }

    # Clean up local files after successful upload
    if ($uploadSuccess) {
        Write-Host "      Cleaning up local release files..." -ForegroundColor Gray
        try {
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Remove-Item $versionTxtPath -Force -ErrorAction SilentlyContinue
            Write-Host "      Deleted: $zipFileName, version.txt" -ForegroundColor Gray
        } catch {
            Write-Host "      Warning: Could not delete local files: $_" -ForegroundColor Yellow
        }
    }
}

# Done
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Release $newVersion Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Summary:" -ForegroundColor White
Write-Host "  Version:    $currentVersion -> $newVersion" -ForegroundColor Gray
Write-Host "  Executable: $exePath" -ForegroundColor Gray
if (-not $NoUpload -and -not $OpenBrowser -and $uploadSuccess) {
    Write-Host "  Server:     $ServerBaseUrl/$zipFileName" -ForegroundColor Gray
    Write-Host "  Local files cleaned up after upload" -ForegroundColor Gray
} else {
    Write-Host "  ZIP:        $zipPath" -ForegroundColor Gray
    Write-Host "  Server:     $ServerBaseUrl/$zipFileName" -ForegroundColor Gray
}
Write-Host ""
