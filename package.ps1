<#
.SYNOPSIS
    Packages OllamaRouter's three Native AOT binaries into a release archive (.zip on Windows, .tar.gz on Linux/macOS).
.DESCRIPTION
    Publishes src/OllamaRouter (ollamarouter-server), src/OllamaRouter.Cli (ollamarouter), and
    src/OllamaRouter.App (ollamarouter-app) with Native AOT, stages the publish.ps1 layout
    (portable appsettings.Production.json, root ollamarouter.cmd on Windows, bin/ shims,
    VERSION, README.md, and LICENSE), and writes OllamaRouter-<Runtime>.zip (win-*) or
    flat-root OllamaRouter-<Runtime>.tar.gz (linux-*/osx-*) to OutputDir.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.PARAMETER Version
    Optional explicit release version override.
.PARAMETER OutputDir
    Destination directory for generated archive (default: ./artifacts).
.EXAMPLE
    .\package.ps1
.EXAMPLE
    .\package.ps1 -Runtime linux-x64 -OutputDir ..\NightCodeForge\artifacts\bundle
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputDir = "./artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$serverProj = Join-Path $repoRoot "src\OllamaRouter\OllamaRouter.csproj"
$cliProj = Join-Path $repoRoot "src\OllamaRouter.Cli\OllamaRouter.Cli.csproj"
$appProj = Join-Path $repoRoot "src\OllamaRouter.App\OllamaRouter.App.csproj"

foreach ($proj in @($serverProj, $cliProj, $appProj)) {
    if (-not (Test-Path $proj)) {
        throw "Required OllamaRouter project not found: $proj"
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projXml = Get-Content $serverProj
    $versionNode = $projXml.SelectSingleNode("//Version")
    $Version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        $versionNode.InnerText.Trim()
    } else {
        "1.0.0"
    }
}

$resolvedOutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$isWindowsRid = $Runtime -like "win-*"
$exeSuffix = if ($isWindowsRid) { ".exe" } else { "" }
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ncf-or-pkg-" + [System.Guid]::NewGuid().ToString("N"))
$binDir = Join-Path $stagingDir "bin"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null

    $projects = @(
        @{ Name = "ollamarouter-server"; Path = $serverProj },
        @{ Name = "ollamarouter";        Path = $cliProj },
        @{ Name = "ollamarouter-app";    Path = $appProj }
    )

    foreach ($p in $projects) {
        Write-Host "Publishing $($p.Name) ($Runtime, $Version)..." -ForegroundColor Cyan
        & dotnet publish $p.Path `
            -c $Configuration `
            -r $Runtime `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:DebugType=none `
            -p:DebugSymbols=false `
            "-p:Version=$Version" `
            -o $stagingDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish for $($p.Name) failed with exit code $LASTEXITCODE."
        }

        $expectedBinary = Join-Path $stagingDir ($p.Name + $exeSuffix)
        if (-not (Test-Path $expectedBinary)) {
            throw "Expected Native AOT binary not found: $expectedBinary"
        }
    }

    Get-ChildItem -Path $stagingDir -Include "*.pdb", "*.dbg" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Portable appsettings.Production.json matching publish.ps1 schema with relative state/logs paths.
    $prodSettings = @"
{
  "OllamaRouter": {
    "StateDirectory": "state"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "OllamaRouter": "Information"
    },
    "LogsDirectory": "logs"
  }
}
"@
    $prodSettings = ($prodSettings -replace "`r`n", "`n").TrimEnd("`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $stagingDir "appsettings.Production.json"), $prodSettings, $utf8NoBom)

    if ($isWindowsRid) {
        $rootCmd = "@echo off`r`nsetlocal`r`ncd /d `"%~dp0`"`r`n`"%~dp0ollamarouter.exe`" %*`r`n"
        [System.IO.File]::WriteAllText((Join-Path $stagingDir "ollamarouter.cmd"), $rootCmd, [System.Text.Encoding]::ASCII)

        $binCliCmd = "@echo off`r`nsetlocal`r`ncd /d `"%~dp0..`"`r`n`"%~dp0..\ollamarouter.exe`" %*`r`n"
        [System.IO.File]::WriteAllText((Join-Path $binDir "ollamarouter.cmd"), $binCliCmd, [System.Text.Encoding]::ASCII)

        $binAppCmd = "@echo off`r`nsetlocal`r`ncd /d `"%~dp0..`"`r`n`"%~dp0..\ollamarouter-app.exe`" %*`r`n"
        [System.IO.File]::WriteAllText((Join-Path $binDir "ollamarouter-app.cmd"), $binAppCmd, [System.Text.Encoding]::ASCII)

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
        $binBashContent = ($binBashContent -replace "`r`n", "`n").TrimEnd("`n") + "`n"
        [System.IO.File]::WriteAllText((Join-Path $binDir "ollamarouter"), $binBashContent, $utf8NoBom)
    } else {
        $unixCliContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
cd "`$DIR/.."
exec "`$DIR/../ollamarouter" "`$@"
"@
        $unixCliContent = ($unixCliContent -replace "`r`n", "`n").TrimEnd("`n") + "`n"
        $unixCliPath = Join-Path $binDir "ollamarouter"
        [System.IO.File]::WriteAllText($unixCliPath, $unixCliContent, $utf8NoBom)

        $unixAppContent = @"
#!/usr/bin/env bash
DIR="`$( cd "`$( dirname "`${BASH_SOURCE[0]}" )" && pwd )"
cd "`$DIR/.."
exec "`$DIR/../ollamarouter-app" "`$@"
"@
        $unixAppContent = ($unixAppContent -replace "`r`n", "`n").TrimEnd("`n") + "`n"
        $unixAppPath = Join-Path $binDir "ollamarouter-app"
        [System.IO.File]::WriteAllText($unixAppPath, $unixAppContent, $utf8NoBom)

        if (-not [System.OperatingSystem]::IsWindows()) {
            foreach ($exePath in @(
                (Join-Path $stagingDir "ollamarouter-server"),
                (Join-Path $stagingDir "ollamarouter"),
                (Join-Path $stagingDir "ollamarouter-app"),
                $unixCliPath,
                $unixAppPath
            )) {
                if (Test-Path $exePath) {
                    & chmod +x $exePath
                }
            }
        }
    }

    [System.IO.File]::WriteAllText((Join-Path $stagingDir "VERSION"), "$Version`n", $utf8NoBom)
    Copy-Item -Path (Join-Path $repoRoot "README.md") -Destination $stagingDir -Force
    Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $stagingDir -Force

    if ($isWindowsRid) {
        $archivePath = Join-Path $resolvedOutputDir "OllamaRouter-$Runtime.zip"
        if (Test-Path $archivePath) { Remove-Item -Path $archivePath -Force }
        Write-Host "Creating archive $archivePath..." -ForegroundColor Cyan
        Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $archivePath -Force
    } else {
        $archivePath = Join-Path $resolvedOutputDir "OllamaRouter-$Runtime.tar.gz"
        if (Test-Path $archivePath) { Remove-Item -Path $archivePath -Force }
        Write-Host "Creating archive $archivePath..." -ForegroundColor Cyan
        & tar -czf $archivePath -C $stagingDir .
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed creating '$archivePath' with exit code $LASTEXITCODE."
        }
    }

    Write-Host "OllamaRouter packaging complete in: $resolvedOutputDir" -ForegroundColor Green
}
finally {
    if (Test-Path $stagingDir) {
        Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
