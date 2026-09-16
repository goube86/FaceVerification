using FaceVerification.Application;
using OpenCvSharp;

namespace FaceVerification.Infrastructure.Vision;

public sealed class OpenCvImageQualityValidator(VerificationOptions options) : IImageQualityValidator
{
    public ImageQualityResult Validate(ReadOnlyMemory<byte> image)
    {
        if (image.Length == 0 || image.Length > options.MaxFileSizeBytes) return new(false, 0, "invalid_file_size");
        if (!HasSupportedSignature(image.Span)) return new(false, 0, "unsupported_image_format");
        try
        {
            using var mat = Cv2.ImDecode(image.ToArray(), ImreadModes.Color);
            if (mat.Empty()) return new(false, 0, "invalid_image");
            if (mat.Width > options.MaxWidth || mat.Height > options.MaxHeight || (long)mat.Width * mat.Height > options.MaxPixels)
                return new(false, 0, "image_dimensions_exceeded");
            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
            using var laplacian = new Mat();
            Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var sharpnessDeviation);
            var sharpness = Math.Clamp(sharpnessDeviation.Val0 / 40d, 0, 1);
            var brightness = Cv2.Mean(gray).Val0;
            var exposure = 1 - Math.Min(Math.Abs(brightness - 127.5) / 127.5, 1);
            var score = (sharpness * 0.65) + (exposure * 0.35);
            return new(score >= options.MinimumQualityScore, score, score >= options.MinimumQualityScore ? null : "insufficient_image_quality");
        }
        catch (OpenCVException) { return new(false, 0, "invalid_image"); }
    }

    private static bool HasSupportedSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[^2] == 0xFF && bytes[^1] == 0xD9) ||
        (bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) ||
        (bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)));
}

public sealed class OpenCvFaceAligner : IFaceAligner
{
    private static readonly Point2f[] Target =
    [
        new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f), new(70.7299f, 92.2041f)
    ];

    public AlignedFace Align(ReadOnlyMemory<byte> image, DetectedFace face)
    {
        if (face.Landmarks.Count != 5) throw new InvalidDataException("Five landmarks are required for alignment.");
        using var source = Cv2.ImDecode(image.ToArray(), ImreadModes.Color);
        var sourcePoints = face.Landmarks.Select(x => new Point2f(x.X, x.Y)).ToArray();
        using var transform = Cv2.EstimateAffinePartial2D(InputArray.Create(sourcePoints), InputArray.Create(Target));
        if (transform is null || transform.Empty()) throw new InvalidDataException("Face alignment failed.");
        using var aligned = new Mat();
        Cv2.WarpAffine(source, aligned, transform, new Size(112, 112), InterpolationFlags.Linear, BorderTypes.Constant);
        return new(aligned.ToBytes(".bmp"), aligned.Width, aligned.Height);
    }
}
