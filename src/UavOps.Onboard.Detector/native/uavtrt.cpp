// uavtrt: a thin C API over one TensorRT engine, for the onboard service to P/Invoke
// (Perception/TensorRtDetector.cs). ONNX Runtime has no GPU build for .NET on the Jetson, and
// building one there takes hours; TensorRT itself ships with JetPack, so this is the short path
// to the GPU. Built on the device by build.sh; the engine by trtexec (see the README).
//
// One engine, one input (float32 NCHW), any number of float32 outputs. Not thread safe: the
// caller serializes calls on a handle.

#include <NvInfer.h>
#include <cuda_runtime_api.h>

#include <cstdio>
#include <cstring>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

namespace {

class Logger final : public nvinfer1::ILogger {
public:
    void log(Severity severity, const char* msg) noexcept override {
        if (severity <= Severity::kWARNING)
            std::fprintf(stderr, "[uavtrt] %s\n", msg);
    }
};

Logger gLogger;

struct Tensor {
    std::string name;
    nvinfer1::Dims dims{};
    bool input = false;
    size_t count = 0;
    void* device = nullptr;
};

struct Engine {
    std::unique_ptr<nvinfer1::IRuntime> runtime;
    std::unique_ptr<nvinfer1::ICudaEngine> engine;
    std::unique_ptr<nvinfer1::IExecutionContext> context;
    cudaStream_t stream = nullptr;
    std::vector<Tensor> tensors;
    uint8_t* frame = nullptr;     // RGBA staging on the GPU (uavtrt_infer_rgba)
    size_t frameBytes = 0;
};

void copyError(char* buffer, int size, const std::string& message) {
    if (buffer && size > 0) {
        std::strncpy(buffer, message.c_str(), size - 1);
        buffer[size - 1] = '\0';
    }
}

size_t Count(const nvinfer1::Dims& dims) {
    size_t n = 1;
    for (int i = 0; i < dims.nbDims; i++)
        n *= dims.d[i] > 0 ? static_cast<size_t>(dims.d[i]) : 1;
    return n;
}

} // namespace

extern "C" int uavtrt_preprocess_launch(const uint8_t* deviceSrc, int srcW, int srcH, int srcStride, float* deviceDst, int size, cudaStream_t stream);

