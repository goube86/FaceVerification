using FaceVerification.Application;
using FaceVerification.Infrastructure.Models;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using System.Globalization;

namespace FaceVerification.Infrastructure.Vision;

public sealed class YuNetFaceDetector(ModelRuntime runtime) : IFaceDetector
{
    public Task<FaceDetectionResult> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var original = Cv2.ImDecode(image.ToArray(), ImreadModes.Color);
        if (original.Empty()) throw new InvalidDataException("The image cannot be decoded.");
        var manifest = runtime.YuNetManifest;
        using var resized = new Mat();
        Cv2.Resize(original, resized, new Size(manifest.InputWidth, manifest.InputHeight));
        var scaleX = original.Width / (float)manifest.InputWidth;
        var scaleY = original.Height / (float)manifest.InputHeight;
        var data = ToNchw(resized, swapRedAndBlue: false, scale: 1f, mean: 0f);
        var dimensions = new long[] { 1, 3, manifest.InputHeight, manifest.InputWidth };
        using var input = OrtValue.CreateTensorValueFromMemory(data, dimensions);
        var name = runtime.YuNetSession.InputNames[0];
        var inputs = new Dictionary<string, OrtValue> { [name] = input };
        using var outputs = runtime.YuNetSession.Run(new RunOptions(), inputs, runtime.YuNetSession.OutputNames);
        var byName = runtime.YuNetSession.OutputNames.Zip(outputs, (n, v) => (n, v)).ToDictionary(x => x.n, x => x.v, StringComparer.OrdinalIgnoreCase);
        var faces = Decode(byName, manifest.InputWidth, manifest.InputHeight, scaleX, scaleY);
        return Task.FromResult(new FaceDetectionResult(faces, original.Width, original.Height));
    }

    private static List<DetectedFace> Decode(Dictionary<string, OrtValue> outputs, int width, int height, float scaleX, float scaleY)
    {
        var candidates = new List<DetectedFace>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cls = Find(outputs, "cls", stride);
            var obj = Find(outputs, "obj", stride);
            var bbox = Find(outputs, "bbox", stride);
            var kps = Find(outputs, "kps", stride);
            if (cls is null || obj is null || bbox is null || kps is null) continue;
            var clsData = cls.GetTensorDataAsSpan<float>();
            var objData = obj.GetTensorDataAsSpan<float>();
            var boxData = bbox.GetTensorDataAsSpan<float>();
            var landmarkData = kps.GetTensorDataAsSpan<float>();
            var rows = height / stride;
            var columns = width / stride;
            for (var index = 0; index < rows * columns && index < clsData.Length && index < objData.Length; index++)
            {
                var score = MathF.Sqrt(Math.Clamp(clsData[index], 0, 1) * Math.Clamp(objData[index], 0, 1));
                if (score < 0.75f) continue;
                var row = index / columns;
                var column = index % columns;
                var boxOffset = index * 4;
                var centerX = (column + boxData[boxOffset]) * stride;
                var centerY = (row + boxData[boxOffset + 1]) * stride;
                var boxWidth = MathF.Exp(Math.Clamp(boxData[boxOffset + 2], -10, 10)) * stride;
                var boxHeight = MathF.Exp(Math.Clamp(boxData[boxOffset + 3], -10, 10)) * stride;
                var landmarks = new FaceLandmark[5];
                for (var i = 0; i < 5; i++)
                {
                    var offset = index * 10 + i * 2;
                    landmarks[i] = new((column + landmarkData[offset]) * stride * scaleX, (row + landmarkData[offset + 1]) * stride * scaleY);
                }
                candidates.Add(new((centerX - boxWidth / 2) * scaleX, (centerY - boxHeight / 2) * scaleY,
                    boxWidth * scaleX, boxHeight * scaleY, score, landmarks));
            }
        }
        return NonMaximumSuppression(candidates, 0.3f);
    }

    private static OrtValue? Find(Dictionary<string, OrtValue> values, string type, int stride) =>
        values.FirstOrDefault(x => x.Key.Contains(type, StringComparison.OrdinalIgnoreCase) && x.Key.EndsWith(stride.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).Value;

    private static List<DetectedFace> NonMaximumSuppression(List<DetectedFace> faces, float threshold)
    {
        var result = new List<DetectedFace>();
        foreach (var face in faces.OrderByDescending(x => x.Confidence))
        {
            if (result.All(existing => IntersectionOverUnion(face, existing) < threshold)) result.Add(face);
        }
        return result;
    }

    private static float IntersectionOverUnion(DetectedFace a, DetectedFace b)
    {
        var x1 = Math.Max(a.X, b.X); var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width); var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        return intersection / ((a.Width * a.Height) + (b.Width * b.Height) - intersection + float.Epsilon);
    }

    internal static float[] ToNchw(Mat image, bool swapRedAndBlue, float scale, float mean)
    {
        var rows = image.Rows;
        var columns = image.Cols;
        var result = new float[3 * rows * columns];
        var plane = rows * columns;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var pixel = image.At<Vec3b>(y, x);
            for (var channel = 0; channel < 3; channel++)
            {
                var sourceChannel = swapRedAndBlue ? 2 - channel : channel;
                result[channel * plane + y * columns + x] = (pixel[sourceChannel] - mean) * scale;
            }
        }
        return result;
    }
}

