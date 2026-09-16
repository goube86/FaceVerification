using FaceVerification.Application;

namespace FaceVerification.UnitTests.Application;

public sealed class VerificationOptionsTests
{
    [Fact]
    public void Valid_thresholds_are_accepted() => new VerificationOptions { NonMatchThreshold = 0.3, MatchThreshold = 0.6 }.Validate();

    [Theory]
    [InlineData(0.6, 0.6)]
    [InlineData(0.7, 0.6)]
    [InlineData(-1.1, 0.6)]
    [InlineData(0.2, 1.1)]
    public void Invalid_thresholds_are_rejected(double nonMatch, double match) =>
        Assert.Throws<InvalidOperationException>(() => new VerificationOptions { NonMatchThreshold = nonMatch, MatchThreshold = match }.Validate());
}
