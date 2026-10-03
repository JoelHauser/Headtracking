using System;
using HeadTracking.Shared;

namespace HeadTracking.Tracking
{
    public enum TrackState
    {
        /// <summary>No live pose since tracking was started.</summary>
        NoData,
        Tracking,
        /// <summary>Lost; still showing the last good position.</summary>
        Holding,
        /// <summary>Lost; easing from the last good position back to centre.</summary>
        Returning,
        /// <summary>Lost and back at centre.</summary>
        Lost,
    }

    /// <summary>Why the newest pose is not usable.</summary>
    public enum LossKind
    {
        None,
        NoPackets,
        Stale,
        Frozen,
        /// <summary>The source said outright that it cannot see a face.</summary>
        NoFace,
    }

    /// <summary>
    /// Everything the tracker reads from the settings. Copied in by the engine each tick, so a
    /// change in the app applies at once. Times are seconds, angles degrees, distances cm.
    /// </summary>
    public sealed class TrackingSettings
    {
        public double YawGain = 2.5;
        public double PitchGain = 2.0;

        /// <summary>Degrees of real head rotation, either side of centre, that do nothing.</summary>
        public double YawDeadZone = 1.5;
        public double PitchDeadZone = 1.5;

        /// <summary>Largest in-game head turn this adds. The game's own limits still apply on top.</summary>
        public double MaxYaw = 40;
        public double MaxPitch = 30;

        /// <summary>1 linear. Above 1, gentle near centre and faster toward the edge.</summary>
        public double YawCurve = 1.0;
        public double PitchCurve = 1.0;

        public bool InvertYaw;
        public bool InvertPitch;

        /// <summary>0 off, 1 heaviest. See <see cref="HeadTracker.MinCutoffFor"/>.</summary>
        public double Smoothing = 0.5;

        /// <summary>0 to 1. How much fast head movement cuts through the smoothing.</summary>
        public double FastMoveResponse = 0.5;

        /// <summary>Lean toward the screen to zoom.</summary>
        public bool ZoomEnabled;

        /// <summary>Leaning in less than this, in cm from centre, does nothing.</summary>
        public double ZoomDeadZone = 2.0;

        /// <summary>Leaning in this far, in cm from centre, is full zoom.</summary>
        public double ZoomFullDistance = 10.0;

        /// <summary>Take the first good pose as centre. Needed for a webcam, whose angles are absolute.</summary>
        public bool AutoCenterOnStart = true;

        public double NoDataTimeout = 0.25;

        /// <summary>0 turns frozen-pose detection off. Only used for sources that do not report validity.</summary>
        public double FrozenTimeout = 0.5;

        public double HoldTime = 0.5;
        public double ReturnTime = 0.75;
        public double RecoveryFade = 0.3;

