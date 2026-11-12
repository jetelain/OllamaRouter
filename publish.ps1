<#
.SYNOPSIS
    Builds, installs, and configures OllamaRouter locally in user AppData.
.DESCRIPTION
    Publishes the three Native AOT executables (ollamarouter-server daemon, ollamarouter CLI,
    ollamarouter-app Avalonia GUI), installs them to the user-local application directory
    (%LOCALAPPDATA%\OllamaRouter or ~/.ollamarouter), configures state, config,
    and logs directories, and sets up launchers and user PATH.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER InstallDir
    Target installation folder (default: %LOCALAPPDATA%\OllamaRouter or ~/.ollamarouter).
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
    [string]$StateDir = "",
    [string]$LogsDir = "",
    [string]$BinDir = "",
    [switch]$Uninstall,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

# ConfigDir is imposed by runtime
$ConfigDir = Join-Path $InstallDir "config"

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

    # Stop any running instances of OllamaRouter (daemon + desktop GUI)
    $runningProcesses = Get-Process -Name "ollamarouter-server", "OllamaRouter", "ollamarouter-app" -ErrorAction SilentlyContinue
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

    # Remove the user-scoped ollamarouter:// protocol handler registration (best effort, non-fatal).
    # Host-OS check: the registration lives on the host that ran the install/uninstall.
    try {
        if ([System.OperatingSystem]::IsWindows()) {
            $protocolKey = "HKCU:\Software\Classes\ollamarouter"
            if (Test-Path $protocolKey) {
                Remove-Item -Path $protocolKey -Recurse -Force
                Write-Host "Removed ollamarouter:// protocol handler registration."
            }
        }
        else {
            $desktopEntry = Join-Path $env:HOME ".local/share/applications/ollamarouter.desktop"
            if (Test-Path $desktopEntry) {
                Remove-Item -Path $desktopEntry -Force
                Write-Host "Removed ollamarouter:// desktop entry."
            }
        }
    }
    catch {
        Write-Warning "Could not remove the ollamarouter:// protocol handler registration: $($_.Exception.Message)"
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
$webProjectPath = Join-Path $repoRoot "src\OllamaRouter\OllamaRouter.csproj"
if (-not (Test-Path $webProjectPath)) {
    $webProjectPath = Join-Path $repoRoot "OllamaRouter\OllamaRouter.csproj"
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

# 3. Gracefully stop running processes to avoid publish file locks
# 3a. Best-effort graceful stop via the installed CLI shim (bounded 30s wait; never fails the install)
$cliShim = Join-Path $BinDir "ollamarouter.cmd"
if (-not (Test-Path $cliShim)) {
    $cliShim = Join-Path $BinDir "ollamarouter"
}
if (Test-Path $cliShim) {
    Write-Host "`nStopping running OllamaRouter daemon via 'ollamarouter stop' (best effort)..." -ForegroundColor Yellow
    try {
        $stopJob = Start-Job -ScriptBlock { param($shim) & $shim stop } -ArgumentList $cliShim
        if (Wait-Job $stopJob -Timeout 30) {
            Remove-Job $stopJob -Force -ErrorAction SilentlyContinue
        } else {
            Stop-Job $stopJob -ErrorAction SilentlyContinue
            Remove-Job $stopJob -Force -ErrorAction SilentlyContinue
            Write-Warning "'ollamarouter stop' did not complete within 30 seconds; falling back to Stop-Process."
        }
    }
    catch {
        Write-Warning "Best-effort 'ollamarouter stop' failed ($($_.Exception.Message)); falling back to Stop-Process."
    }
}

# 3b. Fallback: force-kill any remaining daemon/GUI processes
$runningProcesses = Get-Process -Name "ollamarouter-server", "OllamaRouter", "ollamarouter-app" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Warning "OllamaRouter is currently running. Stopping process to allow file updates..."
    $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 250
}

# 4. Ensure directories exist
foreach ($dir in @($InstallDir, $ConfigDir, $StateDir, $LogsDir, $BinDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# 5. Publish all three Native AOT executables (server -> cli -> app)
$publishProjects = @(
    @{ Name = "OllamaRouter server (ollamarouter-server)"; Path = (Join-Path $repoRoot "src\OllamaRouter\OllamaRouter.csproj") },
    @{ Name = "OllamaRouter CLI (ollamarouter)";           Path = (Join-Path $repoRoot "src\OllamaRouter.Cli\OllamaRouter.Cli.csproj") },
    @{ Name = "OllamaRouter App (ollamarouter-app)";       Path = (Join-Path $repoRoot "src\OllamaRouter.App\OllamaRouter.App.csproj") }
)

foreach ($proj in $publishProjects) {
    Write-Host "`nPublishing $($proj.Name)..." -ForegroundColor Yellow
    if (-not (Test-Path $proj.Path)) {
        throw "Project not found: $($proj.Path)"
    }
    & dotnet publish $proj.Path -c $Configuration -r $Runtime -p:DebugType=none -p:Version=$version -o $InstallDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($proj.Name) with exit code $LASTEXITCODE."
    }
}

# 6. Verify the published Native AOT binaries and record installed version
$exeSuffix = if ($Runtime -match 'win') { ".exe" } else { "" }
foreach ($binaryName in @("ollamarouter-server", "ollamarouter", "ollamarouter-app")) {
    $publishedBinary = Join-Path $InstallDir "$binaryName$exeSuffix"
    if (-not (Test-Path $publishedBinary)) {
        throw "Published binary not found in $InstallDir: $publishedBinary"
    }
}
Set-Content -Path (Join-Path $InstallDir "VERSION") -Value "$version`n"

# 6a. Upgrade cleanup: remove legacy single-binary artifacts (idempotent)
foreach ($legacyName in @("OllamaRouter.exe", "OllamaRouter", "ollamarouter-web.cmd")) {
    $legacyPath = Join-Path $InstallDir $legacyName
    if (Test-Path $legacyPath) {
        Remove-Item -Path $legacyPath -Force -ErrorAction SilentlyContinue
    }
}

# 6b. Register the ollamarouter:// protocol handler (user-scoped, NO elevation required; best effort, non-fatal)
$protocolRegistered = $false
if ($Runtime -match 'win') {
    try {
        $appExe = Join-Path $InstallDir "ollamarouter-app.exe"
        $protocolKey = "HKCU:\Software\Classes\ollamarouter"
        New-Item -Path $protocolKey -Force | Out-Null
        Set-ItemProperty -Path $protocolKey -Name "(Default)" -Value "URL:OllamaRouter Protocol"
        Set-ItemProperty -Path $protocolKey -Name "URL Protocol" -Value ""
        New-Item -Path "$protocolKey\DefaultIcon" -Force | Out-Null
        $iconValue = '"' + $appExe + '",0'
        Set-ItemProperty -Path "$protocolKey\DefaultIcon" -Name "(Default)" -Value $iconValue
        New-Item -Path "$protocolKey\shell\open\command" -Force | Out-Null
        $commandValue = '"' + $appExe + '" "%1"'
        Set-ItemProperty -Path "$protocolKey\shell\open\command" -Name "(Default)" -Value $commandValue
        $protocolRegistered = $true
    }
    catch {
        Write-Warning "Could not register the ollamarouter:// protocol handler: $($_.Exception.Message)"
    }
}

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
Write-Host "  - LogsDirectory     : $logsDirPath" -ForegroundColor Gray

# 8. Launcher Commands & PATH configuration
Write-Host "`nSetting up launcher commands..." -ForegroundColor Yellow

# In InstallDir: legacy alias, now forwarded to the CLI binary
$installCmd = Join-Path $InstallDir "ollamarouter.cmd"
$installCmdContent = @"
@echo off
setlocal
cd /d "%~dp0"
"%~dp0ollamarouter.exe" %*
"@
Set-Content -Path $installCmd -Value $installCmdContent -Encoding ASCII

# In BinDir: CLI launcher on PATH (forwards all arguments to the CLI binary)
$binCmd = Join-Path $BinDir "ollamarouter.cmd"
$binCmdContent = @"
@echo off
setlocal
cd /d "%~dp0.."
"%~dp0..\ollamarouter.exe" %*
"@
Set-Content -Path $binCmd -Value $binCmdContent -Encoding ASCII

# In BinDir: desktop GUI launcher on PATH
$binAppCmd = Join-Path $BinDir "ollamarouter-app.cmd"
$binAppCmdContent = @"
@echo off
setlocal
cd /d "%~dp0.."
"%~dp0..\ollamarouter-app.exe" %*
"@
Set-Content -Path $binAppCmd -Value $binAppCmdContent -Encoding ASCII

# Bash launcher in BinDir for WSL / Git Bash (CLI, forwards all arguments)
$binBash = Join-Path $BinDir "ollamarouter"
$binBashContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
cd "`$DIR/.."
if [ -f "`$DIR/../ollamarouter.exe" ]; then
    "`$DIR/../ollamarouter.exe" "`$@"
elif [ -f "`$DIR/../ollamarouter" ]; then
    "`$DIR/../ollamarouter" "`$@"
fi
"@

# Remove legacy web alias left over from single-binary installs
$installWebCmd = Join-Path $InstallDir "ollamarouter-web.cmd"
if (Test-Path $installWebCmd) {
    Remove-Item -Path $installWebCmd -Force -ErrorAction SilentlyContinue
}
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
Write-Host "Server Binary   : $(Join-Path $InstallDir "ollamarouter-server$exeSuffix")" -ForegroundColor Green
Write-Host "CLI Binary      : $(Join-Path $InstallDir "ollamarouter$exeSuffix")" -ForegroundColor Green
Write-Host "App Binary      : $(Join-Path $InstallDir "ollamarouter-app$exeSuffix")" -ForegroundColor Green
Write-Host "Config Folder   : $ConfigDir" -ForegroundColor Green
Write-Host "State Folder    : $StateDir" -ForegroundColor Green
Write-Host "Logs Folder     : $LogsDir" -ForegroundColor Green
Write-Host "CLI Command     : 'ollamarouter'" -ForegroundColor Green
Write-Host "GUI Command     : 'ollamarouter-app'" -ForegroundColor Green
if ($protocolRegistered) {
    Write-Host "URI Protocol    : 'ollamarouter://' handler registered (ollamarouter://open, ollamarouter://wizard)" -ForegroundColor Green
}

if ($pathAdded) {
    Write-Host "`nNote: Added '$BinDir' to your user PATH." -ForegroundColor Yellow
    Write-Host "Please open a new terminal window for PATH changes to take effect across your system." -ForegroundColor Yellow
}
