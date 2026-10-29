<#
.SYNOPSIS
    Queries hardware profile from a remote Linux node via SSH and registers it with OllamaRouter.
.DESCRIPTION
    Uses Windows OpenSSH (ssh.exe) to run a fail-soft probe script on a remote Linux host,
    extracts system memory (size, DDR tier, channels), CPU architecture, and GPU details
    (NVIDIA via nvidia-smi, AMD via rocm-smi, or fallback via lspci), computes the derived
    fast memory and effective bandwidth metrics, and submits the hardware profile to
    OllamaRouter via PUT /api/router/fleet/hardware/{targetName}.
.PARAMETER SshTarget
    The SSH destination, such as "user@192.168.1.50" or "aiserver".
.PARAMETER TargetName
    The target name as configured in OllamaRouter (e.g. "Remote"). If omitted, the script
    attempts to match the target from the OllamaRouter fleet list or defaults to the remote hostname.
.PARAMETER RouterUrl
    Base URL of the OllamaRouter instance (default: http://localhost:11434).
.PARAMETER Port
    SSH port number (default: 22).
.PARAMETER IdentityFile
    Path to an SSH private key file (-i).
.PARAMETER DryRun
    If specified, queries the hardware and prints the JSON profile without posting to the API.
.EXAMPLE
    .\query-linux-hardware.ps1 -SshTarget user@aiserver -TargetName Remote
.EXAMPLE
    .\query-linux-hardware.ps1 user@192.168.1.50 -DryRun
.EXAMPLE
    .\query-linux-hardware.ps1 -SshTarget user@node1 -Port 2222 -IdentityFile ~/.ssh/id_rsa
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$SshTarget,

    [Parameter(Position = 1)]
    [string]$TargetName = "",

    [string]$RouterUrl = "http://localhost:11434",

    [int]$Port = 22,

    [string]$IdentityFile = "",

    [string[]]$SshOptions = @(),

    [double]$BandwidthGBs = 0.0,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# Ensure RouterUrl has no trailing slash
$RouterUrl = $RouterUrl.TrimEnd('/')

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " OllamaRouter - Linux Node Hardware Profiler (SSH)        " -ForegroundColor Cyan
Write-Host " SSH Target : $SshTarget"
Write-Host " SSH Port   : $Port"
if ($TargetName) {
    Write-Host " Fleet Target: $TargetName"
}
Write-Host " Router URL : $RouterUrl"
if ($DryRun) {
    Write-Host " Mode       : DryRun (will not post to API)" -ForegroundColor Yellow
}
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verify ssh.exe is available
$sshCmd = Get-Command "ssh" -ErrorAction SilentlyContinue
if (-not $sshCmd) {
    Write-Error "Windows OpenSSH client ('ssh.exe') was not found in PATH. Please install OpenSSH Client via Windows Optional Features."
    exit 1
}

# 2. Remote bash probe script
# Designed to be fail-soft, banner-resistant, and work without root privileges
$remoteBash = @'
echo "===BEGIN_HW==="
echo "HOSTNAME=$(uname -n 2>/dev/null || hostname 2>/dev/null || echo 'linux-node')"
echo "ARCH=$(uname -m 2>/dev/null || echo 'x86_64')"

CPU=$(grep -m1 "model name" /proc/cpuinfo 2>/dev/null | cut -d: -f2- | sed -e 's/^[ \t]*//')
if [ -z "$CPU" ]; then
    CPU=$(lscpu 2>/dev/null | grep -i "Model name:" | head -n1 | cut -d: -f2- | sed -e 's/^[ \t]*//')
fi
echo "CPU=$CPU"

MEM_KB=$(grep -i MemTotal /proc/meminfo 2>/dev/null | awk '{print $2}')
if [ -n "$MEM_KB" ]; then
    echo "RAM_MB=$((MEM_KB / 1024))"
else
    echo "RAM_MB=0"
fi

# Query dmidecode (try sudo -n non-interactive, then bare dmidecode)
DMI=$(sudo -n dmidecode -t memory 2>/dev/null || dmidecode -t memory 2>/dev/null || true)
RAM_TYPE="Unknown"
CHANNELS="0"
if [ -n "$DMI" ]; then
    TYPE_LINE=$(echo "$DMI" | grep -iE '^\s*Type:\s+(DDR|LPDDR)' | head -n1 | awk '{print $2}')
    [ -n "$TYPE_LINE" ] && RAM_TYPE="$TYPE_LINE"
    CHANNELS=$(echo "$DMI" | grep -E '^\s*Locator:' | awk '{print $2}' | sed 's/[0-9]*$//' | sort -u | grep -v '^$' | wc -l)
fi
echo "RAM_TYPE=$RAM_TYPE"
echo "CHANNELS=$CHANNELS"

echo "---NVIDIA_GPUS---"
if command -v nvidia-smi >/dev/null 2>&1; then
    nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits 2>/dev/null || true
fi

echo "---ROCM_GPUS---"
if command -v rocm-smi >/dev/null 2>&1; then
    rocm-smi --showmeminfo vram --json 2>/dev/null || true
fi

echo "---LSPCI_GPUS---"
if command -v lspci >/dev/null 2>&1; then
    lspci 2>/dev/null | grep -iE 'vga|3d|display' || true
fi
echo "===END_HW==="
'@

# 3. Execute SSH command
Write-Host "`nConnecting to $SshTarget via SSH..." -ForegroundColor Yellow

$bytes = [System.Text.Encoding]::UTF8.GetBytes($remoteBash)
$b64 = [Convert]::ToBase64String($bytes)
$remoteCommand = "echo $b64 | base64 -d | bash"

$sshArgs = @("-p", "$Port")
if ($IdentityFile) {
    if (-not (Test-Path $IdentityFile)) {
        Write-Error "Identity file not found: $IdentityFile"
        exit 1
    }
    $sshArgs += @("-i", $IdentityFile)
}
if ($SshOptions) {
    $sshArgs += $SshOptions
}
$sshArgs += @($SshTarget, $remoteCommand)

$rawOutput = & $sshCmd.Source @sshArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "SSH command failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# 4. Parse the output between ===BEGIN_HW=== and ===END_HW===
$lines = $rawOutput -split "`r?`n"
$startIndex = -1
$endIndex = -1
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match "===BEGIN_HW===") { $startIndex = $i }
    if ($lines[$i] -match "===END_HW===") { $endIndex = $i; break }
}

