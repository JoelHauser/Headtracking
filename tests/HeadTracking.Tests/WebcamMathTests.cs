using HeadTracking.App.Webcam;

namespace HeadTracking.Tests;

public class ImageOpsTests
{
    private static GrayImage Image(int w, int h, Func<int, int, byte> pixel)
    {
        var image = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.Data[y * w + x] = pixel(x, y);
        return image;
    }

    [Fact]
    public void ShrinkingByTwoAveragesEachBlockExactly()
    {
        var src = Image(4, 2, (x, y) => (byte)(x < 2 ? (y == 0 ? 10 : 30) : (y == 0 ? 100 : 200)));
        var dst = new byte[2];
        ImageOps.ResampleRegion(src, 0, 0, 4, 2, 2, 1, dst);

        Assert.Equal(20, dst[0]);
        Assert.Equal(150, dst[1]);
    }

    [Fact]
    public void FractionalCoverageIsWeighted()
    {
        // Three columns into two: each output takes 1.5 source pixels.
        var src = Image(3, 1, (x, y) => (byte)(x == 0 ? 0 : x == 1 ? 90 : 180));
        var dst = new byte[2];
        ImageOps.ResampleRegion(src, 0, 0, 3, 1, 2, 1, dst);

        Assert.Equal(30, dst[0]);   // (0*1 + 90*0.5) / 1.5
        Assert.Equal(150, dst[1]);  // (90*0.5 + 180*1) / 1.5
    }

    [Theory]
    [InlineData(-50f, -50f, 300f, 7, 7)]    // region far outside: edges replicated
    [InlineData(10f, 20f, 40f, 129, 129)]   // enlarging: bilinear
    [InlineData(3.3f, 1.7f, 90f, 32, 32)]   // shrinking from a fractional origin
    public void AConstantImageStaysConstantWhateverTheRegion(float x0, float y0, float size, int w, int h)
    {
        var src = Image(64, 48, (x, y) => 77);
        var dst = new byte[w * h];
        ImageOps.ResampleRegion(src, x0, y0, size, size, w, h, dst);

        Assert.All(dst, v => Assert.Equal(77, v));
    }

    [Fact]
    public void TheLocalizerInputIsCentredOnZero()
    {
        var src = Image(640, 480, (x, y) => 255);
        var scratch = new byte[288 * 224];
        var tensor = new float[288 * 224];
        ImageOps.ResizeToTensor(src, 288, 224, scratch, tensor);

        Assert.All(tensor, v => Assert.Equal(0.5f, v, 4));
    }

    [Fact]
    public void DarkPatchesAreStretchedLikeOpenTrackDoes()
    {
        // More than 90% of pixels at or below 40 (OpenTrack counts strictly more): alpha = 0.9 * 0.5 / 40.
        byte[] pixels = Enumerable.Range(0, 100).Select(i => (byte)(i < 91 ? 40 : 200)).ToArray();
        var tensor = new float[100];
        ImageOps.NormalizeBrightness(pixels, 100, tensor);

        Assert.Equal(40, ImageOps.IntensityQuantile(pixels, 100, 90));
        Assert.Equal(40 * (0.9f * 0.5f / 40) - 0.5f, tensor[0], 5);
    }

    [Fact]
    public void BrightPatchesAreOnlyScaled()
    {
        byte[] pixels = Enumerable.Repeat((byte)200, 50).ToArray();
        var tensor = new float[50];
        ImageOps.NormalizeBrightness(pixels, 50, tensor);

        Assert.Equal(200 / 255f - 0.5f, tensor[0], 5);
    }

    [Fact]
    public void IoUIsOneForTheSameBoxAndZeroForDisjointOnes()
    {
        var a = new RectF(0, 0, 10, 10);
        Assert.Equal(1f, RectF.IoU(a, a), 5);
        Assert.Equal(0f, RectF.IoU(a, new RectF(20, 20, 5, 5)), 5);
        Assert.Equal(50f / 150f, RectF.IoU(a, new RectF(5, 0, 10, 10)), 5);
    }
}

public class PoseMathTests
{
    private static readonly CamIntrinsics Lens = CamIntrinsics.FromDiagonalFov(640f / 480f, 70f);

    [Fact]
    public void ImageToWorldOnQuaternionsIsItsOwnInverse()
    {
        var q = new Quat(0.9f, 0.1f, -0.3f, 0.2f).Normalized;
        Quat back = PoseMath.ImageToWorld(PoseMath.ImageToWorld(q));
        Assert.Equal(q.W, back.W, 6);
        Assert.Equal(q.X, back.X, 6);
        Assert.Equal(q.Y, back.Y, 6);
        Assert.Equal(q.Z, back.Z, 6);
    }