public sealed class SFaceEmbeddingProvider(ModelRuntime runtime) : IFaceEmbeddingProvider
{
    public string ModelName => runtime.SFaceManifest.Name;
    public string ModelVersion => runtime.SFaceManifest.Version;

    public Task<FaceEmbedding> GenerateAsync(AlignedFace face, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Cv2.ImDecode(face.BgrPixels.ToArray(), ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("The aligned face cannot be decoded.");
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(runtime.SFaceManifest.InputWidth, runtime.SFaceManifest.InputHeight));
        // This deliberately mirrors OpenCV FaceRecognizerSF::feature: blobFromImage with
        // scale=1, mean=0 and swapRB=true. The SFace ONNX graph performs its own scaling.
        var data = YuNetFaceDetector.ToNchw(resized, swapRedAndBlue: true, scale: 1f, mean: 0f);
        using var input = OrtValue.CreateTensorValueFromMemory(data, new long[] { 1, 3, resized.Rows, resized.Cols });
        var inputs = new Dictionary<string, OrtValue> { [runtime.SFaceSession.InputNames[0]] = input };
        using var outputs = runtime.SFaceSession.Run(new RunOptions(), inputs, runtime.SFaceSession.OutputNames);
        var embedding = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        if (embedding.Length != runtime.SFaceManifest.EmbeddingSize)
            throw new InvalidDataException($"SFace produced {embedding.Length} values; {runtime.SFaceManifest.EmbeddingSize} were expected.");
        if (embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("SFace produced a non-finite embedding.");
        var squaredNorm = embedding.Sum(value => (double)value * value);
        var norm = Math.Sqrt(squaredNorm);
        if (!double.IsFinite(norm) || norm <= double.Epsilon)
            throw new InvalidDataException("SFace produced an empty embedding.");
        for (var i = 0; i < embedding.Length; i++) embedding[i] = (float)(embedding[i] / norm);
        return Task.FromResult(new FaceEmbedding(embedding));
    }
}

public sealed class CosineFaceComparator : IFaceComparator
{
    public FaceComparisonResult Compare(FaceEmbedding left, FaceEmbedding right)
    {
        if (left.Values.Length == 0 || left.Values.Length != right.Values.Length)
            throw new ArgumentException("Embeddings must have the same non-zero length.");
        Validate(left.Values, nameof(left));
        Validate(right.Values, nameof(right));
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Values.Length; i++)
        {
            dot += left.Values[i] * right.Values[i];
            leftNorm += left.Values[i] * left.Values[i];
            rightNorm += right.Values[i] * right.Values[i];
        }
        if (!double.IsFinite(leftNorm) || !double.IsFinite(rightNorm) || leftNorm <= 0 || rightNorm <= 0)
            throw new ArgumentException("Embeddings must have finite, non-zero norms.");
        var leftScale = Math.Sqrt(leftNorm);
        var rightScale = Math.Sqrt(rightNorm);
        var cosine = Math.Clamp(dot / (leftScale * rightScale), -1, 1);
        double squaredL2 = 0;
        for (var i = 0; i < left.Values.Length; i++)
        {
            var difference = (left.Values[i] / leftScale) - (right.Values[i] / rightScale);
            squaredL2 += difference * difference;
        }
        return new(cosine, Math.Sqrt(squaredL2));
    }

    private static void Validate(float[] values, string parameterName)
    {
        if (values.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Embeddings must contain only finite values.", parameterName);
        var first = values[0];
        if (values.All(value => value == first))
            throw new ArgumentException("Constant embeddings are invalid.", parameterName);
    }
}