if ($startIndex -lt 0 -or $endIndex -lt 0 -or $endIndex -le $startIndex) {
    Write-Error "Failed to parse hardware information markers from SSH output. Raw output:`n$rawOutput"
    exit 1
}

$hwLines = $lines[($startIndex + 1)..($endIndex - 1)]

# State parsing
$data = @{
    HOSTNAME = "linux-node"
    ARCH = "x86_64"
    CPU = ""
    RAM_MB = 0
    RAM_TYPE = "Unknown"
    CHANNELS = 0
}

$section = "MAIN"
$nvidiaRows = [System.Collections.Generic.List[string]]::new()
$rocmJsonLines = [System.Collections.Generic.List[string]]::new()
$lspciRows = [System.Collections.Generic.List[string]]::new()

foreach ($line in $hwLines) {
    $trimmed = $line.Trim()
    if ($trimmed -eq "---NVIDIA_GPUS---") { $section = "NVIDIA"; continue }
    if ($trimmed -eq "---ROCM_GPUS---") { $section = "ROCM"; continue }
    if ($trimmed -eq "---LSPCI_GPUS---") { $section = "LSPCI"; continue }

    switch ($section) {
        "MAIN" {
            $eq = $trimmed.IndexOf('=')
            if ($eq -gt 0) {
                $k = $trimmed.Substring(0, $eq).Trim()
                $v = $trimmed.Substring($eq + 1).Trim()
                $data[$k] = $v
            }
        }
        "NVIDIA" {
            if ($trimmed.Length -gt 0) { $nvidiaRows.Add($trimmed) }
        }
        "ROCM" {
            if ($trimmed.Length -gt 0) { $rocmJsonLines.Add($trimmed) }
        }
        "LSPCI" {
            if ($trimmed.Length -gt 0) { $lspciRows.Add($trimmed) }
        }
    }
}

