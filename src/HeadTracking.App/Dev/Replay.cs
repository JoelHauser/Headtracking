using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HeadTracking.Shared;
using HeadTracking.Tracking;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: replays head angles through the whole chain (tracker, then the plugin's
    /// render-rate follow at 144 fps) for a grid of settings, and scores what you would feel:
    ///   shake  RMS of the rendered view's high-frequency wobble (view minus its own 7-frame
    ///          centred average), in-game degrees;
    ///   lag    how far behind the head the view runs (cross-correlation peak against the true
    ///          angle when the file has one, else against the raw angle);
    ///   error  RMS distance from the true in-game angle (needs a truth column).
    ///   HeadTracking.exe --replay angles.csv [--out results.txt]
    /// CSV: t,valid,yaw,pitch,sigma[,trueYaw,truePitch] (from --live-jitter --csv, or synthetic).
    /// </summary>
    public static class Replay
    {
        private const double RenderHz = 144;

        private struct Sample
        {
            public double T, Yaw, Pitch, Sigma, TrueYaw, TruePitch;
            public bool Valid, HasTruth;
        }

        public static int Run(string[] args, AppLog log)
        {
            string csv = null, outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--replay") continue;
                if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; continue; }
                csv = args[i];
            }

            List<Sample> samples = File.ReadAllLines(csv).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).Select(f => new Sample
            {
                T = D(f[0]),
                Valid = f[1] == "1",
                Yaw = D(f[2]),
                Pitch = D(f[3]),
                Sigma = D(f[4]),
                HasTruth = f.Length >= 7,
                TrueYaw = f.Length >= 7 ? D(f[5]) : 0,
                TruePitch = f.Length >= 7 ? D(f[6]) : 0,
            }).Where(s => s.T > 1.0).ToList();

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[replay] " + line);
            }

            if (samples.Count(s => s.Valid) < 30)
            {
                Write(Path.GetFileName(csv) + ": only " + samples.Count(s => s.Valid) + " samples with a face; nothing to score.");
                return Finish(lines, outPath, 1);
            }

            bool truth = samples[0].HasTruth;
            Write(Path.GetFileName(csv) + ": " + samples.Count + " samples over " + (samples.Last().T - samples.First().T).ToString("0.0") + " s"
                  + (truth ? ", with the true head motion" : "") + ". Sensitivity 2.5 yaw / 2.0 pitch, no centre dead zone.");
            Write("steady smooth  band creep follow |  shake  rest-motion   lag" + (truth ? "  error" : "") + "  still");
            if (truth)
            {
                Score(samples, 0, 0, 0, 0, 0, true, out _, out double floor, out _, out _, out _, truthAsView: true);
                Write("  the true head itself moves " + floor.ToString("0.00") + " deg/s (in game) during the rest windows: the floor.");
            }

            var grid = new List<(double steady, double smooth, double band, double creep, double follow)>
            {
                (0, 0, 0, 0, 0),
                (1.0, 0.5, 0, 0, 60),
                // The shipped defaults (Feel 0.25): the band sized automatically from the measured noise.
                // With and without the creep: DLSS/TAA blur a view that moves at all, however little.
                (1.0, 0, -1, 4.0, 35),
                (1.0, 0, -1, 0.0, 35),
                // Feel 0 (the most responsive end): steadiness 0.8, stillness 0.6, glide 20 ms; and variants.
                (0.8, 0, -0.6, 0, 20),
                (0.8, 0, -0.6, 0, 0),
                (0.0, 0, -0.6, 0, 20),
                (0.8, 0, 0, 0, 20),
                (0.0, 0, 0, 0, 20),
                (0.0, 0, 0, 0, 0),
                // The Feel slider, 0.6.0 mapping against 0.7.0 (less steadiness, a wider stillness lock),
                // at Feel 0, 0.25, 0.5, 0.75 and 1: same glide, so the same smoothness between frames.
                (0.8, 0, -0.6, 0, 20),
                (0.3, 0, -1.0, 0, 20),
                (1.0, 0, -1.0, 0, 35),
                (0.38, 0, -1.2, 0, 35),
                (1.2, 0, -1.4, 0, 50),
                (0.45, 0, -1.4, 0, 50),
                (1.4, 0.2, -1.8, 0, 65),
                (0.52, 0.2, -1.6, 0, 65),
                (1.6, 0.4, -2.2, 0, 80),
                (0.6, 0.4, -1.8, 0, 80),
            };
            foreach (double band in new[] { 0.5, 0.7, 1.0 })
            foreach (double creep in new[] { 0.0, 2.0, 4.0 })
            foreach (double smooth in new[] { 0.0, 0.1 })
            foreach (double follow in new[] { 30, 40, 50 })
            {
                grid.Add((1.0, smooth, band, creep, follow));
            }

            foreach (var g in grid)
            {
                Score(samples, g.steady, g.smooth, g.band, g.creep, g.follow, truth, out double shake, out double rest, out double lag, out double error, out double still);
                Write(g.steady.ToString("0.0").PadLeft(6) + g.smooth.ToString("0.00").PadLeft(7) + (g.band < 0 ? "a" + (-g.band).ToString("0.0") : g.band.ToString("0.0")).PadLeft(6) + g.creep.ToString("0").PadLeft(6)
                      + g.follow.ToString("0").PadLeft(7) + " | " + shake.ToString("0.000").PadLeft(6) + "  " + rest.ToString("0.00").PadLeft(6) + " deg/s "
                      + (lag * 1000).ToString("0").PadLeft(5) + "ms" + (truth ? " " + error.ToString("0.00").PadLeft(6) : "")
                      + (still * 100).ToString("0").PadLeft(5) + "%");
            }

            return Finish(lines, outPath, 0);
        }

        private static void Score(List<Sample> samples, double steadiness, double smoothing, double band, double creep, double followMs, bool truth,
                                  out double shake, out double rest, out double lag, out double error, out double still, bool truthAsView = false)
        {
            HeadTracker tracker = new HeadTracker(new QueuedLog());
            TrackingSettings s = new TrackingSettings
            {
                AutoCenterOnStart = false, Steadiness = steadiness, Smoothing = smoothing, StillnessBand = Math.Max(0, band), Stillness = band < 0 ? -band : band > 0 ? 1 : 0, StillnessCreep = creep,
                YawDeadZone = 0, PitchDeadZone = 0, RecoveryFade = 0, MaxYaw = 90, MaxPitch = 90,
            };

            SmoothFollow follow = new SmoothFollow();
            List<double> view = new List<double>(), reference = new List<double>();
            List<bool> resting = new List<bool>();
            int next = 0;
            double refYaw = 0, lastTrue = double.NaN, lastTrueT = 0;
            bool atRest = false;
            double t0 = samples[0].T, t1 = samples[samples.Count - 1].T;
            for (double t = t0; t <= t1; t += 1.0 / RenderHz)
            {
                while (next < samples.Count && samples[next].T <= t)
                {
                    Sample x = samples[next++];
                    PoseSnapshot snapshot = new PoseSnapshot
                    {
                        HasPose = true, Pose = new Pose(0, 0, 60, x.Yaw, x.Pitch, 0), ArrivalTime = x.T, LastChangeTime = x.T,
                        ReportsValidity = true, Valid = x.Valid, RotationSigma = x.Sigma,
                    };
                    tracker.Tick(true, snapshot, x.T, s);
                    // The reference, in game degrees: the true angle if known, else the raw one.
                    refYaw = HeadTracker.YawSign * 2.5 * (truth ? x.TrueYaw : x.Yaw);
                    // At rest: the true head moving slower than 1 degree a second.
                    if (truth && !double.IsNaN(lastTrue))
                    {
                        atRest = Math.Abs(x.TrueYaw - lastTrue) / Math.Max(1e-3, x.T - lastTrueT) < 1.0;
                    }

                    lastTrue = x.TrueYaw;
                    lastTrueT = x.T;
                }

                resting.Add(atRest);

                view.Add(truthAsView ? refYaw : follow.Step(tracker.OutputYaw, followMs / 1000.0, 1.0 / RenderHz));
                reference.Add(refYaw);
            }

            double sum = 0;
            int n = 0;
            for (int i = 3; i < view.Count - 3; i++)
            {
                double avg = 0;
                for (int k = -3; k <= 3; k++) avg += view[i + k];
                avg /= 7;
                sum += (view[i] - avg) * (view[i] - avg);
                n++;
            }

            shake = Math.Sqrt(sum / Math.Max(1, n));

            // Rest motion: how far the view travels per second while the head is (nearly) still.
            // Still: the share of those frames where the camera did not move at all (under 0.0005
            // deg, about a fiftieth of a pixel). Temporal upscalers only sharpen a view that stops.
            double path = 0;
            int restFrames = 0, stillFrames = 0;
            for (int i = 1; i < view.Count; i++)
            {
                if (resting[i] && resting[i - 1])
                {
                    double step = Math.Abs(view[i] - view[i - 1]);
                    path += step;
                    restFrames++;
                    if (step < 0.0005) stillFrames++;
                }
            }

            still = restFrames > 0 ? (double)stillFrames / restFrames : double.NaN;

            rest = restFrames > 0 ? path / (restFrames / RenderHz) : double.NaN;

            double best = double.NegativeInfinity;
            int bestShift = 0;
            double mv = view.Average(), mr = reference.Average();
            for (int shift = 0; shift <= (int)(0.5 * RenderHz); shift++)
            {
                double c = 0;
                for (int i = shift; i < view.Count; i++) c += (view[i] - mv) * (reference[i - shift] - mr);
                if (c > best) { best = c; bestShift = shift; }
            }

            lag = bestShift / RenderHz;

            double e = 0;
            for (int i = 0; i < view.Count; i++) e += (view[i] - reference[i]) * (view[i] - reference[i]);
            error = Math.Sqrt(e / view.Count);
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private static int Finish(List<string> lines, string outPath, int code)
        {
            if (outPath != null) File.WriteAllLines(outPath, lines);
            return code;
        }
    }
}
