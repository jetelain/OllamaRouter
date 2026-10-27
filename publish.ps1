<#
.SYNOPSIS
    Builds, installs, and configures OllamaRouter locally in user AppData.
.DESCRIPTION
    Publishes OllamaRouter, installs it to the user-local application directory
    (%LOCALAPPDATA%\OllamaRouter or ~/.ollamarouter), configures state, config,
    and logs directories, and sets up launchers and user PATH.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER InstallDir
    Target installation folder (default: %LOCALAPPDATA%\OllamaRouter or ~/.ollamarouter).
.PARAMETER ConfigDir
    Persistent directory for configuration (default: <InstallDir>\config).
.PARAMETER StateDir
    Persistent directory for runtime state (targets.json, activity-statistics.json) (default: <InstallDir>\state).
.PARAMETER LogsDir
    Directory for application logs (default: <InstallDir>\logs).
.PARAMETER BinDir
    Directory for the command launcher and PATH entry (default: <InstallDir>\bin).
.PARAMETER Uninstall
    Uninstalls OllamaRouter from the user directory and cleans user PATH.
.PARAMETER KeepData
    When used with -Uninstall, preserves configuration (config/), state (state/), and logs (logs/) directories.
.EXAMPLE
    .\publish.ps1
.EXAMPLE
    .\publish.ps1 -Configuration Debug
.EXAMPLE
    .\publish.ps1 -Uninstall
.EXAMPLE
    .\publish.ps1 -Uninstall -KeepData
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InstallDir = $(
        $localApp = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($localApp)) {
            Join-Path $HOME ".ollamarouter"
        } else {
            Join-Path $localApp "OllamaRouter"
        }
    ),
    [string]$ConfigDir = "",
    [string]$StateDir = "",
    [string]$LogsDir = "",
    [string]$BinDir = "",
    [switch]$Uninstall,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

if ([string]::IsNullOrWhiteSpace($ConfigDir)) {
    $ConfigDir = Join-Path $InstallDir "config"
} else {
    $ConfigDir = [System.IO.Path]::GetFullPath($ConfigDir)
}

if ([string]::IsNullOrWhiteSpace($StateDir)) {
    $StateDir = Join-Path $InstallDir "state"
} else {
    $StateDir = [System.IO.Path]::GetFullPath($StateDir)
}

if ([string]::IsNullOrWhiteSpace($LogsDir)) {
    $LogsDir = Join-Path $InstallDir "logs"
} else {
    $LogsDir = [System.IO.Path]::GetFullPath($LogsDir)
}

if ([string]::IsNullOrWhiteSpace($BinDir)) {
    $BinDir = Join-Path $InstallDir "bin"
} else {
    $BinDir = [System.IO.Path]::GetFullPath($BinDir)
}

