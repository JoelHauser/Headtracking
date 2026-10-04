using System;
namespace HeadTracking.App.Webcam
{
    public sealed class WebcamTrackerOptions
    {
        /// <summary>The camera's diagonal field of view in degrees.</summary>
        public float CameraFov = 70f;

        /// <summary>Face detector score (0..1) above which a face counts as found. OpenTrack: 0.5.</summary>
        public float DetectionThreshold = 0.5f;

        /// <summary>
        /// How long to keep trusting the pose network while the face detector is unsure. OpenTrack
        /// trusts it indefinitely, which keeps "tracking" whatever is left in the box after you walk
        /// away; a limit turns that into a clean "face lost".
        /// </summary>
        public double UncertainTrustSeconds = 1.0;
    }

    public struct TrackResult
    {
        public bool Valid;
        public HeadPose Pose;
        public bool RanLocalizer;
        public float LocalizerScore;
        public RectF? FaceBox;
        public double LocalizerMs;
        public double PoseMs;
        public string LossReason;

        /// <summary>The network's rotation uncertainty for this frame, degrees (0 if unknown).</summary>
        public double RotationSigma;
    }

    /// <summary>
    /// One camera frame in, one head pose (or "no face") out. The region-of-interest logic of
    /// OpenTrack's NeuralNetTracker::detect(): the face detector only runs when there is no
    /// region yet, or the pose network's box has drifted away from where the detector last put
    /// it; otherwise the pose network's own box carries the region from frame to frame.
    ///
    /// Works in the frame's own pixels throughout. OpenTrack works on the coarsest level of an
    /// image pyramid (320-640 wide); the geometry is resolution independent, and the one pixel
    /// threshold it uses (a box under 16x16 is no face) is scaled to match.
    /// </summary>
    public sealed class WebcamTracker
    {
        private const float MatchIoU = 0.25f;
        private const float TinyBoxPixels = 16f;

        private readonly Localizer _localizer;
        private readonly PoseEstimator _poseEstimator;

        private RectF? _lastRoi;
        private RectF? _lastLocalizerRoi;
        private double _uncertainSince = double.NaN;
        private CamIntrinsics _intrinsics;
        private int _intrinsicsW, _intrinsicsH;
        private float _intrinsicsFov;

        public WebcamTracker(Localizer localizer, PoseEstimator poseEstimator)
        {
            _localizer = localizer;
            _poseEstimator = poseEstimator;
        }

        public PoseEstimator PoseEstimator => _poseEstimator;

        public void Reset()
        {
            _lastRoi = null;
            _lastLocalizerRoi = null;
            _uncertainSince = double.NaN;
        }

        public TrackResult Process(GrayImage frame, double now, WebcamTrackerOptions options)
        {
            TrackResult result = default;
            float referenceScale = CoarsestWidth(frame.Width) / (float)frame.Width;

            if (_lastLocalizerRoi == null || _lastRoi == null || RectF.IoU(_lastLocalizerRoi.Value, _lastRoi.Value) < MatchIoU)
            {
                float score = _localizer.Run(frame, out RectF rect);
                result.RanLocalizer = true;
                result.LocalizerMs = _localizer.LastMilliseconds;
                if (rect.Width * referenceScale < TinyBoxPixels && rect.Height * referenceScale < TinyBoxPixels)
                {
                    score = 0f;
                }

                result.LocalizerScore = score;
                bool found = score > options.DetectionThreshold;

                if (_lastRoi != null && RectF.IoU(rect, _lastRoi.Value) >= MatchIoU && found)
                {
                    // The detector agrees with where tracking has the head: the user merely moved.
                    _lastLocalizerRoi = rect;
                    _uncertainSince = double.NaN;
                }
                else if (found)
                {
                    // The detector sees a face somewhere else: jump there.
                    _lastLocalizerRoi = rect;
                    _lastRoi = rect;
                    _uncertainSince = double.NaN;
                }
                else if (_lastRoi != null)
                {
                    // The detector is unsure but tracking has a region. OpenTrack keeps trusting
                    // the pose network here (the detector is weak when you sit far back); this
                    // does too, for a limited time.
                    _lastLocalizerRoi = null;
                    if (double.IsNaN(_uncertainSince))
                    {
                        _uncertainSince = now;
                    }
                    else if (now - _uncertainSince > options.UncertainTrustSeconds)
                    {
                        _lastRoi = null;
                        _uncertainSince = double.NaN;
                        result.LossReason = "the face detector has not seen a face for " + (int)(options.UncertainTrustSeconds * 1000) + " ms";
                    }
                }
                else
                {
                    _lastRoi = null;
                    _lastLocalizerRoi = null;
                }
            }

            if (_lastRoi == null)
            {
                result.LossReason = result.LossReason ?? "no face found (detector score " + result.LocalizerScore.ToString("0.00") + ")";
                return result;
            }

            Face? face = _poseEstimator.Run(frame, _lastRoi.Value);
            result.PoseMs = _poseEstimator.LastMilliseconds;
            if (face == null)
            {
                _lastRoi = null;
                result.LossReason = "the pose network could not use the face region";
                return result;
            }

            Face f = face.Value;
            _lastRoi = f.Box;
            result.FaceBox = f.Box;

            CamIntrinsics intrinsics = Intrinsics(frame.Width, frame.Height, options.CameraFov);
            PoseMath.TransformToWorld(f.Rotation, f.CenterX, f.CenterY, f.Size, frame.Width, frame.Height, intrinsics, out Quat rotation, out Vec3 position);
            HeadPose pose = PoseMath.ToOpenTrack(rotation, position);

            if (double.IsNaN(pose.Yaw) || double.IsNaN(pose.Pitch) || double.IsNaN(pose.Z) || f.Size <= 0)
            {
                _lastRoi = null;
                result.LossReason = "the pose network returned an invalid pose";
                return result;
            }

            result.Valid = true;
            result.Pose = pose;
            result.RotationSigma = f.RotationSigmaDegrees;
            return result;
        }

        /// <summary>The width of the coarsest level of OpenTrack's pyramid: halved while 640 or wider.</summary>
        public static int CoarsestWidth(int width)
        {
            while (width >= 640)
            {
                width /= 2;
            }

            return width;
        }

        private CamIntrinsics Intrinsics(int width, int height, float fov)
        {
            if (width != _intrinsicsW || height != _intrinsicsH || Math.Abs(fov - _intrinsicsFov) > 1e-3f)
            {
                _intrinsics = CamIntrinsics.FromDiagonalFov(width / (float)height, fov);
                _intrinsicsW = width;
                _intrinsicsH = height;
                _intrinsicsFov = fov;
            }

            return _intrinsics;
        }
    }
}
