namespace HeadTracking.Tracking
{
    /// <summary>What the engine knows each tick that decides whether the webcam may run.</summary>
    public struct CameraInputs
    {
        /// <summary>False in --snapshot: never.</summary>
        public bool Allowed;
        /// <summary>Head tracking switched on (header switch, toggle key).</summary>
        public bool Enabled;
        /// <summary>The "Turn the camera off when it isn't needed" setting.</summary>
        public bool OnlyWhenNeeded;
        /// <summary>Nobody in view this long outside a raid turns it off. 0: the default, 3 minutes.</summary>
        public double AwaySeconds;
        /// <summary>The game is running and talking to the app.</summary>
        public bool GameConnected;
        public bool InRaid;
        /// <summary>This app's window is the active window and not minimized.</summary>
        public bool AppActive;
        /// <summary>The camera is open now.</summary>
        public bool Running;
        /// <summary>The tracker sees a face this tick.</summary>
        public bool FaceInView;
    }

    /// <summary>
    /// Privacy: the webcam runs only while it is needed. On while SPT is running or the app's
    /// window is in front; off (its light goes out) 30 s after neither, at once when tracking is
    /// switched off, and after a few minutes with nobody in view outside a raid. A raid starting,
    /// the window coming to the front, the in-game keys or the "Turn camera on" button wake it.
    /// In a raid it never sleeps on its own: a lost face there is someone looking away.
    ///
    /// "Turn camera off now" is the user's own choice and sticks: only the user undoes it (the
    /// button, or F7/F8 in game), never a raid starting or the window coming to the front.
    /// </summary>
    public sealed class CameraPolicy
    {
        public const double GraceSeconds = 30;
        public const double DefaultAwaySeconds = 180;
        public const string ManualOffReason = "turned off on the Privacy page";

        private bool _asleep, _manualOff, _wasActive = true, _wasInRaid;
        private double _neededUntil, _lastFace;
        private string _wakeReason;

        public CameraPolicy(double now)
        {
            _neededUntil = now + GraceSeconds;
            _lastFace = now;
        }

        /// <summary>Null while the camera may run; otherwise why it is off, in words for the user.</summary>
        public string OffReason { get; private set; }

        public bool Wanted => OffReason == null;

        public bool ManuallyOff => _manualOff;

        /// <summary>The last wake-up's cause, for the log; cleared when read.</summary>
        public string TakeWakeReason()
        {
            string r = _wakeReason;
            _wakeReason = null;
            return r;
        }

        /// <param name="byUser">The user asked for it (button, in-game key): also ends "Turn camera off now".</param>
        public void Wake(double now, string why, bool byUser)
        {
            if (!Wanted || _asleep)
            {
                _wakeReason = why;
            }

            _asleep = false;
            if (byUser)
            {
                _manualOff = false;
            }

            _lastFace = now;
            _neededUntil = now + GraceSeconds;
        }

        /// <summary>"Turn camera off now": off until the user turns it back on.</summary>
        public void TurnOff()
        {
            _manualOff = true;
        }

        /// <summary>The camera just opened: the away time counts from now, not from before it closed.</summary>
        public void Opened(double now)
        {
            _lastFace = now;
        }

        public void Update(double now, CameraInputs i)
        {
            if (i.InRaid && !_wasInRaid)
            {
                Wake(now, "a raid started", false);
            }
            else if (i.AppActive && !_wasActive)
            {
                Wake(now, "this window came to the front", false);
            }

            _wasInRaid = i.InRaid;
            _wasActive = i.AppActive;

            if (i.FaceInView)
            {
                _lastFace = now;
            }

            double away = i.AwaySeconds > 0 ? i.AwaySeconds : DefaultAwaySeconds;
            string reason = null;
            if (!i.Allowed)
            {
                reason = "developer snapshot mode";
            }
            else if (!i.Enabled)
            {
                reason = "head tracking is switched off";
            }
            else if (_manualOff)
            {
                reason = ManualOffReason;
            }
            else if (i.OnlyWhenNeeded)
            {
                if (!_asleep && i.Running && !i.InRaid && now - _lastFace > away)
                {
                    _asleep = true;
                }

                if (_asleep)
                {
                    reason = "nobody in view for " + Minutes(away) + " outside a raid";
                }
                else if (i.AppActive || i.GameConnected)
                {
                    _neededUntil = now + GraceSeconds;
                }
                else if (now >= _neededUntil)
                {
                    reason = "no game running and this window is in the background";
                }
            }

            OffReason = reason;
        }

        private static string Minutes(double seconds)
        {
            int m = (int)System.Math.Round(seconds / 60);
            return m == 1 ? "a minute" : m + " minutes";
        }
    }
}
