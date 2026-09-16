using FaceVerification.Domain;
using FaceVerification.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FaceVerification.IntegrationTests;

public sealed class DatabaseIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InitialCreate_and_atomic_processing_transition_work()
    {
        var connection = Environment.GetEnvironmentVariable("FACE_VERIFICATION_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var options = new DbContextOptionsBuilder<FaceVerificationDbContext>().UseNpgsql(connection).Options;
        await using var db = new FaceVerificationDbContext(options);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var (session, nonce) = VerificationSession.Create("integration-user", "integration-client", now, TimeSpan.FromMinutes(5));
        db.VerificationSessions.Add(session);
        await db.SaveChangesAsync();
        var repository = new VerificationSessionRepository(db);
        Assert.True(await repository.TryStartAsync(session.Id, nonce, now, null, null, CancellationToken.None));
        Assert.False(await repository.TryStartAsync(session.Id, nonce, now, null, null, CancellationToken.None));
    }
}