# 5. Normalize System Attributes
$nodeName = [string]$data["HOSTNAME"]
$rawArch = [string]$data["ARCH"]
$architecture = if ($rawArch -match "x86_64|amd64") { "x64" }
                elseif ($rawArch -match "aarch64|arm64") { "arm64" }
                else { $rawArch.ToLowerInvariant() }

$cpuBrand = [string]$data["CPU"]
$systemRamMB = [long]0
[long]::TryParse([string]$data["RAM_MB"], [ref]$systemRamMB) | Out-Null

$rawRamType = ([string]$data["RAM_TYPE"]).ToUpperInvariant()
$systemRamType = if ($rawRamType -match "LPDDR5") { "LpDdr5X" }
                 elseif ($rawRamType -match "DDR5") { "Ddr5" }
                 elseif ($rawRamType -match "DDR4") { "Ddr4" }
                 elseif ($rawRamType -match "DDR3") { "Ddr3" }
                 else { "Unknown" }

$ramChannels = [int]0
[int]::TryParse([string]$data["CHANNELS"], [ref]$ramChannels) | Out-Null

# 6. Parse and classify GPUs
$gpus = [System.Collections.Generic.List[PSObject]]::new()

function Classify-NvidiaGpu([string]$name) {
    $u = $name.ToUpperInvariant()
    if ($u -match "H100|H200|HOPPER|BLACKWELL|B200|B100|B40") {
        return @{ Type = "HBM3"; Bandwidth = 777.5 }
    }
    if ($u -match "A100|A30|GA100") {
        return @{ Type = "HBM2E"; Bandwidth = 777.5 }
    }
    if ($u -match "3090|4080|4090|RTX 4|RTX 309|4070|3080|3070 TI") {
        return @{ Type = "GDDR6X"; Bandwidth = 468.0 }
    }
    return @{ Type = "GDDR6"; Bandwidth = 256.0 }
}

function Classify-AmdGpu([string]$name, [int]$vramMB) {
    $u = $name.ToUpperInvariant()
    if ($u -match "MI300|MI325") {
        return @{ Type = "HBM3"; Bandwidth = 777.5 }
    }
    if ($u -match "MI250|MI200|MI100") {
        return @{ Type = "HBM2E"; Bandwidth = 777.5 }
    }
    if ($vramMB -ge 8192 -or $u -match "7900|7800|6900|6800") {
        return @{ Type = "GDDR6"; Bandwidth = 256.0 }
    }
    return @{ Type = "Unknown"; Bandwidth = 0.0 }
}

function Clean-GpuName([string]$vendor, [string]$raw) {
    $n = $raw.Trim()
    $n = $n -ireplace "^Advanced\s+Micro\s+Devices,?\s*Inc\.?\s*(\[AMD/ATI\])?\s*", ""
    while ($n -imatch "^NVIDIA\s+") { $n = $n -ireplace "^NVIDIA\s+", "" }
    while ($n -imatch "^AMD\s+") { $n = $n -ireplace "^AMD\s+", "" }
    return $n.Trim()
}

# 6a. NVIDIA GPUs via nvidia-smi
if ($nvidiaRows.Count -gt 0) {
    foreach ($row in $nvidiaRows) {
        $parts = $row -split ',', 2
        if ($parts.Length -lt 2) { continue }
        $rawModel = $parts[0].Trim()
        $model = Clean-GpuName "Nvidia" $rawModel
        $vram = [int]0
        $vramStr = ($parts[1] -replace '[^\d]', '').Trim()
        [int]::TryParse($vramStr, [ref]$vram) | Out-Null
        $classification = Classify-NvidiaGpu $model

        $gpus.Add([PSCustomObject]@{
            vendor = "Nvidia"
            modelName = $model
            vramMB = $vram
            vramType = $classification.Type
            busWidthBits = 0
            bandwidthGBs = [double]$classification.Bandwidth
        })
    }
}