    [Fact]
    public void AFrontalFaceInTheMiddleIsStraightAhead()
    {
        PoseMath.TransformToWorld(Quat.Identity, 320, 240, 60, 640, 480, Lens, out Quat rotation, out Vec3 position);
        HeadPose pose = PoseMath.ToOpenTrack(rotation, position);

        Assert.Equal(0, pose.Yaw, 4);
        Assert.Equal(0, pose.Pitch, 4);
        Assert.Equal(0, pose.Roll, 4);
        Assert.Equal(0, pose.X, 4);
        Assert.Equal(0, pose.Y, 4);
        Assert.True(pose.Z > 20 && pose.Z < 300, "z " + pose.Z);
    }

    [Fact]
    public void AHalfSizeFaceIsTwiceAsFar()
    {
        PoseMath.TransformToWorld(Quat.Identity, 320, 240, 80, 640, 480, Lens, out _, out Vec3 near);
        PoseMath.TransformToWorld(Quat.Identity, 320, 240, 40, 640, 480, Lens, out _, out Vec3 far);

        // The face's own distance (before the neck offset) scales with 1/size.
        float faceNear = near.X + 100f, faceFar = far.X + 100f;
        Assert.Equal(2.0, faceFar / faceNear, 4);
    }

    [Fact]
    public void AFaceSeenStraightOnAtTheEdgeIsTurnedTowardTheCamera()
    {
        PoseMath.TransformToWorld(Quat.Identity, 470, 240, 60, 640, 480, Lens, out Quat right, out Vec3 rightPos);
        PoseMath.TransformToWorld(Quat.Identity, 170, 240, 60, 640, 480, Lens, out Quat left, out Vec3 leftPos);
        HeadPose r = PoseMath.ToOpenTrack(right, rightPos), l = PoseMath.ToOpenTrack(left, leftPos);

        // Mirror images of each other: same size of correction, opposite sign, and the head's
        // sideways position follows the image.
        Assert.Equal(-r.Yaw, l.Yaw, 3);
        Assert.True(Math.Abs(r.Yaw) > 5, "correction " + r.Yaw);
        Assert.True(r.X > 0 && l.X < 0);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(-35)]
    public void EulerAnglesComeOutAsOpenTracksFormulaSays(float degrees)
    {
        float rad = degrees * (float)Math.PI / 180f;
        // In the tracker's world frame (x forward, y up), turning about y is yaw, about z pitch, about x roll.
        HeadPose yaw = PoseMath.ToOpenTrack(Quat.FromAngleAxis(rad, new Vec3(0, 1, 0)), new Vec3(-500, 0, 0));
        HeadPose pitch = PoseMath.ToOpenTrack(Quat.FromAngleAxis(rad, new Vec3(0, 0, 1)), new Vec3(-500, 0, 0));
        HeadPose roll = PoseMath.ToOpenTrack(Quat.FromAngleAxis(rad, new Vec3(1, 0, 0)), new Vec3(-500, 0, 0));

        Assert.Equal(-degrees, yaw.Yaw, 3);
        Assert.Equal(0, yaw.Pitch, 3);
        Assert.Equal(degrees, pitch.Pitch, 3);
        Assert.Equal(0, pitch.Yaw, 3);
        Assert.Equal(-degrees, roll.Roll, 3);
        Assert.Equal(50.0, yaw.Z, 3);
    }

    [Fact]
    public void QuaternionRotationMatchesTheMatrix()
    {
        var q = Quat.FromAngleAxis(0.7f, new Vec3(0.6f, 0.8f, 0f));
        var v = new Vec3(1, 2, 3);
        Vec3 viaQuat = q.Rotate(v);
        float[,] m = q.ToMatrix();
        Vec3 viaMatrix = new Vec3(m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z, m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z, m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z);

        Assert.Equal(viaMatrix.X, viaQuat.X, 4);
        Assert.Equal(viaMatrix.Y, viaQuat.Y, 4);
        Assert.Equal(viaMatrix.Z, viaQuat.Z, 4);
    }

    [Fact]
    public void TheCoarsestPyramidWidthMatchesOpenTrack()
    {
        Assert.Equal(320, WebcamTrackerWidth(640));
        Assert.Equal(320, WebcamTrackerWidth(1280));
        Assert.Equal(480, WebcamTrackerWidth(1920));
        Assert.Equal(320, WebcamTrackerWidth(320));
    }

    private static int WebcamTrackerWidth(int w)
    {
        // Mirrors WebcamTracker.CoarsestWidth (that file needs ONNX Runtime, so is not linked here).
        while (w >= 640) w /= 2;
        return w;
    }
}
