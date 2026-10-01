#!/usr/bin/env bash
# Runs ON the Jetson (scripts/deploy-onboard.ps1 copies it there and runs it). Idempotent: every
# step is skipped when its output is already there, so a redeploy only replaces the app.
#
#   ~/uavops-onboard/app      the published onboard service (UavOps.Onboard.Detector) + libuavtrt.so
#   ~/uavops-onboard/models   the detector (ONNX -> TensorRT engine) and the verifier VLM (GGUF)
#   ~/llama.cpp               llama-server, built with CUDA
#
# Services are systemd *user* units (no sudo): uavops-vlm (llama-server on 127.0.0.1:8080) and
# uavops-onboard (the service on 0.0.0.0:5280). For them to start at boot without a login, run
# once: sudo loginctl enable-linger $USER
set -euo pipefail

ROOT="$HOME/uavops-onboard"
APP="$ROOT/app"
MODELS="$ROOT/models"
DETECTOR="${DETECTOR:-dfine_x_obj365}"
DETECTOR_SIZE="${DETECTOR_SIZE:-640}"
VLM_REPO="${VLM_REPO:-Qwen/Qwen3-VL-2B-Instruct-GGUF}"
VLM_MODEL="${VLM_MODEL:-Qwen3VL-2B-Instruct-Q4_K_M.gguf}"
VLM_MMPROJ="${VLM_MMPROJ:-mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf}"
export PATH="/usr/local/cuda/bin:$PATH"
mkdir -p "$MODELS"

echo "== detector: $DETECTOR"
cd "$MODELS"
# $DETECTOR.ln.onnx is the model with its layer norms fused (fuse_layernorm.py, run on the dev
# machine by deploy-onboard.ps1): built from the plain export, the FP16 engine is numerically
# broken (the layer-norm variance overflows - measured: no vehicles found at all).
if [ ! -f "$DETECTOR.ln.onnx" ]; then
  echo "   $MODELS/$DETECTOR.ln.onnx is missing (deploy-onboard.ps1 uploads it)"; exit 1
fi
if [ ! -f "$DETECTOR.config.json" ]; then
  curl -fsSL -o "$DETECTOR.config.json" "https://huggingface.co/onnx-community/${DETECTOR}-ONNX/resolve/main/config.json"
fi
if [ ! -f "$DETECTOR.ln.fp16.engine" ]; then
  echo "   building the TensorRT engine (FP16) - 20-30 min on the Orin Nano, once"
  /usr/src/tensorrt/bin/trtexec --onnx="$DETECTOR.ln.onnx" --saveEngine="$DETECTOR.ln.fp16.engine" --fp16 \
    --shapes=pixel_values:1x3x${DETECTOR_SIZE}x${DETECTOR_SIZE} --memPoolSize=workspace:2048 > "$DETECTOR.trtexec.log" 2>&1
fi

echo "== native TensorRT shim"
bash "$APP/native/build.sh"
cp "$APP/native/libuavtrt.so" "$APP/"

echo "== verifier VLM: $VLM_REPO"
if [ ! -x "$HOME/llama.cpp/build/bin/llama-server" ]; then
  [ -d "$HOME/llama.cpp" ] || git clone --depth 1 https://github.com/ggml-org/llama.cpp "$HOME/llama.cpp"
  cd "$HOME/llama.cpp"
  cmake -B build -DGGML_CUDA=ON -DCMAKE_CUDA_ARCHITECTURES=87 -DLLAMA_CURL=OFF -DCMAKE_BUILD_TYPE=Release > /dev/null
  cmake --build build --target llama-server -j 6
fi
cd "$MODELS"
for f in "$VLM_MODEL" "$VLM_MMPROJ"; do
  [ -f "$f" ] || curl -fsSL -o "$f" "https://huggingface.co/$VLM_REPO/resolve/main/$f"
done

echo "== services"
UNITS="$HOME/.config/systemd/user"
mkdir -p "$UNITS"
cat > "$UNITS/uavops-vlm.service" <<EOF
[Unit]
Description=UavOps onboard verifier VLM (llama-server)

[Service]
ExecStart=$HOME/llama.cpp/build/bin/llama-server -m $MODELS/$VLM_MODEL --mmproj $MODELS/$VLM_MMPROJ --alias verifier --host 127.0.0.1 --port 8080 -ngl 99 -c 4096 --jinja
Environment=GGML_CUDA_ENABLE_UNIFIED_MEMORY=1
Restart=on-failure
StandardOutput=append:$ROOT/vlm.log
StandardError=append:$ROOT/vlm.log

[Install]
WantedBy=default.target
EOF
cat > "$UNITS/uavops-onboard.service" <<EOF
[Unit]
Description=UavOps onboard service (detector, tracker, executive)
After=uavops-vlm.service

[Service]
WorkingDirectory=$APP
ExecStart=$APP/UavOps.Onboard.Detector
Environment=ASPNETCORE_ENVIRONMENT=Jetson
Environment=LD_LIBRARY_PATH=$APP
Restart=on-failure
StandardOutput=append:$ROOT/onboard.log
StandardError=append:$ROOT/onboard.log

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
systemctl --user enable uavops-vlm uavops-onboard > /dev/null
systemctl --user restart uavops-vlm uavops-onboard
echo "done: http://$(hostname -I | awk '{print $1}'):5280/healthz"
