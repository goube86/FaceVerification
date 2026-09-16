using FaceVerification.Application;
using FaceVerification.Domain;
using FaceVerification.Infrastructure.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceVerification.UnitTests.Infrastructure;

public sealed class FaceVerificationServiceTests
{
    [Theory]
    [InlineData(0.8, VerificationDecision.Match)]
    [InlineData(0.2, VerificationDecision.NotMatch)]
    [InlineData(0.45, VerificationDecision.Inconclusive)]
    public async Task Applies_three_way_thresholds(double similarity, VerificationDecision expected)
    {
        var service = CreateService(new[] { OneFace(), OneFace() }, similarity);
        var result = await service.VerifyAsync(Image(), Image(), CancellationToken.None);
        Assert.Equal(expected, result.Decision);
    }

    [Theory]
    [InlineData(0, 1, VerificationDecision.FaceNotDetected)]
    [InlineData(1, 0, VerificationDecision.FaceNotDetected)]
    [InlineData(2, 1, VerificationDecision.MultipleFacesDetected)]
    [InlineData(1, 2, VerificationDecision.MultipleFacesDetected)]
    public async Task Requires_exactly_one_face(int referenceFaces, int capturedFaces, VerificationDecision expected)
    {
        var service = CreateService(new[] { Detection(referenceFaces), Detection(capturedFaces) }, 1);
        var result = await service.VerifyAsync(Image(), Image(), CancellationToken.None);
        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public async Task Quality_failure_is_not_reported_as_non_match()
    {
        var service = new FaceVerificationService(new FakeQuality(false), new FakeDetector([]), new FakeAligner(),
            new FakeEmbedding(), new FakeComparator(0), Options(), new FakeClock(), NullLogger<FaceVerificationService>.Instance);
        var result = await service.VerifyAsync(Image(), Image(), CancellationToken.None);
        Assert.Equal(VerificationDecision.InsufficientImageQuality, result.Decision);
        Assert.NotEqual(VerificationDecision.NotMatch, result.Decision);
    }

    [Fact]
    public async Task Same_input_with_same_embedding_produces_maximum_score()
    {
        var service = CreateService(new[] { OneFace(), OneFace() }, new CosineFaceComparator(), new FakeEmbedding());
        var result = await service.VerifyAsync(Image(1), Image(1), CancellationToken.None);
        Assert.Equal(VerificationDecision.Match, result.Decision);
        Assert.Equal(1, result.SimilarityScore);
    }

    [Fact]
    public async Task Second_embedding_cannot_overwrite_the_first()
    {
        var service = CreateService(new[] { OneFace(), OneFace() }, new CosineFaceComparator(), new ReusingEmbedding());
        var result = await service.VerifyAsync(Image(1), Image(2), CancellationToken.None);
        Assert.Equal(VerificationDecision.NotMatch, result.Decision);
        Assert.Equal(0, result.SimilarityScore);
    }

    [Fact]
    public async Task Different_inputs_reach_alignment_and_embedding_independently()
    {
        var aligner = new RecordingAligner();
        var embedding = new RecordingEmbedding();
        var service = new FaceVerificationService(new FakeQuality(true), new FakeDetector([OneFace(), OneFace()]), aligner,
            embedding, new CosineFaceComparator(), Options(), new FakeClock(), NullLogger<FaceVerificationService>.Instance);

        await service.VerifyAsync(Image(17), Image(29), CancellationToken.None);

        Assert.Equal(new byte[] { 17, 29 }, aligner.Inputs);
        Assert.Equal(new byte[] { 17, 29 }, embedding.Inputs);
        Assert.NotSame(embedding.Results[0].Values, embedding.Results[1].Values);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.15)]
    [InlineData(0.20)]
    public async Task Different_people_against_one_reference_do_not_match(double cosine)
    {
        var service = CreateService(new[] { OneFace(), OneFace() }, cosine);
        var result = await service.VerifyAsync(Image(1), Image(2), CancellationToken.None);
        Assert.Equal(VerificationDecision.NotMatch, result.Decision);
    }