# 6b. AMD GPUs via rocm-smi
if ($gpus.Count -eq 0 -and $rocmJsonLines.Count -gt 0) {
    $rocmJson = $rocmJsonLines -join "`n"
    $matches = [regex]::Matches($rocmJson, '"vram_total"\s*:\s*(\d+)')
    foreach ($match in $matches) {
        $totalKb = [long]$match.Groups[1].Value
        $vramMB = [int][Math]::Max(0, [math]::Floor($totalKb / 1024))
        $classification = Classify-AmdGpu "AMD GPU (rocm-smi)" $vramMB

        $gpus.Add([PSCustomObject]@{
            vendor = "AMD"
            modelName = "AMD GPU (rocm-smi)"
            vramMB = $vramMB
            vramType = $classification.Type
            busWidthBits = 0
            bandwidthGBs = [double]$classification.Bandwidth
        })
    }
}

# 6c. Fallback via lspci
if ($gpus.Count -eq 0 -and $lspciRows.Count -gt 0) {
    foreach ($row in $lspciRows) {
        $vendor = "Unknown"
        if ($row -match "NVIDIA") { $vendor = "Nvidia" }
        elseif ($row -match "Advanced Micro Devices|AMD|Radeon") { $vendor = "AMD" }
        elseif ($row -match "Intel") { $vendor = "Intel" }

        # Extract description after controller type
        $colonIndex = $row.IndexOf(': ')
        $name = if ($colonIndex -gt 0) { $row.Substring($colonIndex + 2).Trim() } else { $row.Trim() }
        $name = Clean-GpuName $vendor $name

        $gpus.Add([PSCustomObject]@{
            vendor = $vendor
            modelName = $name
            vramMB = 0
            vramType = "Unknown"
            busWidthBits = 0
            bandwidthGBs = 0.0
        })
    }
}

# 7. Derive hardware metrics
function Get-RamBandwidth([string]$ramType, [int]$channels, [string]$cpu) {
    if ($ramType -eq "LpDdr5X") {
        $ch = $channels
        if ($ch -lt 4 -and ($cpu -match "MAX|STRIX|8060|8050")) {
            $ch = 4
        }
        if ($ch -le 0) { $ch = 4 }
        return 85.3 * $ch
    }
    if ($ramType -eq "AppleUnified") {
        return 100.0
    }
    $mult = if ($channels -ge 4) { 2.0 } else { 1.0 }
    switch ($ramType) {
        "Ddr5" { return 38.4 * $mult }
        "Ddr4" { return 25.6 * $mult }
        "Ddr3" { return 12.8 * $mult }
        default { return 0.0 }
    }
}

# Check for discrete GPUs with dedicated VRAM (> 2048 MB) or NVIDIA discrete cards
$hasDiscreteGpu = $false
foreach ($g in $gpus) {
    if ($g.vendor -eq "Nvidia" -or $g.vramMB -gt 2048) {
        $hasDiscreteGpu = $true
        break
    }
}

$isUnified = ($systemRamType -eq "LpDdr5X" -and -not $hasDiscreteGpu -and $cpuBrand -match "AMD") -or
             ($systemRamType -eq "AppleUnified")

$totalFastMemoryMB = [long]0
$effectiveBandwidthGBs = [double]0.0

if ($isUnified) {
    $totalFastMemoryMB = $systemRamMB
    $effectiveBandwidthGBs = Get-RamBandwidth $systemRamType $ramChannels $cpuBrand

    # Update integrated APU GPU entries with unified memory tier and bandwidth
    for ($i = 0; $i -lt $gpus.Count; $i++) {
        if ($gpus[$i].vramType -eq "Unknown" -or [string]::IsNullOrWhiteSpace($gpus[$i].vramType)) {
            $gpus[$i].vramType = $systemRamType
        }
        if ($gpus[$i].bandwidthGBs -le 0) {
            $gpus[$i].bandwidthGBs = $effectiveBandwidthGBs
        }
    }
} else {
    $gpuVramSum = [long]0
    foreach ($g in $gpus) {
        $gpuVramSum += [long]$g.vramMB
    }
    $totalFastMemoryMB = if ($gpuVramSum -gt 0) { $gpuVramSum } else { $systemRamMB }

    if ($gpus.Count -eq 0) {
        $effectiveBandwidthGBs = Get-RamBandwidth $systemRamType $ramChannels $cpuBrand
    } else {
        foreach ($g in $gpus) {
            if ($g.bandwidthGBs -gt $effectiveBandwidthGBs) {
                $effectiveBandwidthGBs = $g.bandwidthGBs
            }
        }
    }
}

