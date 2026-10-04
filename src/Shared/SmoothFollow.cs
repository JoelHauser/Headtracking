using System;

namespace HeadTracking.Shared
{
    /// <summary>
    /// A critically damped follow (the same maths as Unity's Mathf.SmoothDamp, without its speed
    /// cap): moves toward a target with continuous velocity, never overshooting.
    ///
    /// Why the plugin needs it: a webcam delivers 30 poses a second, the game draws 60 to 240
    /// frames. Applied as is, every new pose is a step, and a step every 33 ms of a few tenths of a
    /// degree, multiplied by the sensitivity, reads as shaking. Followed every rendered frame, the
    /// same poses become smooth motion, at the cost of about the smoothing time in lag.
    /// </summary>
    public struct SmoothFollow
    {
        public double Value;
        public double Velocity;

        /// <param name="smoothTime">Seconds; roughly the time to reach the target. 0 snaps.</param>
        public double Step(double target, double smoothTime, double dt)
        {
            if (smoothTime <= 0 || dt <= 0)
            {
                Value = dt <= 0 ? Value : target;
                Velocity = 0;
                return Value;
            }

            double omega = 2.0 / smoothTime;
            double x = omega * dt;
            double decay = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
            double change = Value - target;
            double temp = (Velocity + omega * change) * dt;
            Velocity = (Velocity - omega * temp) * decay;
            double output = target + (change + temp) * decay;

            // Never pass the target.
            if ((target - Value > 0) == (output > target))
            {
                output = target;
                Velocity = 0;
            }

            Value = output;
            return Value;
        }

        /// <summary>Jump to <paramref name="value"/> with no motion.</summary>
        public void Reset(double value = 0)
        {
            Value = value;
            Velocity = 0;
        }

        /// <summary>
        /// Settled on exactly zero: the target is zero and what is left is too small to see. Lets
        /// the camera return to exactly vanilla instead of carrying a residue forever.
        /// </summary>
        public bool SettleOnZero(double target, double epsilon = 0.002)
        {
            if (target == 0 && Math.Abs(Value) < epsilon)
            {
                Value = 0;
                Velocity = 0;
                return true;
            }

            return Value == 0 && target == 0;
        }
    }
}
