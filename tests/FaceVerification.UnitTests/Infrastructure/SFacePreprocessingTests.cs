using FaceVerification.Infrastructure.Vision;
using OpenCvSharp;

namespace FaceVerification.UnitTests.Infrastructure;

public sealed class SFacePreprocessingTests
{
    [Fact]
    public void Sface_input_is_raw_rgb_chw_as_required_by_face_recognizer_sf()
    {
        using var bgr = new Mat(new Size(1, 1), MatType.CV_8UC3, new Scalar(10, 20, 30));

        var tensor = YuNetFaceDetector.ToNchw(bgr, swapRedAndBlue: true, scale: 1f, mean: 0f);

        Assert.Equal(new float[] { 30, 20, 10 }, tensor);
    }

    [Fact]
    public void Detector_input_remains_raw_bgr_chw()
    {
        using var bgr = new Mat(new Size(1, 1), MatType.CV_8UC3, new Scalar(10, 20, 30));

        var tensor = YuNetFaceDetector.ToNchw(bgr, swapRedAndBlue: false, scale: 1f, mean: 0f);

        Assert.Equal(new float[] { 10, 20, 30 }, tensor);
    }
}