        public TrackingSettings Clone()
        {
            return (TrackingSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Turns a head pose into the freelook (and zoom) to apply in game, and owns what happens
    /// when tracking is lost. Pure: no clock of its own, so the tests drive it with made-up times.
    /// One thread at a time.
    ///
    /// Per tick: newest pose -> minus centre -> 1-euro smoothing -> dead zone -> gain and curve
    /// -> cap -> game sign convention -> loss handling (hold, ease to centre, cross-fade back).
    /// </summary>
    public sealed class HeadTracker
    {
        /// <summary>
        /// Head pose to game sign. EFT: Player.Look does <c>_horizontal -= mouse X</c>, so positive
        /// yaw is left; its look-down limits read as positive pitch being down. OpenTrack (and the
        /// built-in webcam tracker, which follows OpenTrack's conventions): yaw positive right,
        /// pitch positive up. Inferred, not yet seen in game, hence the invert settings.
        /// </summary>
        public const double YawSign = -1.0;
        public const double PitchSign = -1.0;

        private const double MaxFilterStep = 0.1;
        private const double OutputEpsilon = 1e-4;

        private readonly ILogSink _log;
        private readonly OneEuroFilter _yawFilter = new OneEuroFilter();
        private readonly OneEuroFilter _pitchFilter = new OneEuroFilter();
        private readonly OneEuroFilter _zFilter = new OneEuroFilter();

        private double _lastTick = double.NaN;
        private double _centerYaw, _centerPitch, _centerZ;
        private bool _centered;

        private double _heldYaw, _heldPitch, _heldZoom;
        private double _lossStart, _returnStart;

        private double _fadeFromYaw, _fadeFromPitch, _fadeFromZoom;
        private double _fadeStart = double.NegativeInfinity;

        private bool _recenterRequested;
        private LossKind _loggedIdleLoss = LossKind.None;

        public TrackState State { get; private set; } = TrackState.NoData;
        public LossKind Loss { get; private set; } = LossKind.NoPackets;

        /// <summary>The angle to add in game. Game convention, degrees.</summary>
        public double OutputYaw { get; private set; }
        public double OutputPitch { get; private set; }

        /// <summary>0 none, 1 full.</summary>
        public double OutputZoom { get; private set; }

        // Intermediate values, kept for the UI and the trace log.
        public double RawYaw { get; private set; }
        public double RawPitch { get; private set; }
        public double RawZ { get; private set; }
        public double RelativeYaw { get; private set; }
        public double RelativePitch { get; private set; }
        public double RelativeZ { get; private set; }
        public double SmoothedYaw { get; private set; }
        public double SmoothedPitch { get; private set; }
        public double SmoothedZ { get; private set; }
        public double LiveYaw { get; private set; }
        public double LivePitch { get; private set; }
        public double LiveZoom { get; private set; }
        public double CenterYaw => _centerYaw;
        public double CenterPitch => _centerPitch;
        public double CenterZ => _centerZ;
        public bool IsCentered => _centered;

        /// <summary>When the current state began.</summary>
        public double StateSince { get; private set; }

        public HeadTracker(ILogSink log)
        {
            _log = log;
        }

        public static double MinCutoffFor(double smoothing)
        {
            // 10 Hz (barely smoothed) at 0+, 0.25 Hz (very smooth, slow to settle) at 1.
            return 10.0 * Math.Pow(0.025, Clamp01(smoothing));
        }

        public static double BetaFor(double fastMoveResponse)
        {
            double r = Clamp01(fastMoveResponse);
            return 0.15 * r * r;
        }

        /// <summary>Make the current head position the new centre at the next <see cref="Tick"/>.</summary>
        public void RequestRecenter()
        {
            _recenterRequested = true;
        }

        /// <summary>
        /// Start over: no centre, no history, back to <see cref="TrackState.NoData"/>. For a change
        /// of source, whose angles mean something else.
        /// </summary>
        public void Reset()
        {
            _centered = false;
            _centerYaw = _centerPitch = _centerZ = 0;
            _yawFilter.Unprime();
            _pitchFilter.Unprime();
            _zFilter.Unprime();
            OutputYaw = OutputPitch = OutputZoom = 0;
            _loggedIdleLoss = LossKind.None;
            State = TrackState.NoData;
            StateSince = double.IsNaN(_lastTick) ? 0 : _lastTick;
        }

        /// <remarks>Snapshot by value: scripts\check-opentrack.ps1 calls this from PowerShell.</remarks>
        public void Tick(bool hasSnapshot, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            double dt = 0;
            if (double.IsNaN(_lastTick))
            {
                StateSince = now;
            }
            else
            {
                dt = now - _lastTick;
            }

            _lastTick = now;
            if (dt < 0)
            {
                dt = 0;
            }

            LossKind loss = Classify(hasSnapshot, snapshot, now, s);
            Loss = loss;
            bool live = loss == LossKind.None;

            if (live && !_centered && s.AutoCenterOnStart)
            {
                SetCenter(snapshot.Pose, "Centred automatically on the first good pose");
            }

            if (live)
            {
                ComputeLive(snapshot.Pose, Math.Min(dt, MaxFilterStep), s);
            }
            else
            {
                // Next time tracking comes back the filters start from that pose, rather than
                // gliding over from wherever the head was before the loss.
                _yawFilter.Unprime();
                _pitchFilter.Unprime();
                _zFilter.Unprime();
            }

            if (_recenterRequested)
            {
                _recenterRequested = false;
                Recenter(live, snapshot, now, s);
            }

            Step(live, loss, snapshot, now, s);
        }

        private static LossKind Classify(bool hasSnapshot, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            if (!hasSnapshot || !snapshot.HasPose)
            {
                return LossKind.NoPackets;
            }

            if (now - snapshot.ArrivalTime > s.NoDataTimeout)
            {
                return LossKind.Stale;
            }

            if (snapshot.ReportsValidity)
            {
                return snapshot.Valid ? LossKind.None : LossKind.NoFace;
            }

            if (s.FrozenTimeout > 0 && now - snapshot.LastChangeTime >= s.FrozenTimeout)
            {
                return LossKind.Frozen;
            }

            return LossKind.None;
        }

        private void ComputeLive(in Pose pose, double dt, TrackingSettings s)
        {
            RawYaw = pose.Yaw;
            RawPitch = pose.Pitch;
            RawZ = pose.Z;
            RelativeYaw = Wrap180(pose.Yaw - _centerYaw);
            RelativePitch = Wrap180(pose.Pitch - _centerPitch);
            RelativeZ = pose.Z - _centerZ;

            if (s.Smoothing > 0)
            {
                double minCutoff = MinCutoffFor(s.Smoothing);
                double beta = BetaFor(s.FastMoveResponse);
                SmoothedYaw = _yawFilter.Filter(RelativeYaw, dt, minCutoff, beta);
                SmoothedPitch = _pitchFilter.Filter(RelativePitch, dt, minCutoff, beta);
                SmoothedZ = _zFilter.Filter(RelativeZ, dt, minCutoff, beta);
            }
            else
            {
                _yawFilter.Reset(RelativeYaw);
                _pitchFilter.Reset(RelativePitch);
                _zFilter.Reset(RelativeZ);
                SmoothedYaw = RelativeYaw;
                SmoothedPitch = RelativePitch;
                SmoothedZ = RelativeZ;
            }

            LiveYaw = YawSign * (s.InvertYaw ? -1 : 1) * Shape(SmoothedYaw, s.YawDeadZone, s.YawGain, s.MaxYaw, s.YawCurve);
            LivePitch = PitchSign * (s.InvertPitch ? -1 : 1) * Shape(SmoothedPitch, s.PitchDeadZone, s.PitchGain, s.MaxPitch, s.PitchCurve);

            // Z is distance from the camera: leaning in makes it smaller.
            LiveZoom = s.ZoomEnabled ? ZoomShape(-SmoothedZ, s.ZoomDeadZone, s.ZoomFullDistance) : 0;
        }

        /// <summary>
        /// Dead zone, then gain and curve, then the cap. The dead zone is subtracted rather than
        /// gated, so the output starts from zero at its edge instead of jumping. Inside it the
        /// result is exactly zero, which matters in game: EFT skips its hand-recoil camera motion
        /// whenever the head rotation is anything but exactly zero.
        ///
        /// The curve bends the response without moving its end: with gain g and cap m, the head
        /// angle (beyond the dead zone) that reaches the cap is m/g whatever the curve; a curve
        /// above 1 gives less in the middle of that range, below 1 more.
        /// </summary>
        public static double Shape(double degrees, double deadZone, double gain, double max, double curve = 1.0)
        {
            double beyond = Math.Abs(degrees) - Math.Max(0, deadZone);
            if (beyond <= 0 || max <= 0 || gain <= 0)
            {
                return 0;
            }

            double t = beyond * gain / max;
            double output = t >= 1 ? max : max * (Math.Abs(curve - 1.0) < 1e-9 ? t : Math.Pow(t, Math.Max(0.1, curve)));
            return degrees < 0 ? -output : output;
        }

        /// <summary>Lean (cm toward the screen) to zoom 0..1, linear between the dead zone and full.</summary>
        public static double ZoomShape(double leanIn, double deadZone, double fullDistance)
        {
            double beyond = leanIn - Math.Max(0, deadZone);
            if (beyond <= 0)
            {
                return 0;
            }

            double range = Math.Max(0.1, fullDistance - Math.Max(0, deadZone));
            return Math.Min(1.0, beyond / range);
        }

        private void SetCenter(in Pose pose, string why)
        {
            double oldYaw = _centerYaw, oldPitch = _centerPitch, oldZ = _centerZ;
            _centerYaw = pose.Yaw;
            _centerPitch = pose.Pitch;
            _centerZ = pose.Z;
            _centered = true;
            _log.Log(LogLevel.Info, why + ": centre was yaw " + Deg(oldYaw) + " pitch " + Deg(oldPitch) + " distance " + Cm(oldZ)
                                    + ", now yaw " + Deg(_centerYaw) + " pitch " + Deg(_centerPitch) + " distance " + Cm(_centerZ) + ".");
        }

        private void Recenter(bool live, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            if (!live)
            {
                _log.Log(LogLevel.Warning, "Recenter ignored: no live tracking right now (" + Describe(Loss) + ").");
                return;
            }

            SetCenter(snapshot.Pose, "Recentred");

            // Re-run this frame's maths against the new centre, then glide there.
            _yawFilter.Unprime();
            _pitchFilter.Unprime();
            _zFilter.Unprime();
            ComputeLive(snapshot.Pose, 0, s);
            StartFade(now);
        }

        private void Step(bool live, LossKind loss, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            switch (State)
            {
                case TrackState.Tracking:
                    if (!live)
                    {
                        _heldYaw = OutputYaw;
                        _heldPitch = OutputPitch;
                        _heldZoom = OutputZoom;
                        _lossStart = now;
                        Enter(TrackState.Holding, now);
                        _log.Log(LogLevel.Warning, "Tracking lost: " + Describe(loss, snapshot, now)
                                                   + ". Holding yaw " + Deg(_heldYaw) + " pitch " + Deg(_heldPitch)
                                                   + " for " + Ms(s.HoldTime) + ", then returning to centre over " + Ms(s.ReturnTime) + ".");
                    }

                    break;

                default:
                    if (live)
                    {
                        Recover(now);
                    }

                    break;
            }

            if (State == TrackState.Holding && now - _lossStart >= s.HoldTime)
            {
                _returnStart = now;
                Enter(TrackState.Returning, now);
                _log.Log(LogLevel.Info, "Still lost after holding " + Ms(s.HoldTime) + "; easing back to centre over " + Ms(s.ReturnTime) + ".");
            }

            if (State == TrackState.Returning && (s.ReturnTime <= 0 || now - _returnStart >= s.ReturnTime))
            {
                Enter(TrackState.Lost, now);
                _log.Log(LogLevel.Info, "Back at centre. Waiting for tracking (" + Describe(loss) + ").");
                _loggedIdleLoss = loss;
            }

            if ((State == TrackState.Lost || State == TrackState.NoData) && loss != _loggedIdleLoss)
            {
                // Only kind changes are logged here, not every tick.
                _loggedIdleLoss = loss;
                if (loss != LossKind.None)
                {
                    _log.Log(LogLevel.Info, "Waiting for tracking: " + Describe(loss, snapshot, now) + ".");
                }
            }

            double yaw, pitch, zoom;
            switch (State)
            {
                case TrackState.Tracking:
                    yaw = LiveYaw;
                    pitch = LivePitch;
                    zoom = LiveZoom;
                    double sinceFade = now - _fadeStart;
                    if (s.RecoveryFade > 0 && sinceFade < s.RecoveryFade)
                    {
                        double t = SmoothStep(sinceFade / s.RecoveryFade);
                        yaw = Lerp(_fadeFromYaw, yaw, t);
                        pitch = Lerp(_fadeFromPitch, pitch, t);
                        zoom = Lerp(_fadeFromZoom, zoom, t);
                    }

                    break;

                case TrackState.Holding:
                    yaw = _heldYaw;
                    pitch = _heldPitch;
                    zoom = _heldZoom;
                    break;

                case TrackState.Returning:
                    double k = 1.0 - SmoothStep((now - _returnStart) / s.ReturnTime);
                    yaw = _heldYaw * k;
                    pitch = _heldPitch * k;
                    zoom = _heldZoom * k;
                    break;

                default:
                    yaw = pitch = zoom = 0;
                    break;
            }

            OutputYaw = Math.Abs(yaw) < OutputEpsilon ? 0 : yaw;
            OutputPitch = Math.Abs(pitch) < OutputEpsilon ? 0 : pitch;
            OutputZoom = zoom < OutputEpsilon ? 0 : zoom;
        }

        private void Recover(double now)
        {
            TrackState from = State;
            double lostFor = now - _lossStart;
            StartFade(now);
            Enter(TrackState.Tracking, now);
            _loggedIdleLoss = LossKind.None;

            if (from == TrackState.NoData)
            {
                _log.Log(LogLevel.Info, "Tracking started. Head yaw " + Deg(RawYaw) + " pitch " + Deg(RawPitch) + " distance " + Cm(RawZ)
                                        + " (centre yaw " + Deg(_centerYaw) + " pitch " + Deg(_centerPitch) + ").");
            }
            else
            {
                _log.Log(LogLevel.Info, "Tracking recovered after " + Ms(lostFor) + " (was " + from + "). Fading back in from yaw "
                                        + Deg(_fadeFromYaw) + " pitch " + Deg(_fadeFromPitch) + ".");
            }
        }

        private void StartFade(double now)
        {
            _fadeFromYaw = OutputYaw;
            _fadeFromPitch = OutputPitch;
            _fadeFromZoom = OutputZoom;
            _fadeStart = now;
        }

        private void Enter(TrackState state, double now)
        {
            State = state;
            StateSince = now;
        }

        public static string Describe(LossKind loss)
        {
            switch (loss)
            {
                case LossKind.NoPackets: return "no head data yet";
                case LossKind.Stale: return "the tracker stopped sending";
                case LossKind.Frozen: return "the tracker is sending but the pose is frozen";
                case LossKind.NoFace: return "no face in view";
                default: return "tracking";
            }
        }

        private static string Describe(LossKind loss, PoseSnapshot snapshot, double now)
        {
            switch (loss)
            {
                case LossKind.Stale:
                    return "no head data for " + Ms(now - snapshot.ArrivalTime) + " (camera stopped, or OpenTrack stopped or closed)";
                case LossKind.Frozen:
                    return "data still arrives but the pose has not changed for " + Ms(now - snapshot.LastChangeTime)
                           + " (face not found, camera covered, or tracking paused in OpenTrack; if your face IS visible, "
                           + "OpenTrack's filter dead zone is holding the pose still, so set its dead zones to 0 or raise 'Frozen pose timeout')";
                case LossKind.NoFace:
                    return "no face found in the camera image";
                default:
                    return Describe(loss);
            }
        }

        public static string Deg(double v)
        {
            return v.ToString("+0.0;-0.0;0.0");
        }

        public static string Cm(double v)
        {
            return v.ToString("0.0") + " cm";
        }

        public static string Ms(double seconds)
        {
            return ((int)Math.Round(seconds * 1000)) + " ms";
        }

        public static double Wrap180(double degrees)
        {
            degrees %= 360.0;
            if (degrees > 180.0) degrees -= 360.0;
            if (degrees < -180.0) degrees += 360.0;
            return degrees;
        }

        public static double SmoothStep(double t)
        {
            t = Clamp01(t);
            return t * t * (3 - 2 * t);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private static double Clamp01(double v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }
}
