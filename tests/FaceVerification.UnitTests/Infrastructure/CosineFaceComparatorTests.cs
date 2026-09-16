using FaceVerification.Application;
using FaceVerification.Infrastructure.Vision;

namespace FaceVerification.UnitTests.Infrastructure;

public sealed class CosineFaceComparatorTests
{
    private static readonly float[] EmptyEmbedding = [];
    private static readonly float[] NanEmbedding = [float.NaN, 1f];
    private static readonly float[] InfiniteEmbedding = [float.PositiveInfinity, 1f];
    private static readonly float[] ConstantEmbedding = [0.5f, 0.5f];
    private static readonly float[] WrongDimensionEmbedding = [1f, 2f, 3f];
    private readonly CosineFaceComparator comparator = new();

    [Fact]
    public void Identical_vectors_score_one()
    {
        var result = comparator.Compare(new FaceEmbedding([1, 0]), new FaceEmbedding([1, 0]));
        Assert.Equal(1, result.CosineSimilarity, 10);
        Assert.Equal(0, result.L2Distance, 10);
    }

    [Fact]
    public void Orthogonal_vectors_score_zero()
    {
        var result = comparator.Compare(new FaceEmbedding([1, 0]), new FaceEmbedding([0, 1]));
        Assert.Equal(0, result.CosineSimilarity, 10);
        Assert.Equal(Math.Sqrt(2), result.L2Distance, 10);
    }

    [Fact]
    public void Comparison_is_symmetric()
    {
        var leftToRight = comparator.Compare(new FaceEmbedding([0.2f, -0.4f, 0.8f]), new FaceEmbedding([-0.1f, 0.7f, 0.3f]));
        var rightToLeft = comparator.Compare(new FaceEmbedding([-0.1f, 0.7f, 0.3f]), new FaceEmbedding([0.2f, -0.4f, 0.8f]));
        Assert.Equal(leftToRight.CosineSimilarity, rightToLeft.CosineSimilarity, 12);
        Assert.Equal(leftToRight.L2Distance, rightToLeft.L2Distance, 12);
    }

    [Fact]
    public void Zero_vector_is_rejected() => Assert.Throws<ArgumentException>(() => comparator.Compare(new FaceEmbedding([0, 0]), new FaceEmbedding([1, 0])));

    [Theory]
    [MemberData(nameof(InvalidEmbeddings))]
    public void Invalid_embeddings_are_rejected(float[] invalid) =>
        Assert.Throws<ArgumentException>(() => comparator.Compare(new FaceEmbedding(invalid), new FaceEmbedding([1, 0])));

    public static TheoryData<float[]> InvalidEmbeddings => new()
    {
        EmptyEmbedding,
        NanEmbedding,
        InfiniteEmbedding,
        ConstantEmbedding,
        WrongDimensionEmbedding
    };
}
