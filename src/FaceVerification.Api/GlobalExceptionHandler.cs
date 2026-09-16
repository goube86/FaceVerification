using FaceVerification.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FaceVerification.Api;

public sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = exception switch
        {
            VerificationSessionException session => (StatusCodes.Status409Conflict, session.Code, "Verification session unavailable"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "invalid_request", "Invalid request"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "identity_claim_missing", "Required identity claim missing"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "request_cancelled", "Request cancelled"),
            OperationCanceledException => (StatusCodes.Status408RequestTimeout, "processing_timeout", "Processing timed out"),
            _ => (StatusCodes.Status500InternalServerError, "processing_failed", "Processing failed")
        };
        if (status >= 500) LogProcessingFailure(logger, exception, httpContext.TraceIdentifier);
        else LogRequestRejected(logger, code, httpContext.TraceIdentifier);
        var details = new ProblemDetails { Status = status, Title = title, Detail = status >= 500 ? "An unexpected processing error occurred." : exception.Message, Type = $"https://errors.faceverification.local/{code}" };
        details.Extensions["code"] = code;
        details.Extensions["traceId"] = httpContext.TraceIdentifier;
        details.Extensions["correlationId"] = httpContext.Items["CorrelationId"]?.ToString() ?? string.Empty;
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(details, cancellationToken);
        return true;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Request processing failed. TraceId: {TraceId}")]
    private static partial void LogProcessingFailure(ILogger logger, Exception exception, string traceId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Request rejected with {ErrorCode}. TraceId: {TraceId}")]
    private static partial void LogRequestRejected(ILogger logger, string errorCode, string traceId);
}
