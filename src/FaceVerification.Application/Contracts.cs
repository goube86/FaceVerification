using FaceVerification.Domain;

namespace FaceVerification.Application;

public sealed record FaceImage(ReadOnlyMemory<byte> Bytes, string FileName);
public sealed record FaceLandmark(float X, float Y);
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Confidence, IReadOnlyList<FaceLandmark> Landmarks);
public sealed record FaceDetectionResult(IReadOnlyList<DetectedFace> Faces, int ImageWidth, int ImageHeight);
public sealed record AlignedFace(ReadOnlyMemory<byte> BgrPixels, int Width, int Height);
public sealed record FaceEmbedding(float[] Values);
public sealed record FaceComparisonResult(double CosineSimilarity, double L2Distance);
public sealed record ImageQualityResult(bool IsValid, double Score, string? ErrorCode = null);

public interface IFaceDetector
{
    Task<FaceDetectionResult> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken);
}

public interface IFaceEmbeddingProvider
{
    string ModelName { get; }
    string ModelVersion { get; }
    Task<FaceEmbedding> GenerateAsync(AlignedFace face, CancellationToken cancellationToken);
}

public interface IImageQualityValidator
{
    ImageQualityResult Validate(ReadOnlyMemory<byte> image);
}

public interface IFaceAligner
{
    AlignedFace Align(ReadOnlyMemory<byte> image, DetectedFace face);
}

public interface IFaceComparator
{
    FaceComparisonResult Compare(FaceEmbedding left, FaceEmbedding right);
}

public interface IFaceVerificationService
{
    Task<FaceVerificationOutcome> VerifyAsync(FaceImage referenceImage, FaceImage capturedImage, CancellationToken cancellationToken);
}

public interface IVerificationSessionRepository
{
    Task AddAsync(VerificationSession session, CancellationToken cancellationToken);
    Task<VerificationSession?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> TryStartAsync(Guid id, string nonce, DateTimeOffset now, string? transactionId, string? evidenceHash, CancellationToken cancellationToken);
    Task AddResultAsync(FaceVerificationRecord result, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class VerificationOptions
{
    public const string SectionName = "Verification";
    public double MatchThreshold { get; init; } = 0.55;
    public double NonMatchThreshold { get; init; } = 0.35;
    public int SessionLifetimeMinutes { get; init; } = 5;
    public int MaxFileSizeBytes { get; init; } = 5 * 1024 * 1024;
    public int MaxWidth { get; init; } = 4096;
    public int MaxHeight { get; init; } = 4096;
    public long MaxPixels { get; init; } = 16_000_000;
    public int MinimumFacePixels { get; init; } = 80;
    public int InferenceTimeoutSeconds { get; init; } = 15;
    public int MaxConcurrentInferences { get; init; } = 2;
    public double MinimumQualityScore { get; init; } = 0.45;
    public int AbandonedProcessingMinutes { get; init; } = 5;
    public int RetentionDays { get; init; } = 30;
    public bool BypassSessionValidation { get; init; }

    public void Validate()
    {
        if (NonMatchThreshold is < -1 or > 1 || MatchThreshold is < -1 or > 1 || NonMatchThreshold >= MatchThreshold)
            throw new InvalidOperationException("Verification thresholds must be within [-1, 1] and NonMatchThreshold must be lower than MatchThreshold.");
        if (SessionLifetimeMinutes <= 0 || MaxFileSizeBytes <= 0 || MaxPixels <= 0 || MaxConcurrentInferences <= 0 || AbandonedProcessingMinutes <= 0 || RetentionDays <= 0)
            throw new InvalidOperationException("Verification limits must be positive.");
    }
}

public sealed record CreateVerificationSessionResult(Guid SessionId, string Nonce, DateTimeOffset ExpiresAt);

public sealed class CreateVerificationSessionHandler(IVerificationSessionRepository repository, IClock clock, VerificationOptions options)
{
    public async Task<CreateVerificationSessionResult> HandleAsync(string userId, string clientId, CancellationToken cancellationToken)
    {
        var (session, nonce) = VerificationSession.Create(userId, clientId, clock.UtcNow, TimeSpan.FromMinutes(options.SessionLifetimeMinutes));
        await repository.AddAsync(session, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return new(session.Id, nonce, session.ExpiresAt);
    }
}

public sealed class VerifySessionHandler(IVerificationSessionRepository repository, IFaceVerificationService verifier, IClock clock)
{
    public async Task<(Guid VerificationId, FaceVerificationOutcome Outcome)> HandleAsync(
        Guid sessionId, string nonce, FaceImage reference, FaceImage captured,
        string? transactionId, string? evidenceHash, string traceId, string correlationId,
        CancellationToken cancellationToken)
    {
        if (!await repository.TryStartAsync(sessionId, nonce, clock.UtcNow, transactionId, evidenceHash, cancellationToken))
            throw new VerificationSessionException("session_not_available", "The session is expired, invalid, or already used.");

        var session = await repository.FindAsync(sessionId, cancellationToken)
            ?? throw new VerificationSessionException("session_not_found", "The session was not found.");
        try
        {
            var outcome = await verifier.VerifyAsync(reference, captured, cancellationToken);
            var record = FaceVerificationRecord.Create(sessionId, outcome, traceId, correlationId);
            await repository.AddResultAsync(record, cancellationToken);
            session.Complete(clock.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            return (record.Id, outcome);
        }
        catch
        {
            session.Fail(clock.UtcNow);
            await repository.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}

public sealed class VerificationSessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
