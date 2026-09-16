using FaceVerification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FaceVerification.Infrastructure.Persistence;

public sealed class FaceVerificationDbContext(DbContextOptions<FaceVerificationDbContext> options) : DbContext(options)
{
    public DbSet<VerificationSession> VerificationSessions => Set<VerificationSession>();
    public DbSet<FaceVerificationRecord> FaceVerificationResults => Set<FaceVerificationRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfigurationsFromAssembly(typeof(FaceVerificationDbContext).Assembly);
}

internal sealed class VerificationSessionConfiguration : IEntityTypeConfiguration<VerificationSession>
{
    public void Configure(EntityTypeBuilder<VerificationSession> builder)
    {
        builder.ToTable("verification_sessions", t =>
        {
            t.HasCheckConstraint("ck_verification_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            t.HasCheckConstraint("ck_verification_sessions_status", "\"Status\" BETWEEN 0 AND 4");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ClientId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NonceHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.LivenessTransactionId).HasMaxLength(200);
        builder.Property(x => x.LivenessEvidenceHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.Version).IsRowVersion();
        builder.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_verification_sessions_expires_at");
        builder.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_verification_sessions_user_created");
        builder.HasIndex(x => new { x.Status, x.StartedAt }).HasDatabaseName("ix_verification_sessions_status_started");
    }
}

internal sealed class FaceVerificationRecordConfiguration : IEntityTypeConfiguration<FaceVerificationRecord>
{
    public void Configure(EntityTypeBuilder<FaceVerificationRecord> builder)
    {
        builder.ToTable("face_verification_results", t =>
        {
            t.HasCheckConstraint("ck_face_verification_results_similarity", "\"SimilarityScore\" IS NULL OR (\"SimilarityScore\" >= -1 AND \"SimilarityScore\" <= 1)");
            t.HasCheckConstraint("ck_face_verification_results_thresholds", "\"AppliedNonMatchThreshold\" < \"AppliedMatchThreshold\"");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Decision).HasConversion<int>();
        builder.Property(x => x.ModelName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ModelVersion).HasMaxLength(50).IsRequired();
        builder.Property(x => x.TraceId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.VerificationSessionId).IsUnique();
        builder.HasIndex(x => x.CreatedAt);
        builder.HasOne<VerificationSession>().WithOne(x => x.Result).HasForeignKey<FaceVerificationRecord>(x => x.VerificationSessionId).OnDelete(DeleteBehavior.Cascade);
    }
}
