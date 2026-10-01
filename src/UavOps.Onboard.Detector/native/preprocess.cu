// GPU preprocessing for uavtrt: an RGBA8 frame (any size) -> the engine's float32 CHW input
// (square, bilinear, 0-1). On the CPU in C# this took ~16 ms per 640x640 frame on the Orin Nano;
// here it's well under a millisecond, and a quarter of the bytes cross to the GPU.

#include <cuda_runtime.h>
#include <cstdint>

__global__ void rgba_to_chw(const uint8_t* src, int srcW, int srcH, int srcStride, float* dst, int size)
{
    const int x = blockIdx.x * blockDim.x + threadIdx.x;
    const int y = blockIdx.y * blockDim.y + threadIdx.y;
    if (x >= size || y >= size)
        return;
    // Pixel centres: sample the source at the matching point, bilinear.
    const float fx = fmaxf((x + 0.5f) * srcW / size - 0.5f, 0.f);
    const float fy = fmaxf((y + 0.5f) * srcH / size - 0.5f, 0.f);
    const int x0 = min((int)fx, srcW - 1), y0 = min((int)fy, srcH - 1);
    const int x1 = min(x0 + 1, srcW - 1), y1 = min(y0 + 1, srcH - 1);
    const float ax = fx - x0, ay = fy - y0;
    const uint8_t* p00 = src + y0 * srcStride + x0 * 4;
    const uint8_t* p01 = src + y0 * srcStride + x1 * 4;
    const uint8_t* p10 = src + y1 * srcStride + x0 * 4;
    const uint8_t* p11 = src + y1 * srcStride + x1 * 4;
    const int plane = size * size;
    for (int c = 0; c < 3; c++)
    {
        const float top = p00[c] + (p01[c] - p00[c]) * ax;
        const float bottom = p10[c] + (p11[c] - p10[c]) * ax;
        dst[c * plane + y * size + x] = (top + (bottom - top) * ay) * (1.f / 255.f);
    }
}

extern "C" int uavtrt_preprocess_launch(const uint8_t* deviceSrc, int srcW, int srcH, int srcStride, float* deviceDst, int size, cudaStream_t stream)
{
    const dim3 block(16, 16);
    const dim3 grid((size + block.x - 1) / block.x, (size + block.y - 1) / block.y);
    rgba_to_chw<<<grid, block, 0, stream>>>(deviceSrc, srcW, srcH, srcStride, deviceDst, size);
    return cudaGetLastError() == cudaSuccess ? 0 : 1;
}