# 1. Uninstall path
if ($Uninstall) {
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " Uninstalling OllamaRouter                                " -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan

    # Stop any running instances of OllamaRouter
    $runningProcesses = Get-Process -Name "OllamaRouter" -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        Write-Host "Stopping running OllamaRouter processes..." -ForegroundColor Yellow
        $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path $InstallDir) {
        try {
            if ($KeepData) {
                Write-Host "Preserving user configuration in: $ConfigDir" -ForegroundColor Cyan
                Write-Host "Preserving user state in:         $StateDir" -ForegroundColor Cyan
                Write-Host "Preserving user logs in:          $LogsDir" -ForegroundColor Cyan

                # Remove everything except ConfigDir, StateDir, and LogsDir
                Get-ChildItem -Path $InstallDir -Force | ForEach-Object {
                    $itemPath = $_.FullName
                    $isConfig = [string]::Equals($itemPath.TrimEnd('\/'), $ConfigDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
                    $isState  = [string]::Equals($itemPath.TrimEnd('\/'), $StateDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
                    $isLogs   = [string]::Equals($itemPath.TrimEnd('\/'), $LogsDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
                    if (-not $isConfig -and -not $isState -and -not $isLogs) {
                        Remove-Item -Path $itemPath -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Host "Cleaned OllamaRouter binaries while keeping config, state & logs." -ForegroundColor Green
            } else {
                $deleted = $false
                for ($attempt = 1; $attempt -le 6; $attempt++) {
                    try {
                        Remove-Item -Path $InstallDir -Recurse -Force
                        $deleted = $true
                        break
                    }
                    catch {
                        Start-Sleep -Milliseconds 250
                    }
                }
                if (-not $deleted) {
                    Remove-Item -Path $InstallDir -Recurse -Force
                }
                Write-Host "Removed installation directory: $InstallDir" -ForegroundColor Green
            }
        }
        catch {
            [Console]::Error.WriteLine("Error: Failed to remove directory '$InstallDir': $($_.Exception.Message)")
            exit 1
        }
    }

    # Clean user PATH
    try {
        $userPath = [System.Environment]::GetEnvironmentVariable("Path", [System.EnvironmentVariableTarget]::User)
        if ($userPath) {
            $parts = $userPath -split ';' | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not [string]::Equals($_.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
            }
            $newPath = $parts -join ';'
            [System.Environment]::SetEnvironmentVariable("Path", $newPath, [System.EnvironmentVariableTarget]::User)
        }
        $currentParts = $env:Path -split ';' | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not [string]::Equals($_.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)
        }
        $env:Path = $currentParts -join ';'
        Write-Host "OllamaRouter launcher removed from user PATH." -ForegroundColor Green
    }
    catch {
        [Console]::Error.WriteLine("Error: Failed to update user PATH during uninstallation: $($_.Exception.Message)")
        exit 1
    }

    Write-Host "`nOllamaRouter uninstalled successfully." -ForegroundColor Green
    exit 0
}

# 2. Project check & version reading
$repoRoot = $PSScriptRoot
$webProjectPath = Join-Path $repoRoot "OllamaRouter\OllamaRouter.csproj"
if (-not (Test-Path $webProjectPath)) {
    $webProjectPath = Join-Path $repoRoot "src\OllamaRouter\OllamaRouter.csproj"
}

if (-not (Test-Path $webProjectPath)) {
    throw "OllamaRouter project not found in $repoRoot."
}

[xml]$projXml = Get-Content $webProjectPath
$versionNode = $projXml.SelectSingleNode("//Version")
$version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    $versionNode.InnerText.Trim()
} else {
    "1.0.0"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Publishing & Installing OllamaRouter                     " -ForegroundColor Cyan
Write-Host " Version         : $version"
Write-Host " Configuration   : $Configuration"
Write-Host " Runtime         : $Runtime"
Write-Host " Install Dir     : $InstallDir"
Write-Host " Config Folder   : $ConfigDir"
Write-Host " State Folder    : $StateDir"
Write-Host " Logs Folder     : $LogsDir"
Write-Host " Launcher Bin Dir: $BinDir"
Write-Host "==========================================================" -ForegroundColor Cyan

# 3. Stop running process to avoid publish file locks
# $runningProcesses = Get-Process -Name "OllamaRouter" -ErrorAction SilentlyContinue
# if ($runningProcesses) {
#     Write-Warning "OllamaRouter is currently running. Stopping process to allow file updates..."
#     $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
#     $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
#     Start-Sleep -Milliseconds 250
# }

# 4. Ensure directories exist
foreach ($dir in @($InstallDir, $ConfigDir, $StateDir, $LogsDir, $BinDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# 5. Publish OllamaRouter
Write-Host "`nPublishing OllamaRouter..." -ForegroundColor Yellow

$publishArgs = @(
    "publish", $webProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "-p:DebugType=none",
    "-p:Version=$version",
    "-o", $InstallDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# 6. Normalize the binary name and record installed version
if ($Runtime -match 'win') {
    $publishedBinary = Join-Path $InstallDir "OllamaRouter.exe"
    if (-not (Test-Path $publishedBinary)) {
        $candidate = Join-Path $InstallDir "OllamaRouter"
        if (Test-Path $candidate) {
            Rename-Item -Path $candidate -NewName "OllamaRouter.exe"
        } else {
            throw "Published binary not found in $InstallDir."
        }
    }
} else {
    $publishedBinary = Join-Path $InstallDir "OllamaRouter"
    if (-not (Test-Path $publishedBinary)) {
        throw "Published binary not found in $InstallDir."
    }
}
Set-Content -Path (Join-Path $InstallDir "VERSION") -Value "$version`n"

# 7. Configure runtime directories in appsettings.Production.json
Write-Host "`nConfiguring runtime directories in appsettings.Production.json..." -ForegroundColor Yellow

$stateDirPath = $StateDir.Replace('\', '/')
$configDirPath = $ConfigDir.Replace('\', '/')
$logsDirPath = $LogsDir.Replace('\', '/')

$prodConfigPath = Join-Path $InstallDir "appsettings.Production.json"
$prodSettings = if (Test-Path $prodConfigPath) {
    try {
        $raw = Get-Content $prodConfigPath -Raw
        $clean = $raw -replace '(?m)^\s*//.*$', ''
        $clean | ConvertFrom-Json
    } catch {
        [PSCustomObject]@{}
    }
} else {
    [PSCustomObject]@{}
}

# Ensure OllamaRouter section with StateDirectory and ConfigDirectory
if (-not $prodSettings.PSObject.Properties['OllamaRouter']) {
    $prodSettings | Add-Member -MemberType NoteProperty -Name 'OllamaRouter' -Value ([PSCustomObject]@{}) -Force
}
$prodSettings.OllamaRouter | Add-Member -MemberType NoteProperty -Name 'StateDirectory' -Value $stateDirPath -Force
$prodSettings.OllamaRouter | Add-Member -MemberType NoteProperty -Name 'ConfigDirectory' -Value $configDirPath -Force

# Ensure Logging section
if (-not $prodSettings.PSObject.Properties['Logging']) {
    $prodSettings | Add-Member -MemberType NoteProperty -Name 'Logging' -Value ([PSCustomObject]@{}) -Force
}
if (-not $prodSettings.Logging.PSObject.Properties['LogLevel']) {
    $prodSettings.Logging | Add-Member -MemberType NoteProperty -Name 'LogLevel' -Value ([PSCustomObject]@{
        "Default" = "Warning"
        "OllamaRouter" = "Information"
    }) -Force
}
$prodSettings.Logging | Add-Member -MemberType NoteProperty -Name 'LogsDirectory' -Value $logsDirPath -Force

$prodSettings | ConvertTo-Json -Depth 10 | Set-Content -Path $prodConfigPath -Encoding UTF8

Write-Host "Configured directories:" -ForegroundColor Green
Write-Host "  - StateDirectory    : $stateDirPath" -ForegroundColor Gray
Write-Host "  - ConfigDirectory   : $configDirPath" -ForegroundColor Gray
Write-Host "  - LogsDirectory     : $logsDirPath" -ForegroundColor Gray

# 8. Launcher Commands & PATH configuration
Write-Host "`nSetting up launcher commands..." -ForegroundColor Yellow

# In InstallDir:
$installCmd = Join-Path $InstallDir "ollamarouter.cmd"
$installCmdContent = @"
@echo off
setlocal
cd /d "%~dp0"
"%~dp0OllamaRouter.exe" %*
"@
Set-Content -Path $installCmd -Value $installCmdContent -Encoding ASCII

# Backwards compatibility alias in InstallDir
$installWebCmd = Join-Path $InstallDir "ollamarouter-web.cmd"
Set-Content -Path $installWebCmd -Value $installCmdContent -Encoding ASCII

# In BinDir:
$binCmd = Join-Path $BinDir "ollamarouter.cmd"
$binCmdContent = @"
@echo off
setlocal
cd /d "%~dp0.."
"%~dp0..\OllamaRouter.exe" %*
"@
Set-Content -Path $binCmd -Value $binCmdContent -Encoding ASCII

# Bash launcher in BinDir for WSL / Git Bash
$binBash = Join-Path $BinDir "ollamarouter"
$binBashContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
cd "`$DIR/.."
if [ -f "`$DIR/../OllamaRouter.exe" ]; then
    "`$DIR/../OllamaRouter.exe" "`$@"
elif [ -f "`$DIR/../OllamaRouter" ]; then
    "`$DIR/../OllamaRouter" "`$@"
fi
"@
Set-Content -Path $binBash -Value $binBashContent -Encoding UTF8 -NoNewline

# Idempotent User PATH update for BinDir
$pathAdded = $false
try {
    $userPath = [System.Environment]::GetEnvironmentVariable("Path", [System.EnvironmentVariableTarget]::User)
    $pathParts = if ($userPath) { $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } } else { @() }
    
    $alreadyInUserPath = $false
    foreach ($entry in $pathParts) {
        if ([string]::Equals($entry.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyInUserPath = $true
            break
        }
    }

    if (-not $alreadyInUserPath) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $BinDir } else { "$userPath;$BinDir" }
        [System.Environment]::SetEnvironmentVariable("Path", $newUserPath, [System.EnvironmentVariableTarget]::User)
        $pathAdded = $true
    }

    # Also update current session PATH
    $currentParts = $env:Path -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $alreadyInProcess = $false
    foreach ($entry in $currentParts) {
        if ([string]::Equals($entry.TrimEnd('\/'), $BinDir.TrimEnd('\/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyInProcess = $true
            break
        }
    }
    if (-not $alreadyInProcess) {
        $env:Path = "$env:Path;$BinDir"
    }
}
catch {
    [Console]::Error.WriteLine("Error: Failed to configure user PATH: $($_.Exception.Message)")
    exit 1
}

# 10. Summary
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " OllamaRouter Publish & Install Successful!               " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "Binary Location : $(Join-Path $InstallDir 'OllamaRouter.exe')" -ForegroundColor Green
Write-Host "Config Folder   : $ConfigDir" -ForegroundColor Green
Write-Host "State Folder    : $StateDir" -ForegroundColor Green
Write-Host "Logs Folder     : $LogsDir" -ForegroundColor Green
Write-Host "Launch Command  : 'ollamarouter'" -ForegroundColor Green

if ($pathAdded) {
    Write-Host "`nNote: Added '$BinDir' to your user PATH." -ForegroundColor Yellow
    Write-Host "Please open a new terminal window for PATH changes to take effect across your system." -ForegroundColor Yellow
}
