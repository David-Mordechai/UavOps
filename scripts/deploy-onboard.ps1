<#
.SYNOPSIS
  Deploys everything onboard - the onboard service (UavOps.Onboard.Detector: detector, tracker,
  mission executive), its TensorRT detector and the verifier VLM - to the onboard computer (a
  Jetson Orin Nano Super), and (re)starts it there.

.DESCRIPTION
  Publishes the service self-contained for linux-arm64, copies it over SSH, then runs
  scripts/onboard/setup-onboard.sh on the device, which builds what's missing there (the TensorRT
  engine, the native shim, llama.cpp) and installs two systemd user services, uavops-vlm and
  uavops-onboard. Idempotent: a redeploy only replaces the app and restarts.

  The device defaults to the host of Simulator:OnboardUrl in src/UavOps.Simulator/appsettings.json,
  so the simulator and this script always point at the same onboard computer.

  SSH must log in with a key (run `ssh-copy-id davidm@<device>` once yourself); nothing here types
  a password.

.PARAMETER Device
  The onboard computer's address. Default: the host in Simulator:OnboardUrl.
.PARAMETER User
  SSH user on it. Default: davidm.
#>
[CmdletBinding()]
param(
    [string]$Device,
    [string]$User = "davidm"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Device) {
    $settings = Get-Content (Join-Path $repoRoot "src\UavOps.Simulator\appsettings.json") -Raw | ConvertFrom-Json
    $Device = ([Uri]$settings.Simulator.OnboardUrl).Host
}
$target = "$User@$Device"
Write-Host "Deploying the onboard stack to $target"

$publish = Join-Path $repoRoot ".tools\onboard-publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $repoRoot "src\UavOps.Onboard.Detector") -c Release -r linux-arm64 --self-contained -o $publish -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
Copy-Item -Recurse (Join-Path $repoRoot "src\UavOps.Onboard.Detector\native") (Join-Path $publish "native")
Copy-Item (Join-Path $repoRoot "scripts\onboard\setup-onboard.sh") $publish

ssh -o BatchMode=yes $target "systemctl --user stop uavops-onboard 2>/dev/null; mkdir -p ~/uavops-onboard/app ~/uavops-onboard/models"
if ($LASTEXITCODE -ne 0) { throw "Can't reach $target over SSH with a key (run ssh-copy-id $target once)." }

# The detector with its layer norms fused (scripts/onboard/fuse_layernorm.py), made here once and
# uploaded: the device has no pip, and the plain export's FP16 engine finds nothing.
$detector = "dfine_x_obj365"
ssh -o BatchMode=yes $target "test -f ~/uavops-onboard/models/$detector.ln.onnx"
if ($LASTEXITCODE -ne 0) {
    $cache = Join-Path $repoRoot ".tools\onboard-models"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $onnx = Join-Path $cache "$detector.onnx"
    if (-not (Test-Path $onnx)) {
        Invoke-WebRequest "https://huggingface.co/onnx-community/$detector-ONNX/resolve/main/onnx/model.onnx" -OutFile $onnx
    }
    python -m pip install -q onnx numpy
    python (Join-Path $repoRoot "scripts\onboard\fuse_layernorm.py") $onnx (Join-Path $cache "$detector.ln.onnx")
    if ($LASTEXITCODE -ne 0) { throw "Fusing the detector's layer norms failed." }
    scp -o BatchMode=yes (Join-Path $cache "$detector.ln.onnx") "${target}:uavops-onboard/models/"
}
# tar over ssh: one stream instead of scp's per-file round trips (hundreds of files).
tar -C $publish -czf - . | ssh -o BatchMode=yes $target "cd ~/uavops-onboard/app && tar -xzf - && sed -i 's/\r$//' setup-onboard.sh native/build.sh && chmod +x UavOps.Onboard.Detector"
if ($LASTEXITCODE -ne 0) { throw "Copying the app failed." }
ssh -o BatchMode=yes $target "bash ~/uavops-onboard/app/setup-onboard.sh"
if ($LASTEXITCODE -ne 0) { throw "Setup on $Device failed." }

# Checked on the device itself: in SignalR link mode nothing on the ground needs to reach port 5280
# (the onboard computer connects out to the ground agent), so it may well be closed from here.
$health = "http://localhost:5280/healthz"
for ($i = 0; $i -lt 30; $i++) {
    $json = ssh -o BatchMode=yes $target "curl -s -m 3 $health"
    if ($LASTEXITCODE -eq 0 -and $json) { $h = $json | ConvertFrom-Json; break }
    Start-Sleep 2
}
if (-not $h) { throw "The onboard service didn't answer at $health on $Device (see: ssh $target journalctl --user -u uavops-onboard)." }
Write-Host "Onboard service up at ${Device}: detector $($h.detector), verifier reachable: $($h.modelReachable)"
