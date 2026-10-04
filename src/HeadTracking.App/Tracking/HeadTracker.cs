using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Everything the tracker reads from the settings. Copied in by the engine, so a change in the
    /// app applies at once. Times are seconds, angles degrees.
    /// </summary>
    public sealed class TrackingSettings
    {
        // The defaults here are the straight 0.4.0 response, kept as the baseline that tests and the
        // dev tools (--replay, --jitter-test, --live-jitter) measure against. What ships is
        // AppSettings' (0.5.0: gain 2.0/1.6, curve 1.5), copied in by the engine.
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

        /// <summary>
        /// Uncertainty-scaled soft dead zone on each new pose, in multiples of the network's own
        /// per-frame uncertainty (OpenTrack's deadzone_size). 0 is off. Only for sources that
        /// report an uncertainty (the built-in webcam tracker).
        /// </summary>
        public double Steadiness = 1.0;

        /// <summary>0 off, 1 heaviest. See <see cref="HeadTracker.MinCutoffFor"/>.</summary>
        public double Smoothing = 0;

        /// <summary>
        /// Stillness lock strength, as a multiple of the automatic band (1.5 x the tracker noise
        /// measured as it runs). 0 is off. Head movement smaller than the band, from where the view
        /// last settled, does not move it; bigger movement is followed at once (trailing by the
        /// band). A still head then gives a still view, which no linear smoothing achieves alone.
        /// </summary>
        public double Stillness = 1.0;

        /// <summary>A fixed band in head degrees instead of the automatic one (tests, replays). 0: automatic.</summary>
        public double StillnessBand;

        /// <summary>
        /// Seconds over which a held view creeps to where the head really is, so the lock never
        /// leaves it off by the band. 0: no creep.
        /// Off since 0.5.0: it chased the noisy reading every camera frame, so a held view still
        /// moved by slivers, and DLSS/TAA kept the picture soft ("like motion blur"). Replay: the
        /// view was exactly still in 20% of resting frames with it, 74% without, same lag.
        /// </summary>
        public double StillnessCreep = 0;

        /// <summary>0 to 1. How much fast head movement cuts through the smoothing.</summary>
        public double FastMoveResponse = 0.5;

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
    /// Turns a head pose into the freelook to apply in game, and owns what happens when tracking
    /// is lost. Pure: no clock of its own, so the tests drive it with made-up times. One thread.
    ///
    /// Per new pose (once per camera frame, not per tick: filters need the real time between
    /// samples, and running them on a held value makes every new frame look like a jerk):
    ///   minus centre -> steadiness (uncertainty-scaled soft dead zone) -> 1-euro smoothing ->
    ///   dead zone -> gain and curve -> cap -> game sign convention.
    /// Per tick: loss handling (hold, ease to centre, cross-fade back), which is time based.
    /// The plugin then follows the result smoothly at the game's frame rate.
    /// </summary>
    public sealed class HeadTracker
    {
        /// <summary>
        /// Head pose to game sign. EFT: Player.Look does <c>_horizontal -= mouse X</c>, so positive
        /// yaw is left; its look-down limits read as positive pitch being down. The trackers
        /// (OpenTrack's convention): yaw positive = head turned to the user's right (shown on still
        /// portraits), pitch positive up. Hence both negated; the invert settings flip each.
        /// </summary>
        public const double YawSign = -1.0;
        public const double PitchSign = -1.0;

        /// <summary>OpenTrack's deadzone_hardness default.</summary>
        public const double SteadinessHardness = 1.5;

        private const double MaxSampleStep = 0.25;
        private const double OutputEpsilon = 1e-4;

        private readonly ILogSink _log;
        private readonly OneEuroFilter _yawFilter = new OneEuroFilter();
        private readonly OneEuroFilter _pitchFilter = new OneEuroFilter();

        private double _lastTick = double.NaN;
        private double _lastSample = double.NaN;
        private double _centerYaw, _centerPitch, _centerZ;
        private bool _centered;

        private bool _steadyPrimed;
        private double _steadyYaw, _steadyPitch;
        private bool _stillPrimed;
        private double _stillYaw, _stillPitch;

        // Noise estimate: frame-to-frame changes of the raw angle over the last ~3 s.
        private readonly double[] _steps = new double[90];
        private int _stepCount, _stepNext;
        private double _lastRawYaw = double.NaN, _lastRawPitch;
        private double _noise = 0.5;
        private int _samplesSinceNoise;

        private double _heldYaw, _heldPitch;
        private double _lossStart, _returnStart;

        private double _fadeFromYaw, _fadeFromPitch;
        private double _fadeStart = double.NegativeInfinity;

        private bool _recenterRequested;
        private LossKind _loggedIdleLoss = LossKind.None;

        // Centring: the last ~1.5 s of raw poses, so a centre is the median of many frames rather
        // than one frame's jitter (0.3-0.6 deg per frame on a webcam).
        private struct RawSample
        {
            public double T, Yaw, Pitch, Z;
        }

        private readonly RawSample[] _recent = new RawSample[64];
        private int _recentCount, _recentNext;
        private bool _centerByUser, _autoRecenterPending;
        private double _autoRecenterUntil, _liveSince = double.NaN;

        /// <summary>A recentre averages this much of the newest tracking.</summary>
        public const double RecenterWindow = 0.5;
        /// <summary>An automatic centre waits for the head to be this steady for a second.</summary>
        public const double SteadySeconds = 1.0, SteadySpread = 4.0;
        /// <summary>...but no longer than this; then it takes the last second as it is.</summary>
        public const double SteadyGiveUpSeconds = 5.0;

        public TrackState State { get; private set; } = TrackState.NoData;
        public LossKind Loss { get; private set; } = LossKind.NoPackets;

        /// <summary>The angle to add in game. Game convention, degrees.</summary>
        public double OutputYaw { get; private set; }
        public double OutputPitch { get; private set; }

        // Intermediate values, kept for the UI and the log.
        public double RawYaw { get; private set; }
        public double RawPitch { get; private set; }
        public double RawZ { get; private set; }
        public double RelativeYaw { get; private set; }
        public double RelativePitch { get; private set; }
        public double SteadyYaw => _steadyYaw;
        public double SteadyPitch => _steadyPitch;
        public double SmoothedYaw { get; private set; }
        public double SmoothedPitch { get; private set; }

        /// <summary>
        /// The tracker's noise, estimated as it runs: the one-sigma frame-to-frame jitter of the raw
        /// angle, in head degrees, from the 30th percentile of recent 2-D steps (so real movement,
        /// which is rarer, hardly counts). Starts at 0.5.
        /// </summary>
        public double NoiseEstimate => _noise;

        /// <summary>The stillness band in use, head degrees.</summary>
        public double StillnessBandInUse { get; private set; }

        /// <summary>After the stillness lock: the head angle the view is shaped from.</summary>
        public double SettledYaw => _stillYaw;
        public double SettledPitch => _stillPitch;
        public double LiveYaw { get; private set; }
        public double LivePitch { get; private set; }
        public double LastSigma { get; private set; }
        public double LastSteadyAttenuation { get; private set; } = 1;
        public long Samples { get; private set; }
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

        /// <summary>
        /// OpenTrack's attenuation (deadzone_filter.cpp, apply_filter_to_offset): an offset of
        /// <paramref name="sigmas"/> standard deviations is let through by this fraction. Soft, so
        /// a held head still converges, just without the frame-to-frame shake.
        /// </summary>
        public static double SteadyAttenuation(double sigmas, double size)
        {
            return 1.0 / (1.0 + Math.Exp(-(sigmas - size) * SteadinessHardness));
        }

        /// <summary>Make the current head position the new centre at the next <see cref="Tick"/>.</summary>
        public void RequestRecenter()
        {
            _recenterRequested = true;
        }

        /// <summary>
        /// A raid is starting: if the centre was only ever set automatically (when the app started,
        /// perhaps while looking at another screen), centre again on the first steady second in the
        /// next 10 s. A centre the user set (recenter key or button) is never replaced.
        /// </summary>
        public void RequestAutoRecenter(double now)
        {
            if (!_centerByUser)
            {
                _autoRecenterPending = true;
                _autoRecenterUntil = now + 10;
            }
        }

        /// <summary>True when the user set the centre (recenter key or button) rather than the app.</summary>
        public bool CenterSetByUser => _centerByUser;

        /// <summary>Start over: no centre, no history. For a change of source.</summary>
        public void Reset()
        {
            _centered = false;
            _centerByUser = false;
            _autoRecenterPending = false;
            _recentCount = _recentNext = 0;
            _liveSince = double.NaN;
            _centerYaw = _centerPitch = _centerZ = 0;
            Unprime();
            OutputYaw = OutputPitch = 0;
            _loggedIdleLoss = LossKind.None;
            State = TrackState.NoData;
            StateSince = double.IsNaN(_lastTick) ? 0 : _lastTick;
        }

        /// <remarks>Snapshot by value: called from PowerShell by test scripts.</remarks>
        public void Tick(bool hasSnapshot, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            if (double.IsNaN(_lastTick))
            {
                StateSince = now;
            }

            _lastTick = now;

            LossKind loss = Classify(hasSnapshot, snapshot, now, s);
            Loss = loss;
            bool live = loss == LossKind.None;

            if (live)
            {
                if (double.IsNaN(_liveSince))
                {
                    _liveSince = now;
                }

                if (snapshot.LastChangeTime != _lastRemembered)
                {
                    _lastRemembered = snapshot.LastChangeTime;
                    Remember(snapshot.Pose, snapshot.LastChangeTime);
                }
            }
            else
            {
                _liveSince = double.NaN;
            }

            if (live && s.AutoCenterOnStart && (!_centered || _autoRecenterPending))
            {
                AutoCenter(now, s);
            }

            if (_autoRecenterPending && now > _autoRecenterUntil)
            {
                _autoRecenterPending = false;
            }

            // An automatic centre waits for a steady second; until then nothing goes to the game,
            // rather than angles measured from no centre at all. A recenter still works meanwhile.
            bool tracking = live;
            if (live && s.AutoCenterOnStart && !_centered)
            {
                live = false;
            }

            if (live)
            {
                // A new sample is a pose the source has not given before. OpenTrack resends the
                // same pose between camera frames; the webcam source publishes once per frame.
                if (snapshot.LastChangeTime != _lastSample)
                {
                    double dt = double.IsNaN(_lastSample) ? 0 : snapshot.LastChangeTime - _lastSample;
                    _lastSample = snapshot.LastChangeTime;
                    ComputeLive(snapshot.Pose, snapshot.RotationSigma, Clamp(dt, 0, MaxSampleStep), s);
                }
            }
            else
            {
                // When tracking comes back the filters start from that pose, rather than gliding
                // over from wherever the head was before the loss.
                Unprime();
            }

            if (_recenterRequested)
            {
                _recenterRequested = false;
                Recenter(tracking, snapshot, now, s);
            }

            Step(live, loss, snapshot, now, s);
        }

        private void Unprime()
        {
            _yawFilter.Unprime();
            _pitchFilter.Unprime();
            _steadyPrimed = false;
            _stillPrimed = false;
            _lastSample = double.NaN;
            _lastRawYaw = double.NaN;
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

        private void ComputeLive(in Pose pose, double sigma, double dt, TrackingSettings s)
        {
            Samples++;
            RawYaw = pose.Yaw;
            RawPitch = pose.Pitch;
            RawZ = pose.Z;
            RelativeYaw = Wrap180(pose.Yaw - _centerYaw);
            RelativePitch = Wrap180(pose.Pitch - _centerPitch);
            LastSigma = sigma;
            EstimateNoise(pose.Yaw, pose.Pitch);

            // Steadiness: move toward the new pose by a fraction that depends on how big the step
            // is compared with the network's own uncertainty for this frame.
            if (_steadyPrimed && s.Steadiness > 0 && sigma > 0)
            {
                double dy = RelativeYaw - _steadyYaw, dp = RelativePitch - _steadyPitch;
                double a = SteadyAttenuation(Math.Sqrt(dy * dy + dp * dp) / sigma, s.Steadiness);
                LastSteadyAttenuation = a;
                _steadyYaw += dy * a;
                _steadyPitch += dp * a;
            }
            else
            {
                LastSteadyAttenuation = 1;
                _steadyYaw = RelativeYaw;
                _steadyPitch = RelativePitch;
                _steadyPrimed = true;
            }

            if (s.Smoothing > 0)
            {
                double minCutoff = MinCutoffFor(s.Smoothing);
                double beta = BetaFor(s.FastMoveResponse);
                SmoothedYaw = _yawFilter.Filter(_steadyYaw, dt, minCutoff, beta);
                SmoothedPitch = _pitchFilter.Filter(_steadyPitch, dt, minCutoff, beta);
            }
            else
            {
                _yawFilter.Reset(_steadyYaw);
                _pitchFilter.Reset(_steadyPitch);
                SmoothedYaw = _steadyYaw;
                SmoothedPitch = _steadyPitch;
            }

            Settle(dt, s);

            LiveYaw = YawSign * (s.InvertYaw ? -1 : 1) * Shape(_stillYaw, s.YawDeadZone, s.YawGain, s.MaxYaw, s.YawCurve);
            LivePitch = PitchSign * (s.InvertPitch ? -1 : 1) * Shape(_stillPitch, s.PitchDeadZone, s.PitchGain, s.MaxPitch, s.PitchCurve);
        }

        /// <summary>
        /// The stillness lock: a radial backlash on the smoothed head angle. Inside the band the view
        /// holds (and only creeps toward the head over <see cref="TrackingSettings.StillnessCreep"/>);
        /// beyond it the view follows, trailing by the band.
        /// </summary>
        private void EstimateNoise(double yaw, double pitch)
        {
            if (!double.IsNaN(_lastRawYaw))
            {
                double dy = Wrap180(yaw - _lastRawYaw), dp = pitch - _lastRawPitch;
                _steps[_stepNext] = Math.Sqrt(dy * dy + dp * dp);
                _stepNext = (_stepNext + 1) % _steps.Length;
                _stepCount = Math.Min(_stepCount + 1, _steps.Length);
                if (++_samplesSinceNoise >= 15 && _stepCount >= 30)
                {
                    _samplesSinceNoise = 0;
                    double[] sorted = new double[_stepCount];
                    Array.Copy(_steps, sorted, _stepCount);
                    Array.Sort(sorted);
                    // 2-D steps of white noise with per-axis sigma s are Rayleigh with scale s*sqrt(2);
                    // its 30th percentile is 0.845 * s * sqrt(2) = 1.195 s.
                    double estimate = Clamp(sorted[(int)(0.3 * sorted.Length)] / 1.195, 0.05, 3.0);
                    _noise = _noise + 0.3 * (estimate - _noise);
                }
            }

            _lastRawYaw = yaw;
            _lastRawPitch = pitch;
        }

        private void Settle(double dt, TrackingSettings s)
        {
            double band = s.StillnessBand > 0 ? s.StillnessBand : s.Stillness * 1.5 * _noise;
            StillnessBandInUse = band;
            if (!_stillPrimed || band <= 0)
            {
                _stillYaw = SmoothedYaw;
                _stillPitch = SmoothedPitch;
                _stillPrimed = true;
                return;
            }

            double ey = SmoothedYaw - _stillYaw, ep = SmoothedPitch - _stillPitch;
            double e = Math.Sqrt(ey * ey + ep * ep);
            if (e > band)
            {
                double k = (e - band) / e;
                _stillYaw += ey * k;
                _stillPitch += ep * k;
            }
            else if (s.StillnessCreep > 0 && dt > 0)
            {
                double c = 1.0 - Math.Exp(-dt / s.StillnessCreep);
                _stillYaw += ey * c;
                _stillPitch += ep * c;
            }
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

        private double _lastRemembered = double.NaN;

        private void Remember(in Pose pose, double t)
        {
            _recent[_recentNext] = new RawSample { T = t, Yaw = pose.Yaw, Pitch = pose.Pitch, Z = pose.Z };
            _recentNext = (_recentNext + 1) % _recent.Length;
            if (_recentCount < _recent.Length)
            {
                _recentCount++;
            }
        }

        /// <summary>
        /// The median pose over the newest <paramref name="window"/> seconds, if there are enough
        /// frames and the head stayed within <paramref name="maxSpread"/> degrees. Yaw is taken
        /// relative to the newest frame, so a head near +/-180 does not average to 0.
        /// </summary>
        private bool TryRecentPose(double now, double window, double maxSpread, out Pose pose, out int count)
        {
            pose = default;
            count = 0;
            if (_recentCount == 0)
            {
                return false;
            }

            RawSample newest = _recent[(_recentNext - 1 + _recent.Length) % _recent.Length];
            double oldest = newest.T;
            var yaws = new List<double>();
            var pitches = new List<double>();
            var zs = new List<double>();
            for (int i = 0; i < _recentCount; i++)
            {
                RawSample r = _recent[(_recentNext - 1 - i + 2 * _recent.Length) % _recent.Length];
                if (newest.T - r.T > window + 1e-9)
                {
                    break;
                }

                yaws.Add(Wrap180(r.Yaw - newest.Yaw));
                pitches.Add(r.Pitch);
                zs.Add(r.Z);
                oldest = r.T;
            }

            // Enough frames, spread over most of the window (not a burst), and current.
            count = yaws.Count;
            if (count < Math.Max(3, (int)(window * 12)) || newest.T - oldest < 0.8 * window || newest.T < now - 0.25)
            {
                return false;
            }

            if (yaws.Max() - yaws.Min() > maxSpread || pitches.Max() - pitches.Min() > maxSpread)
            {
                return false;
            }

            pose = new Pose(0, 0, Median(zs), Wrap180(newest.Yaw + Median(yaws)), Median(pitches), 0);
            return true;
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
        }

        /// <summary>
        /// The automatic centre: on the first second the head holds steady (looking at the screen,
        /// not glancing around), as the median of that second. If the head never settles for 5 s,
        /// the last second is taken as it is.
        /// </summary>
        private void AutoCenter(double now, TrackingSettings s)
        {
            bool steady = TryRecentPose(now, SteadySeconds, SteadySpread, out Pose pose, out int n);
            bool giveUp = !steady && !_centered && now - _liveSince >= SteadyGiveUpSeconds
                          && TryRecentPose(now, SteadySeconds, 360, out pose, out n);
            if (!steady && !giveUp)
            {
                return;
            }

            bool again = _centered;
            _autoRecenterPending = false;
            SetCenter(pose, (again ? "Centred again automatically at the start of the raid" : "Centred automatically")
                            + " on " + (steady ? "a steady second (median of " + n + " frames)" : "the last second (the head never settled for 5 s)"));
            if (again)
            {
                // The view glides to the new centre, as after a recenter.
                Unprime();
                StartFade(now);
            }
        }

        private void SetCenter(in Pose pose, string why)
        {
            double oldYaw = _centerYaw, oldPitch = _centerPitch;
            _centerYaw = pose.Yaw;
            _centerPitch = pose.Pitch;
            _centerZ = pose.Z;
            _centered = true;
            _log.Log(LogLevel.Info, why + ": centre was yaw " + Deg(oldYaw) + " pitch " + Deg(oldPitch)
                                    + ", now yaw " + Deg(_centerYaw) + " pitch " + Deg(_centerPitch) + " (head " + Cm(_centerZ) + " from the camera).");
        }

        private void Recenter(bool live, PoseSnapshot snapshot, double now, TrackingSettings s)
        {
            if (!live)
            {
                _log.Log(LogLevel.Warning, "Recenter ignored: no live tracking right now (" + Describe(Loss) + ").");
                return;
            }

            Pose centre = snapshot.Pose;
            int used = 0;
            if (TryRecentPose(now, RecenterWindow, 6.0, out Pose median, out int n))
            {
                centre = median;
                used = n;
            }

            _centerByUser = true;
            _autoRecenterPending = false;
            SetCenter(centre, used > 1 ? "Recentred on the median of the last " + used + " frames" : "Recentred");

            // Re-run this pose against the new centre, then glide there.
            Unprime();
            _lastSample = snapshot.LastChangeTime;
            ComputeLive(snapshot.Pose, snapshot.RotationSigma, 0, s);
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

            double yaw, pitch;
            switch (State)
            {
                case TrackState.Tracking:
                    yaw = LiveYaw;
                    pitch = LivePitch;
                    double sinceFade = now - _fadeStart;
                    if (s.RecoveryFade > 0 && sinceFade < s.RecoveryFade)
                    {
                        double t = SmoothStep(sinceFade / s.RecoveryFade);
                        yaw = Lerp(_fadeFromYaw, yaw, t);
                        pitch = Lerp(_fadeFromPitch, pitch, t);
                    }

                    break;

                case TrackState.Holding:
                    yaw = _heldYaw;
                    pitch = _heldPitch;
                    break;

                case TrackState.Returning:
                    double k = 1.0 - SmoothStep((now - _returnStart) / s.ReturnTime);
                    yaw = _heldYaw * k;
                    pitch = _heldPitch * k;
                    break;

                default:
                    yaw = pitch = 0;
                    break;
            }

            OutputYaw = Math.Abs(yaw) < OutputEpsilon ? 0 : yaw;
            OutputPitch = Math.Abs(pitch) < OutputEpsilon ? 0 : pitch;
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
                _log.Log(LogLevel.Info, "Tracking started. Head yaw " + Deg(RawYaw) + " pitch " + Deg(RawPitch)
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
            return v.ToString("0") + " cm";
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

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static double Clamp01(double v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }
}
