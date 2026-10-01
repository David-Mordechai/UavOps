using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector.Perception;

/// <summary>One object the detector found in a frame: its class (the model's own label, e.g.
/// "Pickup Truck"), score 0-1, and box normalized 0-1000 like <see cref="BoundingBox"/>.</summary>
public sealed record DetectedObject(string Class, double Score, BoundingBox Box);

/// <summary>
/// The fast, every-frame detector: finds every object of the classes it knows in a frame, in tens
/// of milliseconds. It never decides what the target is - that's the executive's job, from class,
/// colour and (rarely) the vision model.
/// </summary>
public interface IObjectDetector
{
    string Name { get; }

    /// <summary>The frame width the detector needs: frames are decoded no smaller than this (the
    /// engine's square size, or more for <see cref="TiledDetector"/>).</summary>
    int InputSize { get; }

    /// <param name="image">The frame, decoded.</param>
    IReadOnlyList<DetectedObject> Detect(SKBitmap image, double minScore);
}

/// <summary>A detector that can run on part of a frame, with boxes normalized to that part (what
/// <see cref="TiledDetector"/> needs).</summary>
public interface IRegionDetector : IObjectDetector
{
    /// <param name="image">The frame, decoded as RGBA 8888.</param>
    /// <param name="region">The part to run on, in the image's pixels.</param>
    IReadOnlyList<DetectedObject> Detect(SKBitmap image, SKRectI region, double minScore);
}

/// <summary>Per-stage timing of the last call (ms), for the <c>/detect</c> diagnostic.</summary>
public interface IDetectorTiming
{
    (double Prepare, double Infer, double Decode) LastTiming { get; }
}

/// <summary>
/// A DETR-family detector (D-FINE, RT-DETR, RF-DETR as exported to ONNX by HuggingFace) running as
/// a TensorRT engine through <c>libuavtrt.so</c> (native/uavtrt.cpp). Input: one RGB image stretched
/// to the engine's square size (as the HuggingFace processor does), scaled to 0-1, CHW - done on
/// the GPU (native/preprocess.cu). Outputs: <c>logits</c> [1, queries, classes]
/// (sigmoid scores) and <c>pred_boxes</c> [1, queries, 4] (cx, cy, w, h, normalized).
/// Labels come from the model's HuggingFace <c>config.json</c> (<c>id2label</c>).
/// </summary>
public sealed class TensorRtDetector : IRegionDetector, IDetectorTiming, IDisposable
{
    private readonly object _lock = new();
    private readonly IntPtr _handle;
    private readonly int _size;
    private readonly float[] _logits;
    private readonly float[] _boxes;
    private readonly int _queries;
    private readonly int _classes;
    private readonly bool _logitsFirst;
    private readonly string[] _labels;

    public TensorRtDetector(string enginePath, string labelsPath)
    {
        var error = new StringBuilder(1024);
        _handle = Native.uavtrt_create(enginePath, error, error.Capacity);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"TensorRT engine '{enginePath}' didn't load: {error}");

        long[]? inputDims = null, logitsDims = null, boxDims = null;
        var outputOrder = new List<string>();
        for (var i = 0; i < Native.uavtrt_tensor_count(_handle); i++)
        {
            var name = new StringBuilder(256);
            var dims = new long[8];
            var n = Native.uavtrt_tensor_info(_handle, i, name, name.Capacity, out var isInput, dims);
            var shape = dims[..n];
            if (isInput != 0)
                inputDims = shape;
            else
            {
                outputOrder.Add(name.ToString());
                // [1, queries, 4] is the boxes; the other [1, queries, classes] the logits.
                if (shape.Length == 3 && shape[2] == 4)
                    boxDims = shape;
                else
                    logitsDims = shape;
            }
        }
        if (inputDims is not { Length: 4 } || logitsDims is not { Length: 3 } || boxDims is null || outputOrder.Count != 2)
            throw new InvalidOperationException($"'{enginePath}' isn't a DETR-style detector (one NCHW input, logits + pred_boxes outputs).");

        _size = (int)inputDims[3];
        _queries = (int)logitsDims[1];
        _classes = (int)logitsDims[2];
        _logitsFirst = outputOrder.Count == 2 && !IsBoxes(outputOrder[0]);
        _logits = new float[_queries * _classes];
        _boxes = new float[_queries * 4];
        _labels = LoadLabels(labelsPath, _classes);
        Name = $"{Path.GetFileNameWithoutExtension(enginePath)} ({_size}px, {_classes} classes)";

