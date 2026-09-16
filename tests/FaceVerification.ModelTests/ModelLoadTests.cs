using FaceVerification.Infrastructure.Models;

namespace FaceVerification.ModelTests;

public sealed class ModelLoadTests
{
    [Fact]
    [Trait("Category", "Model")]
    public void Official_models_load_and_warm_up_once()
    {
        var root = FindRepositoryRoot();
        using var runtime = new ModelRuntime(new ModelOptions { RootPath = Path.Combine(root, "models") });
        Assert.NotEmpty(runtime.YuNetSession.InputNames);
        Assert.NotEmpty(runtime.SFaceSession.InputNames);
        Assert.Equal(128, runtime.SFaceManifest.EmbeddingSize);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FaceVerification.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
