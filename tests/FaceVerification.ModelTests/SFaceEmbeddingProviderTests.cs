using FaceVerification.Application;
using FaceVerification.Infrastructure.Models;
using FaceVerification.Infrastructure.Vision;
using OpenCvSharp;

namespace FaceVerification.ModelTests;

public sealed class SFaceEmbeddingProviderTests
{
    [Fact]
    [Trait("Category", "Model")]
    public async Task Real_model_returns_independent_finite_normalized_embeddings()
    {
        using var runtime = CreateRuntime();
        var provider = new SFaceEmbeddingProvider(runtime);

        var first = await provider.GenerateAsync(CreatePattern(invert: false), CancellationToken.None);
        var second = await provider.GenerateAsync(CreatePattern(invert: true), CancellationToken.None);

        Assert.NotSame(first.Values, second.Values);
        Assert.Equal(runtime.SFaceManifest.EmbeddingSize, first.Values.Length);
        Assert.Equal(runtime.SFaceManifest.EmbeddingSize, second.Values.Length);
        Assert.All(first.Values, value => Assert.True(float.IsFinite(value)));
        Assert.All(second.Values, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(1, Norm(first), 5);
        Assert.Equal(1, Norm(second), 5);
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Real_model_same_aligned_image_has_maximum_cosine_score()
    {
        using var runtime = CreateRuntime();
        var provider = new SFaceEmbeddingProvider(runtime);
        var image = CreatePattern(invert: false);
        var first = await provider.GenerateAsync(image, CancellationToken.None);
        var second = await provider.GenerateAsync(image, CancellationToken.None);

        var comparison = new CosineFaceComparator().Compare(first, second);

        Assert.Equal(1, comparison.CosineSimilarity, 6);
        Assert.Equal(0, comparison.L2Distance, 6);
    }

    private static ModelRuntime CreateRuntime() =>
        new(new ModelOptions { RootPath = Path.Combine(FindRepositoryRoot(), "models") });

    private static AlignedFace CreatePattern(bool invert)
    {
        using var image = new Mat(new Size(112, 112), MatType.CV_8UC3, invert ? new Scalar(220, 170, 40) : new Scalar(20, 70, 210));
        Cv2.Circle(image, new Point(36, 44), 12, invert ? Scalar.Black : Scalar.White, -1);
        Cv2.Rectangle(image, new Rect(62, 66, 28, 20), invert ? Scalar.White : Scalar.Black, -1);
        return new AlignedFace(image.ToBytes(".bmp"), image.Width, image.Height);
    }

    private static double Norm(FaceEmbedding embedding) =>
        Math.Sqrt(embedding.Values.Sum(value => (double)value * value));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FaceVerification.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
