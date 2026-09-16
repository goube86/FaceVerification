using System.Security.Cryptography;
using System.Text;

namespace FaceVerification.Domain;

public enum VerificationSessionStatus { Pending, Processing, Completed, Failed, Expired }

public enum VerificationDecision
{
    Match,
    NotMatch,
    Inconclusive,
    InvalidReferenceImage,
    InvalidCapturedImage,
    FaceNotDetected,
    MultipleFacesDetected,
    InsufficientImageQuality,
    FaceTooSmall,
    UnsupportedImageFormat,
    CaptureSessionExpired,
    CaptureSessionAlreadyUsed,
    ProcessingFailed
}

public sealed class VerificationSession
{
    private VerificationSession() { }

    public Guid Id { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public string ClientId { get; private set; } = string.Empty;
    public string NonceHash { get; private set; } = string.Empty;
    public VerificationSessionStatus Status { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? LivenessTransactionId { get; private set; }
    public string? LivenessEvidenceHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public uint Version { get; private set; }
    public FaceVerificationRecord? Result { get; private set; }

    public static (VerificationSession Session, string Nonce) Create(
        string userId, string clientId, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (new VerificationSession
        {
            Id = Guid.NewGuid(), UserId = userId, ClientId = clientId,
            NonceHash = Hash(nonce), Status = VerificationSessionStatus.Pending,
            CreatedAt = now, ExpiresAt = now.Add(lifetime)
        }, nonce);
    }

    public bool ValidateNonce(string nonce) =>
        !string.IsNullOrEmpty(nonce) && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(NonceHash), Convert.FromHexString(Hash(nonce)));

    public void Start(DateTimeOffset now, string? livenessTransactionId, string? livenessEvidenceHash)
    {
        if (now >= ExpiresAt) { Status = VerificationSessionStatus.Expired; throw new InvalidOperationException("Session expired."); }
        if (Status != VerificationSessionStatus.Pending) throw new InvalidOperationException("Session cannot be reused.");
        Status = VerificationSessionStatus.Processing;
        StartedAt = now;
        LivenessTransactionId = livenessTransactionId;
        LivenessEvidenceHash = livenessEvidenceHash;
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != VerificationSessionStatus.Processing) throw new InvalidOperationException("Only a processing session can complete.");
        Status = VerificationSessionStatus.Completed;
        CompletedAt = now;
    }

    public void Fail(DateTimeOffset now)
    {
        if (Status != VerificationSessionStatus.Processing) throw new InvalidOperationException("Only a processing session can fail.");
        Status = VerificationSessionStatus.Failed;
        CompletedAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Status is VerificationSessionStatus.Completed or VerificationSessionStatus.Failed) return;
        if (now >= ExpiresAt) Status = VerificationSessionStatus.Expired;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class FaceVerificationRecord
{
    private FaceVerificationRecord() { }
    public Guid Id { get; private set; }
    public Guid VerificationSessionId { get; private set; }
    public VerificationDecision Decision { get; private set; }
    public double? SimilarityScore { get; private set; }
    public double AppliedMatchThreshold { get; private set; }
    public double AppliedNonMatchThreshold { get; private set; }
    public double ReferenceQualityScore { get; private set; }
    public double CapturedQualityScore { get; private set; }
    public int DetectedReferenceFaces { get; private set; }
    public int DetectedCapturedFaces { get; private set; }
    public string ModelName { get; private set; } = string.Empty;
    public string ModelVersion { get; private set; } = string.Empty;
    public long ProcessingTimeMs { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string TraceId { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;

    public static FaceVerificationRecord Create(Guid sessionId, FaceVerificationOutcome outcome, string traceId, string correlationId) => new()
    {
        Id = Guid.NewGuid(), VerificationSessionId = sessionId, Decision = outcome.Decision,
        SimilarityScore = outcome.SimilarityScore, AppliedMatchThreshold = outcome.MatchThreshold,
        AppliedNonMatchThreshold = outcome.NonMatchThreshold, ReferenceQualityScore = outcome.ReferenceQualityScore,
        CapturedQualityScore = outcome.CapturedQualityScore, DetectedReferenceFaces = outcome.DetectedReferenceFaces,
        DetectedCapturedFaces = outcome.DetectedCapturedFaces, ModelName = outcome.ModelName,
        ModelVersion = outcome.ModelVersion, ProcessingTimeMs = outcome.ProcessingTimeMs,
        CreatedAt = outcome.ProcessedAt, TraceId = traceId, CorrelationId = correlationId
    };
}

public sealed record FaceVerificationOutcome(
    VerificationDecision Decision, double? SimilarityScore, double MatchThreshold,
    double NonMatchThreshold, double ReferenceQualityScore, double CapturedQualityScore,
    int DetectedReferenceFaces, int DetectedCapturedFaces, string ModelName,
    string ModelVersion, long ProcessingTimeMs, DateTimeOffset ProcessedAt);
