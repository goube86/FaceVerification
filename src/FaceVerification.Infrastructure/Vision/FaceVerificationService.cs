using System.Diagnostics;
using FaceVerification.Application;
using FaceVerification.Domain;
using Microsoft.Extensions.Logging;

namespace FaceVerification.Infrastructure.Vision;

public sealed partial class FaceVerificationService(
    IImageQualityValidator qualityValidator, IFaceDetector detector, IFaceAligner aligner,
    IFaceEmbeddingProvider embeddingProvider, IFaceComparator comparator, VerificationOptions options,
    IClock clock, ILogger<FaceVerificationService> logger) : IFaceVerificationService, IDisposable
{
    private readonly SemaphoreSlim concurrency = new(options.MaxConcurrentInferences, options.MaxConcurrentInferences);

    public async Task<FaceVerificationOutcome> VerifyAsync(FaceImage referenceImage, FaceImage capturedImage, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.InferenceTimeoutSeconds));
        await concurrency.WaitAsync(timeout.Token);
        try
        {
            var stage = Stopwatch.StartNew();
            var referenceQuality = qualityValidator.Validate(referenceImage.Bytes);
            if (!referenceQuality.IsValid) return Outcome(MapQuality(referenceQuality, true), referenceQuality.Score, 0, 0, 0, stopwatch);
            var capturedQuality = qualityValidator.Validate(capturedImage.Bytes);
            if (!capturedQuality.IsValid) return Outcome(MapQuality(capturedQuality, false), referenceQuality.Score, capturedQuality.Score, 0, 0, stopwatch);
            var qualityMs = stage.ElapsedMilliseconds;

            stage.Restart();
            var referenceDetection = await detector.DetectAsync(referenceImage.Bytes, timeout.Token);
            var capturedDetection = await detector.DetectAsync(capturedImage.Bytes, timeout.Token);
            var detectionMs = stage.ElapsedMilliseconds;
            LogDetection(logger, "reference", referenceDetection);
            LogDetection(logger, "captured", capturedDetection);
            var faceDecision = ValidateFaces(referenceDetection, capturedDetection);
            if (faceDecision is not null) return Outcome(faceDecision.Value, referenceQuality.Score, capturedQuality.Score, referenceDetection.Faces.Count, capturedDetection.Faces.Count, stopwatch);

            var referenceFace = referenceDetection.Faces[0];
            var capturedFace = capturedDetection.Faces[0];
            if (Math.Min(referenceFace.Width, referenceFace.Height) < options.MinimumFacePixels || Math.Min(capturedFace.Width, capturedFace.Height) < options.MinimumFacePixels)
                return Outcome(VerificationDecision.FaceTooSmall, referenceQuality.Score, capturedQuality.Score, 1, 1, stopwatch);
            if (!IsCentered(referenceFace, referenceDetection) || !IsCentered(capturedFace, capturedDetection))
                return Outcome(VerificationDecision.InsufficientImageQuality, referenceQuality.Score, capturedQuality.Score, 1, 1, stopwatch);

            stage.Restart();
            var referenceAligned = aligner.Align(referenceImage.Bytes, referenceFace);
            var alignmentMs = stage.ElapsedMilliseconds;
            stage.Restart();
            var left = await GenerateIndependentEmbeddingAsync(referenceAligned, timeout.Token);
            var embeddingMs = stage.ElapsedMilliseconds;

            stage.Restart();
            var capturedAligned = aligner.Align(capturedImage.Bytes, capturedFace);
            alignmentMs += stage.ElapsedMilliseconds;
            stage.Restart();
            var right = await GenerateIndependentEmbeddingAsync(capturedAligned, timeout.Token);
            embeddingMs += stage.ElapsedMilliseconds;

            stage.Restart();
            var comparison = comparator.Compare(left, right);
            var comparisonMs = stage.ElapsedMilliseconds;
            var similarity = comparison.CosineSimilarity;
            var decision = similarity >= options.MatchThreshold ? VerificationDecision.Match
                : similarity <= options.NonMatchThreshold ? VerificationDecision.NotMatch
                : VerificationDecision.Inconclusive;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var leftNorm = Norm(left);
                var rightNorm = Norm(right);
                LogComparison(logger, similarity, similarity, comparison.L2Distance, leftNorm, rightNorm, left.Values.Length,
                    right.Values.Length, qualityMs, detectionMs, alignmentMs, embeddingMs, comparisonMs, stopwatch.ElapsedMilliseconds);
            }
            return Outcome(decision, referenceQuality.Score, capturedQuality.Score, 1, 1, stopwatch, similarity);
        }
        finally { concurrency.Release(); }
    }

    private async Task<FaceEmbedding> GenerateIndependentEmbeddingAsync(AlignedFace face, CancellationToken cancellationToken)
    {
        var generated = await embeddingProvider.GenerateAsync(face, cancellationToken);
        if (generated.Values is null) throw new InvalidDataException("The embedding provider returned no values.");
        return new FaceEmbedding((float[])generated.Values.Clone());
    }

    private static double Norm(FaceEmbedding embedding) =>
        Math.Sqrt(embedding.Values.Sum(value => (double)value * value));

    private static void LogDetection(ILogger logger, string role, FaceDetectionResult detection)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;
        if (detection.Faces.Count == 0)
        {
            LogNoFaces(logger, role, detection.ImageWidth, detection.ImageHeight);
            return;
        }
        foreach (var face in detection.Faces)
            LogDetectedFace(logger,
                role, detection.Faces.Count, face.Confidence, face.X, face.Y, face.Width, face.Height);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug,
        Message = "Face detection role={Role} count=0 imageWidth={ImageWidth} imageHeight={ImageHeight}")]
    private static partial void LogNoFaces(ILogger logger, string role, int imageWidth, int imageHeight);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug,
        Message = "Face detection role={Role} count={Count} confidence={Confidence} x={X} y={Y} width={Width} height={Height}")]
    private static partial void LogDetectedFace(ILogger logger, string role, int count, float confidence,
        float x, float y, float width, float height);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug,
        Message = "Face comparison metric=cosine nativeScore={NativeScore} exposedScore={ExposedScore} l2Distance={L2Distance} leftNorm={LeftNorm} rightNorm={RightNorm} leftDimensions={LeftDimensions} rightDimensions={RightDimensions} qualityMs={QualityMs} detectionMs={DetectionMs} alignmentMs={AlignmentMs} embeddingMs={EmbeddingMs} comparisonMs={ComparisonMs} totalMs={TotalMs}")]
    private static partial void LogComparison(ILogger logger, double nativeScore, double exposedScore, double l2Distance,
        double leftNorm, double rightNorm,
        int leftDimensions, int rightDimensions, long qualityMs, long detectionMs, long alignmentMs,
        long embeddingMs, long comparisonMs, long totalMs);

    private FaceVerificationOutcome Outcome(VerificationDecision decision, double referenceQuality, double capturedQuality,
        int referenceFaces, int capturedFaces, Stopwatch stopwatch, double? similarity = null) =>
        new(decision, similarity, options.MatchThreshold, options.NonMatchThreshold, referenceQuality, capturedQuality,
            referenceFaces, capturedFaces, embeddingProvider.ModelName, embeddingProvider.ModelVersion, stopwatch.ElapsedMilliseconds, clock.UtcNow);

    private static VerificationDecision MapQuality(ImageQualityResult result, bool reference) => result.ErrorCode switch
    {
        "unsupported_image_format" => VerificationDecision.UnsupportedImageFormat,
        "insufficient_image_quality" => VerificationDecision.InsufficientImageQuality,
        _ => reference ? VerificationDecision.InvalidReferenceImage : VerificationDecision.InvalidCapturedImage
    };

    private static VerificationDecision? ValidateFaces(FaceDetectionResult reference, FaceDetectionResult captured)
    {
        if (reference.Faces.Count == 0 || captured.Faces.Count == 0) return VerificationDecision.FaceNotDetected;
        if (reference.Faces.Count > 1 || captured.Faces.Count > 1) return VerificationDecision.MultipleFacesDetected;
        return null;
    }

    private static bool IsCentered(DetectedFace face, FaceDetectionResult image)
    {
        var centerX = face.X + face.Width / 2;
        var centerY = face.Y + face.Height / 2;
        return Math.Abs(centerX - image.ImageWidth / 2d) <= image.ImageWidth * 0.35 &&
               Math.Abs(centerY - image.ImageHeight / 2d) <= image.ImageHeight * 0.35;
    }

    public void Dispose() => concurrency.Dispose();
}