if ($BandwidthGBs -gt 0) {
    $effectiveBandwidthGBs = [double]$BandwidthGBs
    for ($i = 0; $i -lt $gpus.Count; $i++) {
        if ($isUnified -or $gpus[$i].bandwidthGBs -le 0) {
            $gpus[$i].bandwidthGBs = [double]$BandwidthGBs
        }
    }
}

# 8. Build NodeHardwareProfile object
$profile = [ordered]@{
    nodeName = $nodeName
    os = "linux"
    architecture = $architecture
    isUnifiedMemory = [bool]$isUnified
    systemRamMB = [long]$systemRamMB
    systemRamType = $systemRamType
    gpus = $gpus
    totalFastMemoryMB = [long]$totalFastMemoryMB
    effectiveBandwidthGBs = [double]$effectiveBandwidthGBs
}

$jsonBody = $profile | ConvertTo-Json -Depth 5

Write-Host "`nProbed Hardware Profile:" -ForegroundColor Green
Write-Host " Node Name        : $nodeName"
Write-Host " OS / Arch        : linux / $architecture"
Write-Host " CPU Model        : $cpuBrand"
Write-Host " System RAM       : $systemRamMB MB ($systemRamType, $ramChannels channel(s))"
Write-Host " Unified Memory   : $isUnified"
Write-Host " GPU Count        : $($gpus.Count)"
foreach ($g in $gpus) {
    Write-Host "   - $($g.vendor) $($g.modelName) ($($g.vramMB) MB, $($g.vramType), $($g.bandwidthGBs) GB/s)" -ForegroundColor Cyan
}
Write-Host " Total Fast Memory: $totalFastMemoryMB MB"
Write-Host " Effective Bandw. : $effectiveBandwidthGBs GB/s"

if ($DryRun) {
    Write-Host "`nJSON Payload (DryRun):" -ForegroundColor Magenta
    Write-Host $jsonBody
    Write-Host "`nDryRun completed. No request was sent to OllamaRouter." -ForegroundColor Green
    return
}

# 9. Resolve TargetName in OllamaRouter if not provided
if (-not $TargetName) {
    Write-Host "`nResolving target in OllamaRouter..." -ForegroundColor Yellow
    try {
        $fleetResp = Invoke-RestMethod -Uri "$RouterUrl/api/router/fleet" -Method Get -TimeoutSec 5
        $targets = $fleetResp.targets | Where-Object { $_.name -ne "Cloud" -and $_.name -ne "Local" }
        if ($targets) {
            # Try to match by hostname or IP in target URL
            $matched = $targets | Where-Object { $_.url -and ($_.url -match [regex]::Escape($nodeName) -or $_.url -match [regex]::Escape($SshTarget.Split('@')[-1])) }
            if ($matched) {
                $TargetName = $matched[0].name
                Write-Host "Auto-matched target '$TargetName' based on URL/hostname." -ForegroundColor Green
            } elseif ($targets.Count -eq 1) {
                $TargetName = $targets[0].name
                Write-Host "Using single configured remote target '$TargetName'." -ForegroundColor Green
            }
        }
    }
    catch {
        Write-Warning "Could not retrieve fleet list from $($RouterUrl): $($_.Exception.Message)"
    }

    if (-not $TargetName) {
        $TargetName = $nodeName
        Write-Host "Using remote hostname '$TargetName' as TargetName." -ForegroundColor Yellow
    }
}

# 10. Post to OllamaRouter API
$endpoint = "$RouterUrl/api/router/fleet/hardware/$TargetName"
Write-Host "`nPosting profile to $endpoint..." -ForegroundColor Yellow

try {
    $resp = Invoke-RestMethod -Uri $endpoint -Method Put -Body $jsonBody -ContentType "application/json; charset=utf-8"
    Write-Host "Successfully registered hardware profile for target '$TargetName'!" -ForegroundColor Green
    Write-Host ($resp | ConvertTo-Json -Depth 4)
}
catch {
    Write-Error "Failed to update hardware profile on OllamaRouter: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = [System.IO.StreamReader]::new($stream)
            $body = $reader.ReadToEnd()
            Write-Host "Server response: $body" -ForegroundColor Red
        } catch { }
    }
    exit 1
}
