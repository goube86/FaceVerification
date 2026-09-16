using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FaceVerification.Infrastructure.Persistence;

public sealed class FaceVerificationDbContextFactory : IDesignTimeDbContextFactory<FaceVerificationDbContext>
{
    public FaceVerificationDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FaceVerification")
            ?? "Host=localhost;Port=5432;Database=face_verification;Username=face_verification;Password=development-only";
        var options = new DbContextOptionsBuilder<FaceVerificationDbContext>().UseNpgsql(connection).Options;
        return new FaceVerificationDbContext(options);
    }
}