        bool IsBoxes(string name) => name.Contains("box", StringComparison.OrdinalIgnoreCase);
    }

    public string Name { get; }
    public int InputSize => _size;

    /// <summary>The last call's stages (ms): preparing the input, the engine, reading the output.</summary>
    public (double Prepare, double Infer, double Decode) LastTiming { get; private set; }

    public IReadOnlyList<DetectedObject> Detect(SKBitmap image, double minScore) =>
        Detect(image, new SKRectI(0, 0, image.Width, image.Height), minScore);

    /// <summary>A part of the frame, with no copy: the GPU reads it in place by its first pixel and
    /// the frame's row stride, and resizes it to the engine's square there.</summary>
    public IReadOnlyList<DetectedObject> Detect(SKBitmap image, SKRectI region, double minScore)
    {
        lock (_lock)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The frame goes to the GPU as RGBA bytes; resizing and scaling happen there
            // (native/preprocess.cu) - on the CPU that took ~16 ms a frame on the Orin Nano.
            using var rgba = image.ColorType == SKColorType.Rgba8888 ? null : image.Copy(SKColorType.Rgba8888);
            var frame = rgba ?? image;
            region.Intersect(new SKRectI(0, 0, frame.Width, frame.Height));
            if (region.Width <= 0 || region.Height <= 0)
                return [];
            var first = frame.GetPixels() + region.Top * frame.RowBytes + region.Left * 4;
            var prepare = watch.Elapsed.TotalMilliseconds;
            var outputs = new IntPtr[2];
            var logits = GCHandle.Alloc(_logits, GCHandleType.Pinned);
            var boxes = GCHandle.Alloc(_boxes, GCHandleType.Pinned);
            try
            {
                outputs[_logitsFirst ? 0 : 1] = logits.AddrOfPinnedObject();
                outputs[_logitsFirst ? 1 : 0] = boxes.AddrOfPinnedObject();
                watch.Restart();
                var status = Native.uavtrt_infer_rgba(_handle, first, region.Width, region.Height, frame.RowBytes, outputs);
                var infer = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                if (status != 0)
                    throw new InvalidOperationException($"TensorRT inference failed ({status}).");
                var found = Decode(minScore);
                LastTiming = (prepare, infer, watch.Elapsed.TotalMilliseconds);
                return found;
            }
            finally
            {
                logits.Free();
                boxes.Free();
            }
        }
    }

    private List<DetectedObject> Decode(double minScore)
    {
        var found = new List<DetectedObject>();
        for (var q = 0; q < _queries; q++)
        {
            var best = 0;
            var bestLogit = float.NegativeInfinity;
            var row = q * _classes;
            for (var c = 0; c < _classes; c++)
                if (_logits[row + c] > bestLogit)
                    (bestLogit, best) = (_logits[row + c], c);
            var score = 1 / (1 + Math.Exp(-bestLogit));
            if (score < minScore)
                continue;
            var cx = _boxes[q * 4];
            var cy = _boxes[q * 4 + 1];
            var w = _boxes[q * 4 + 2];
            var h = _boxes[q * 4 + 3];
            found.Add(new DetectedObject(_labels[best], score, new BoundingBox(
                Math.Clamp((cx - w / 2) * 1000, 0, 1000), Math.Clamp((cy - h / 2) * 1000, 0, 1000),
                Math.Clamp((cx + w / 2) * 1000, 0, 1000), Math.Clamp((cy + h / 2) * 1000, 0, 1000))));
        }
        return found;
    }

    /// <summary><c>id2label</c> from a HuggingFace config.json; "class N" for any id it lacks.</summary>
    private static string[] LoadLabels(string path, int classes)
    {
        var labels = Enumerable.Range(0, classes).Select(i => $"class {i}").ToArray();
        if (!File.Exists(path))
            return labels;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("id2label", out var map))
            foreach (var entry in map.EnumerateObject())
                if (int.TryParse(entry.Name, out var id) && id >= 0 && id < classes)
                    labels[id] = entry.Value.GetString() ?? labels[id];
        return labels;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            Native.uavtrt_destroy(_handle);
    }

    private static class Native
    {
        private const string Lib = "uavtrt";

        [DllImport(Lib, CharSet = CharSet.Ansi)]
        public static extern IntPtr uavtrt_create(string enginePath, StringBuilder error, int errorSize);

        [DllImport(Lib)]
        public static extern int uavtrt_tensor_count(IntPtr handle);

        [DllImport(Lib, CharSet = CharSet.Ansi)]
        public static extern int uavtrt_tensor_info(IntPtr handle, int index, StringBuilder name, int nameSize, out int isInput, long[] dims);

        [DllImport(Lib)]
        public static extern int uavtrt_infer(IntPtr handle, IntPtr input, IntPtr[] outputs);

        [DllImport(Lib)]
        public static extern int uavtrt_infer_rgba(IntPtr handle, IntPtr rgba, int width, int height, int stride, IntPtr[] outputs);

        [DllImport(Lib)]
        public static extern void uavtrt_destroy(IntPtr handle);
    }
}