extern "C" {

// Loads a serialized engine. Returns null on failure, with the reason in error.
void* uavtrt_create(const char* enginePath, char* error, int errorSize) {
    std::ifstream file(enginePath, std::ios::binary);
    if (!file) {
        copyError(error, errorSize, std::string("can't open ") + enginePath);
        return nullptr;
    }
    std::vector<char> blob((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());

    auto e = std::make_unique<Engine>();
    e->runtime.reset(nvinfer1::createInferRuntime(gLogger));
    if (!e->runtime) {
        copyError(error, errorSize, "createInferRuntime failed");
        return nullptr;
    }
    e->engine.reset(e->runtime->deserializeCudaEngine(blob.data(), blob.size()));
    if (!e->engine) {
        copyError(error, errorSize, "deserializeCudaEngine failed (engine built by another TensorRT version?)");
        return nullptr;
    }
    e->context.reset(e->engine->createExecutionContext());
    if (!e->context || cudaStreamCreate(&e->stream) != cudaSuccess) {
        copyError(error, errorSize, "createExecutionContext/cudaStreamCreate failed");
        return nullptr;
    }

    for (int i = 0; i < e->engine->getNbIOTensors(); i++) {
        Tensor t;
        t.name = e->engine->getIOTensorName(i);
        t.input = e->engine->getTensorIOMode(t.name.c_str()) == nvinfer1::TensorIOMode::kINPUT;
        if (e->engine->getTensorDataType(t.name.c_str()) != nvinfer1::DataType::kFLOAT) {
            copyError(error, errorSize, "tensor " + t.name + " isn't float32");
            return nullptr;
        }
        t.dims = e->context->getTensorShape(t.name.c_str());
        t.count = Count(t.dims);
        if (cudaMalloc(&t.device, t.count * sizeof(float)) != cudaSuccess) {
            copyError(error, errorSize, "cudaMalloc failed for " + t.name);
            return nullptr;
        }
        e->context->setTensorAddress(t.name.c_str(), t.device);
        e->tensors.push_back(t);
    }
    return e.release();
}

int uavtrt_tensor_count(void* handle) {
    return static_cast<int>(static_cast<Engine*>(handle)->tensors.size());
}

// A tensor's name, whether it's the input, and its shape (up to 8 dims). Returns the number of dims.
int uavtrt_tensor_info(void* handle, int index, char* name, int nameSize, int* isInput, long long* dims) {
    auto& t = static_cast<Engine*>(handle)->tensors.at(index);
    copyError(name, nameSize, t.name);
    *isInput = t.input ? 1 : 0;
    for (int i = 0; i < t.dims.nbDims && i < 8; i++)
        dims[i] = t.dims.d[i];
    return t.dims.nbDims;
}

// Runs the engine: input is the input tensor's elements, outputs[i] receives the i-th output
// tensor (in tensor order, inputs skipped). Returns 0 on success.
int uavtrt_infer(void* handle, const float* input, float** outputs) {
    auto* e = static_cast<Engine*>(handle);
    int out = 0;
    for (auto& t : e->tensors)
        if (t.input && cudaMemcpyAsync(t.device, input, t.count * sizeof(float), cudaMemcpyHostToDevice, e->stream) != cudaSuccess)
            return 1;
    if (!e->context->enqueueV3(e->stream))
        return 2;
    for (auto& t : e->tensors)
        if (!t.input && cudaMemcpyAsync(outputs[out++], t.device, t.count * sizeof(float), cudaMemcpyDeviceToHost, e->stream) != cudaSuccess)
            return 3;
    return cudaStreamSynchronize(e->stream) == cudaSuccess ? 0 : 4;
}

// Like uavtrt_infer, from an RGBA8 frame of any size, or a region of one (rgba at its first pixel,
// stride the frame's): uploaded as is, resized to the (square) input and scaled to 0-1 on the GPU
// (preprocess.cu). Returns 0 on success.
int uavtrt_infer_rgba(void* handle, const uint8_t* rgba, int width, int height, int stride, float** outputs) {
    auto* e = static_cast<Engine*>(handle);
    Tensor* in = nullptr;
    for (auto& t : e->tensors)
        if (t.input)
            in = &t;
    if (!in || in->dims.nbDims != 4)
        return 5;
    // Exactly the rows' pixels: rgba may point into a larger frame (a tile, read in place with the
    // frame's stride), where a whole last stride would run past the end of the frame.
    if (width <= 0 || height <= 0 || stride < width * 4)
        return 8;
    const size_t bytes = static_cast<size_t>(stride) * (height - 1) + static_cast<size_t>(width) * 4;
    if (bytes > e->frameBytes) {
        cudaFree(e->frame);
        if (cudaMalloc(reinterpret_cast<void**>(&e->frame), bytes) != cudaSuccess)
            return 6;
        e->frameBytes = bytes;
    }
    if (cudaMemcpyAsync(e->frame, rgba, bytes, cudaMemcpyHostToDevice, e->stream) != cudaSuccess)
        return 1;
    if (uavtrt_preprocess_launch(e->frame, width, height, stride, static_cast<float*>(in->device), static_cast<int>(in->dims.d[3]), e->stream) != 0)
        return 7;
    if (!e->context->enqueueV3(e->stream))
        return 2;
    int out = 0;
    for (auto& t : e->tensors)
        if (!t.input && cudaMemcpyAsync(outputs[out++], t.device, t.count * sizeof(float), cudaMemcpyDeviceToHost, e->stream) != cudaSuccess)
            return 3;
    return cudaStreamSynchronize(e->stream) == cudaSuccess ? 0 : 4;
}

void uavtrt_destroy(void* handle) {
    auto* e = static_cast<Engine*>(handle);
    if (!e)
        return;
    cudaFree(e->frame);
    for (auto& t : e->tensors)
        cudaFree(t.device);
    if (e->stream)
        cudaStreamDestroy(e->stream);
    delete e;
}

} // extern "C"