    private static FaceVerificationService CreateService(IEnumerable<FaceDetectionResult> detections, double similarity) =>
        CreateService(detections, new FakeComparator(similarity), new FakeEmbedding());

    private static FaceVerificationService CreateService(IEnumerable<FaceDetectionResult> detections, IFaceComparator comparator, IFaceEmbeddingProvider embedding) =>
        new(new FakeQuality(true), new FakeDetector(detections), new FakeAligner(), embedding, comparator, Options(), new FakeClock(), NullLogger<FaceVerificationService>.Instance);

    private static VerificationOptions Options() => new() { NonMatchThreshold = 0.35, MatchThreshold = 0.55, MinimumFacePixels = 40 };
    private static FaceImage Image(byte value = 1) => new(new[] { value }, "image.jpg");
    private static FaceDetectionResult OneFace() => Detection(1);
    private static FaceDetectionResult Detection(int count) => new(
        Enumerable.Range(0, count).Select(_ => new DetectedFace(50, 50, 100, 100, 0.99f,
            [new(80, 90), new(120, 90), new(100, 110), new(85, 130), new(115, 130)])).ToArray(), 200, 200);

    private sealed class FakeQuality(bool valid) : IImageQualityValidator
    {
        public ImageQualityResult Validate(ReadOnlyMemory<byte> image) => new(valid, valid ? 1 : 0, valid ? null : "insufficient_image_quality");
    }
    private sealed class FakeDetector(IEnumerable<FaceDetectionResult> results) : IFaceDetector
    {
        private readonly Queue<FaceDetectionResult> queue = new(results);
        public Task<FaceDetectionResult> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken) => Task.FromResult(queue.Dequeue());
    }
    private sealed class FakeAligner : IFaceAligner { public AlignedFace Align(ReadOnlyMemory<byte> image, DetectedFace face) => new(new byte[] { 1 }, 112, 112); }
    private sealed class RecordingAligner : IFaceAligner
    {
        public List<byte> Inputs { get; } = [];
        public AlignedFace Align(ReadOnlyMemory<byte> image, DetectedFace face)
        {
            Inputs.Add(image.Span[0]);
            return new(image.ToArray(), 112, 112);
        }
    }
    private sealed class FakeEmbedding : IFaceEmbeddingProvider
    {
        public string ModelName => "test"; public string ModelVersion => "1";
        public Task<FaceEmbedding> GenerateAsync(AlignedFace face, CancellationToken cancellationToken) => Task.FromResult(new FaceEmbedding([1, 0]));
    }
    private sealed class RecordingEmbedding : IFaceEmbeddingProvider
    {
        public string ModelName => "test";
        public string ModelVersion => "1";
        public List<byte> Inputs { get; } = [];
        public List<FaceEmbedding> Results { get; } = [];
        public Task<FaceEmbedding> GenerateAsync(AlignedFace face, CancellationToken cancellationToken)
        {
            Inputs.Add(face.BgrPixels.Span[0]);
            var result = new FaceEmbedding([face.BgrPixels.Span[0], 1]);
            Results.Add(result);
            return Task.FromResult(result);
        }
    }
    private sealed class ReusingEmbedding : IFaceEmbeddingProvider
    {
        private readonly float[] shared = [1, 0];
        private int calls;
        public string ModelName => "test";
        public string ModelVersion => "1";
        public Task<FaceEmbedding> GenerateAsync(AlignedFace face, CancellationToken cancellationToken)
        {
            if (calls++ > 0) { shared[0] = 0; shared[1] = 1; }
            return Task.FromResult(new FaceEmbedding(shared));
        }
    }
    private sealed class FakeComparator(double score) : IFaceComparator
    {
        public FaceComparisonResult Compare(FaceEmbedding left, FaceEmbedding right) => new(score, Math.Sqrt(Math.Max(0, 2 - (2 * score))));
    }
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
