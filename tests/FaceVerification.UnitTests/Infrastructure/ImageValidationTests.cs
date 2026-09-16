using FaceVerification.Application;
using FaceVerification.Infrastructure.Vision;

namespace FaceVerification.UnitTests.Infrastructure;

public sealed class ImageValidationTests
{
    [Fact]
    public void Rejects_unsupported_real_signature()
    {
        var validator = new OpenCvImageQualityValidator(new VerificationOptions());
        Assert.Equal("unsupported_image_format", validator.Validate(new byte[20]).ErrorCode);
    }

    [Fact]
    public void Rejects_oversized_file_before_decoding()
    {
        var validator = new OpenCvImageQualityValidator(new VerificationOptions { MaxFileSizeBytes = 4 });
        Assert.Equal("invalid_file_size", validator.Validate(new byte[5]).ErrorCode);
    }
}
