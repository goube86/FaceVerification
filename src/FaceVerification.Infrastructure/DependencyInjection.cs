using FaceVerification.Application;
using FaceVerification.Domain;
using FaceVerification.Infrastructure.Models;
using FaceVerification.Infrastructure.Persistence;
using FaceVerification.Infrastructure.Vision;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FaceVerification.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFaceVerificationInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string modelsRoot)
    {
        var verificationOptions = configuration.GetSection(VerificationOptions.SectionName).Get<VerificationOptions>() ?? new();
        verificationOptions.Validate();
        services.AddSingleton(verificationOptions);
        var modelOptions = configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new();
        modelOptions = new ModelOptions { RootPath = modelsRoot, YuNetManifest = modelOptions.YuNetManifest, SFaceManifest = modelOptions.SFaceManifest };
        services.AddSingleton(modelOptions);
        services.AddSingleton<ModelRuntime>();
        services.AddSingleton<IImageQualityValidator, OpenCvImageQualityValidator>();
        services.AddSingleton<IFaceDetector, YuNetFaceDetector>();
        services.AddSingleton<IFaceEmbeddingProvider, SFaceEmbeddingProvider>();
        services.AddSingleton<IFaceAligner, OpenCvFaceAligner>();
        services.AddSingleton<IFaceComparator, CosineFaceComparator>();
        services.AddSingleton<IFaceVerificationService, FaceVerificationService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IVerificationSessionRepository, VerificationSessionRepository>();
        services.AddScoped<CreateVerificationSessionHandler>();
        services.AddScoped<VerifySessionHandler>();
        services.AddDbContext<FaceVerificationDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("FaceVerification") ?? throw new InvalidOperationException("ConnectionStrings:FaceVerification is required.")));
        services.AddHostedService<VerificationSessionMaintenanceService>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"])
            .AddCheck<YuNetModelHealthCheck>("yunet", tags: ["ready"])
            .AddCheck<SFaceModelHealthCheck>("sface", tags: ["ready"]);
        return services;
    }
}

internal sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

internal sealed class DatabaseHealthCheck(FaceVerificationDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
}

internal sealed class YuNetModelHealthCheck(ModelRuntime runtime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(runtime.YuNetSession is not null
            ? HealthCheckResult.Healthy($"{runtime.YuNetManifest.Name} is loaded.")
            : HealthCheckResult.Unhealthy("YuNet is unavailable."));
}

internal sealed class SFaceModelHealthCheck(ModelRuntime runtime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(runtime.SFaceSession is not null
            ? HealthCheckResult.Healthy($"{runtime.SFaceManifest.Name} is loaded.")
            : HealthCheckResult.Unhealthy("SFace is unavailable."));
}

internal sealed partial class VerificationSessionMaintenanceService(
    IServiceScopeFactory scopeFactory, VerificationOptions options, ILogger<VerificationSessionMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FaceVerificationDbContext>();
                var now = DateTimeOffset.UtcNow;
                var expired = await db.VerificationSessions
                    .Where(x => x.Status == VerificationSessionStatus.Pending && x.ExpiresAt <= now)
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, VerificationSessionStatus.Expired), stoppingToken);
                var abandoned = await db.VerificationSessions
                    .Where(x => x.Status == VerificationSessionStatus.Processing && x.StartedAt < now.AddMinutes(-options.AbandonedProcessingMinutes))
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, VerificationSessionStatus.Failed).SetProperty(s => s.CompletedAt, now), stoppingToken);
                var retentionCutoff = now.AddDays(-options.RetentionDays);
                var deleted = await db.VerificationSessions
                    .Where(x => (x.Status == VerificationSessionStatus.Completed || x.Status == VerificationSessionStatus.Failed || x.Status == VerificationSessionStatus.Expired) &&
                        ((x.CompletedAt != null && x.CompletedAt < retentionCutoff) || (x.CompletedAt == null && x.ExpiresAt < retentionCutoff)))
                    .ExecuteDeleteAsync(stoppingToken);
                if (expired + abandoned + deleted > 0) LogSessionsUpdated(logger, expired + abandoned, deleted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogMaintenanceFailure(logger, exception);
            }
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Session maintenance updated {SessionCount} sessions and deleted {DeletedCount} retained sessions")]
    private static partial void LogSessionsUpdated(ILogger logger, int sessionCount, int deletedCount);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Session maintenance failed")]
    private static partial void LogMaintenanceFailure(ILogger logger, Exception exception);
}
