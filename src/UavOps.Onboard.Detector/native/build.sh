#!/usr/bin/env bash
# Builds libuavtrt.so on the Jetson (JetPack 6: TensorRT 10, CUDA 12). Run from this folder, or
# let scripts/deploy-onboard.ps1 do it.
set -euo pipefail
cd "$(dirname "$0")"
CUDA=${CUDA_HOME:-/usr/local/cuda}
"$CUDA/bin/nvcc" -O2 -std=c++17 -arch=sm_87 -Xcompiler -fPIC -c preprocess.cu -o preprocess.o
g++ -O2 -std=c++17 -shared -fPIC uavtrt.cpp preprocess.o -o libuavtrt.so \
    -I/usr/include/aarch64-linux-gnu -I"$CUDA/include" \
    -L/usr/lib/aarch64-linux-gnu -L"$CUDA/lib64" -lnvinfer -lcudart
echo "built $(pwd)/libuavtrt.so"
