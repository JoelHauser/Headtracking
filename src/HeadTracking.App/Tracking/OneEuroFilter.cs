using System;
using HeadTracking.Shared;

namespace HeadTracking.Tracking
{
    /// <summary>
    /// The 1€ filter (Casiez, Roussel and Vogel, CHI 2012): a low-pass filter whose cutoff rises
    /// with speed. A still head gets heavy smoothing, so no jitter; a fast turn gets almost none,
    /// so no lag. That trade is the whole reason to use it over a plain average for head tracking.
    /// </summary>
    public sealed class OneEuroFilter
    {
        /// <summary>Cutoff for the speed estimate itself. The paper's recommended value.</summary>
        private const double DerivativeCutoff = 1.0;

        private bool _primed;
        private double _value;
        private double _speed;

        public double Value => _value;

        /// <summary>Forget history and start again at <paramref name="value"/>.</summary>
        public void Reset(double value)
        {
            _primed = true;
            _value = value;
            _speed = 0;
        }

        public void Unprime()
        {
            _primed = false;
        }

        /// <param name="minCutoff">Hz. Lower is smoother at rest.</param>
        /// <param name="beta">How much the cutoff rises per degree per second of movement.</param>
        public double Filter(double input, double dt, double minCutoff, double beta)
        {
            if (!_primed)
            {
                Reset(input);
                return input;
            }

            if (dt <= 0)
            {
                return _value;
            }

            double rawSpeed = (input - _value) / dt;
            _speed += Alpha(dt, DerivativeCutoff) * (rawSpeed - _speed);

            double cutoff = minCutoff + beta * Math.Abs(_speed);
            _value += Alpha(dt, cutoff) * (input - _value);
            return _value;
        }

        private static double Alpha(double dt, double cutoff)
        {
            double tau = 1.0 / (2.0 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }
    }
}
