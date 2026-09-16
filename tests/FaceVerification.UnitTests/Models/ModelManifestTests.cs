using FaceVerification.Infrastructure.Models;

namespace FaceVerification.UnitTests.Models;

public sealed class ModelManifestTests
{
    [Theory]
    [InlineData("yunet/manifest.json")]
    [InlineData("sface/manifest.json")]
    public void Official_model_hash_matches_manifest(string manifest)
    {
        var root = FindRepositoryRoot();
        var parsed = ModelRuntime.ReadAndValidate(Path.Combine(root, "models"), manifest);
        Assert.NotEmpty(parsed.Sha256);
        Assert.StartsWith("https://github.com/opencv/", parsed.Source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FaceVerification.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
