using System.Security.Claims;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using FaceVerification.Application;
using FaceVerification.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FaceVerification.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/verification-sessions")]
[Produces("application/json")]
public sealed class VerificationSessionsController : ControllerBase
{
    /// <summary>Creates a short-lived, single-use facial verification session.</summary>
    [HttpPost]
    [EnableRateLimiting("verification")]
    [ProducesResponseType<CreateSessionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CreateSessionResponse>> Create(
        [FromServices] CreateVerificationSessionHandler handler, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue("sub") ?? throw new UnauthorizedAccessException("The sub claim is required.");
        var clientId = User.FindFirstValue("client_id") ?? throw new UnauthorizedAccessException("The client_id claim is required.");
        var result = await handler.HandleAsync(userId, clientId, cancellationToken);
        var response = new CreateSessionResponse(result.SessionId, result.Nonce, result.ExpiresAt);
        return Created($"/api/v1/verification-sessions/{result.SessionId}", response);
    }

    /// <summary>Validates two encoded images and compares the single face in each image.</summary>
    /// <remarks>Accepts JPEG, PNG, or WebP. Each file is limited to 5 MiB by default. Liveness fields are retained as unverified evidence only.</remarks>
    [HttpPost("{sessionId:guid}/verify")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = 10_500_000)]
    [RequestSizeLimit(10_500_000)]
    [RequestTimeout("inference")]
    [EnableRateLimiting("verification")]
    [ProducesResponseType<VerifySessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<VerifySessionResponse>> Verify(
        Guid sessionId, [FromForm] VerifySessionRequest request,
        [FromServices] VerifySessionHandler handler,
        [FromServices] IFaceVerificationService verifier,
        [FromServices] VerificationOptions options,
        [FromServices] IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (request.ReferenceImage.Length == 0 || request.CapturedImage.Length == 0)
            return Problem(statusCode: 400, title: "Images are required", detail: "Both image fields must contain data.");
        var referenceBytes = await ReadAsync(request.ReferenceImage, cancellationToken);
        var capturedBytes = await ReadAsync(request.CapturedImage, cancellationToken);
        var referenceImage = new FaceImage(referenceBytes, request.ReferenceImage.FileName);
        var capturedImage = new FaceImage(capturedBytes, request.CapturedImage.FileName);
        Guid verificationId;
        FaceVerificationOutcome outcome;
        if (environment.IsDevelopment() && options.BypassSessionValidation)
        {
            verificationId = Guid.NewGuid();
            outcome = await verifier.VerifyAsync(referenceImage, capturedImage, cancellationToken);
        }
        else
        {
            var nonce = request.Nonce;
            if (string.IsNullOrWhiteSpace(nonce))
                return Problem(statusCode: 400, title: "Nonce is required", detail: "A valid single-use nonce is required outside the Development bypass.");
            var evidenceHash = string.IsNullOrEmpty(request.LivenessEvidence) ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.LivenessEvidence)));
            var correlationId = HttpContext.Items["CorrelationId"]?.ToString() ?? string.Empty;
            (verificationId, outcome) = await handler.HandleAsync(sessionId, nonce, referenceImage, capturedImage,
                request.LivenessTransactionId, evidenceHash, HttpContext.TraceIdentifier, correlationId, cancellationToken);
        }
        return Ok(new VerifySessionResponse(verificationId, sessionId, outcome.Decision.ToString(),
            outcome.Decision == VerificationDecision.Match ? true : outcome.Decision == VerificationDecision.NotMatch ? false : null,
            outcome.SimilarityScore, outcome.MatchThreshold, outcome.NonMatchThreshold, outcome.ReferenceQualityScore,
            outcome.CapturedQualityScore, outcome.ModelName, outcome.ModelVersion, outcome.ProcessingTimeMs, outcome.ProcessedAt));
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

public sealed record CreateSessionResponse(Guid SessionId, string Nonce, DateTimeOffset ExpiresAt);

public sealed class VerifySessionRequest
{
    [Required]
    [FromForm(Name = "referenceImage")]
    public required IFormFile ReferenceImage { get; init; }

    [Required]
    [FromForm(Name = "capturedImage")]
    public required IFormFile CapturedImage { get; init; }

    [FromForm(Name = "nonce")]
    public string? Nonce { get; init; }

    [FromForm(Name = "livenessTransactionId")]
    public string? LivenessTransactionId { get; init; }

    [FromForm(Name = "livenessEvidence")]
    public string? LivenessEvidence { get; init; }
}

public sealed record VerifySessionResponse(
    Guid VerificationId, Guid SessionId, string Decision, bool? IsMatch, double? SimilarityScore,
    double MatchThreshold, double NonMatchThreshold, double ReferenceImageQuality,
    double CapturedImageQuality, string ModelName, string ModelVersion, long ProcessingTimeMs, DateTimeOffset ProcessedAt);
