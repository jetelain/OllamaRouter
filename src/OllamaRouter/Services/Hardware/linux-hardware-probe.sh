#!/usr/bin/env bash
# ============================================================================
# OllamaRouter Linux hardware probe (task #143).
#
# The SINGLE Linux collector of the daemon: this script is embedded in the
# OllamaRouter assembly and executed by LinuxHardwareProbeExecutor -- locally
# via `bash` (the script text is fed over stdin) or remotely over ssh (the
# script travels base64-encoded and is decoded + run on the remote host).
#
# Every sub-probe degrades INDEPENDENTLY (command -v guards + 2>/dev/null +
# no `set -e`), so the script ALWAYS emits the complete ===BEGIN_HW=== /
# ===END_HW=== marker frame on stdout -- even when every probe tool is
# missing or every sub-probe fails.
#
# The script ONLY COLLECTS raw values: all parsing, GPU classification and
# metric derivation happen in the C# digest (LinuxHardwareDigest.TryBuildProfile).
# This file must not contain that logic.
#
# Dependencies: bash + coreutils (grep/sed/awk/head) only. The probe tools
# (dmidecode, nvidia-smi, rocm-smi, lspci) are OPTIONAL -- a missing tool
# simply leaves its RAW section empty. Works unprivileged: sudo is attempted
# NON-INTERACTIVELY (`sudo -n`) for dmidecode first, then the bare command.
# ============================================================================

# --- basics (hostname / architecture / CPU brand / total RAM) ---------------
hostname_value="$(uname -n 2>/dev/null)"
[ -n "$hostname_value" ] || hostname_value="$(hostname 2>/dev/null)"
[ -n "$hostname_value" ] || hostname_value="linux-node"

arch_value="$(uname -m 2>/dev/null)"
[ -n "$arch_value" ] || arch_value="x86_64"

cpu_value="$(sed -n 's/^model name[[:space:]]*:[[:space:]]*//p' /proc/cpuinfo 2>/dev/null | head -n 1)"
if [ -z "$cpu_value" ]; then
    cpu_value="$(lscpu 2>/dev/null | sed -n 's/^Model name[[:space:]]*:[[:space:]]*//p' | head -n 1)"
fi

ram_mb="$(awk '/^MemTotal:/ { printf "%d", $2 / 1024; exit }' /proc/meminfo 2>/dev/null)"
case "$ram_mb" in
    ''|*[!0-9]*) ram_mb=0 ;;
esac

# --- dmidecode memory (non-interactive sudo first, then the bare command) ----
dmidecode_output="$(sudo -n dmidecode -t memory 2>/dev/null)"
if [ -z "$dmidecode_output" ]; then
    dmidecode_output="$(dmidecode -t memory 2>/dev/null)"
fi

# --- GPU RAW sections (the C# digest takes the first non-empty one) ----------
nvidia_output=""
if command -v nvidia-smi >/dev/null 2>&1; then
    nvidia_output="$(nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits 2>/dev/null)"
fi

rocm_output=""
if command -v rocm-smi >/dev/null 2>&1; then
    rocm_output="$(rocm-smi --showmeminfo vram --json 2>/dev/null)"
fi

lspci_output=""
if command -v lspci >/dev/null 2>&1; then
    lspci_output="$(lspci | grep -iE 'vga|3d|display' 2>/dev/null)"
fi

# --- marker frame (ALWAYS complete, always emitted last) ----------------------
printf '===BEGIN_HW===\n'
printf 'HOSTNAME=%s\n' "$hostname_value"
printf 'ARCH=%s\n' "$arch_value"
printf 'CPU=%s\n' "$cpu_value"
printf 'RAM_MB=%s\n' "$ram_mb"
printf -- '---DMIDECODE_MEMORY---\n'
printf '%s\n' "$dmidecode_output"
printf -- '---NVIDIA_GPUS---\n'
printf '%s\n' "$nvidia_output"
printf -- '---ROCM_GPUS---\n'
printf '%s\n' "$rocm_output"
printf -- '---LSPCI_GPUS---\n'
printf '%s\n' "$lspci_output"
printf '===END_HW===\n'
