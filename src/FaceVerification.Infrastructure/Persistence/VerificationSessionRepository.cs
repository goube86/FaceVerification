using System.Security.Cryptography;
using System.Text;
using FaceVerification.Application;
using FaceVerification.Domain;
using Microsoft.EntityFrameworkCore;

namespace FaceVerification.Infrastructure.Persistence;

public sealed class VerificationSessionRepository(FaceVerificationDbContext dbContext) : IVerificationSessionRepository
{
    public Task AddAsync(VerificationSession session, CancellationToken cancellationToken) =>
        dbContext.VerificationSessions.AddAsync(session, cancellationToken).AsTask();

    public Task<VerificationSession?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.VerificationSessions.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<bool> TryStartAsync(Guid id, string nonce, DateTimeOffset now, string? transactionId, string? evidenceHash, CancellationToken cancellationToken)
    {
        var nonceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
        var rows = await dbContext.VerificationSessions
            .Where(x => x.Id == id && x.Status == VerificationSessionStatus.Pending && x.ExpiresAt > now && x.NonceHash == nonceHash)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, VerificationSessionStatus.Processing)
                .SetProperty(x => x.StartedAt, now)
                .SetProperty(x => x.LivenessTransactionId, transactionId)
                .SetProperty(x => x.LivenessEvidenceHash, evidenceHash), cancellationToken);
        return rows == 1;
    }

    public Task AddResultAsync(FaceVerificationRecord result, CancellationToken cancellationToken) =>
        dbContext.FaceVerificationResults.AddAsync(result, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
