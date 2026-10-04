using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using HeadTracking.App.Webcam;
using HeadTracking.Shared;
using HeadTracking.Tracking;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: how much does the view shake for a head that does not move?
    ///   HeadTracking.exe --jitter-test face.png [--noise 4] [--out results.txt]
    /// Feeds the same face image for 150 frames with fresh Gaussian pixel noise each frame (a still
    /// head on a real sensor), then pushes the poses through the tracker at 30 frames a second with
    /// the head held 10 degrees off centre (outside the dead zone), and reports the in-game shake:
    /// raw, with smoothing only (0.2.0's chain), and with steadiness + smoothing (0.3.0's default).
    /// </summary>
    public static class JitterTest
    {
        private struct Sample
        {
            public HeadPose Pose;
            public double Sigma;
        }

        public static int Run(string[] args, AppLog log)
        {
            string image = null, outPath = null;
            double noise = 4;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--jitter-test") continue;
                if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; continue; }
                if (args[i] == "--noise" && i + 1 < args.Length) { double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out noise); continue; }
                image = args[i];
            }

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[jitter-test] " + line);
            }

            GrayImage clean;
            using (Bitmap source = new Bitmap(image))
            using (Bitmap canvas = new Bitmap(640, 480))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.FromArgb(96, 96, 96));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    int h = 240, w = source.Width * h / source.Height;
                    g.DrawImage(source, new Rectangle((640 - w) / 2, 120, w, h));
                }

                clean = FrameDecoder.FromBitmap(canvas);
            }

            Write("Image " + Path.GetFileName(image) + ", pixel noise sigma " + noise + " grey levels, 150 frames per model, sensitivity 2.5/2.0.");
            string localizerPath = ModelLocator.Find(ModelLocator.LocalizerFile);
            foreach (ModelQuality quality in new[] { ModelQuality.Fast, ModelQuality.Balanced, ModelQuality.Accurate })
            {
                string posePath = ModelLocator.Find(ModelLocator.PoseFileFor(quality));
                if (posePath == null)
                {
                    Write(quality + ": model missing");
                    continue;
                }

                List<Sample> samples = new List<Sample>();
                double ms = 0;
                using (SessionOptions options = WebcamSource.CreateSessionOptions(1))
                using (Localizer localizer = new Localizer(localizerPath, options))
                using (PoseEstimator pose = new PoseEstimator(posePath, options))
                {
                    WebcamTracker tracker = new WebcamTracker(localizer, pose);
                    Random random = new Random(1);
                    GrayImage noisy = new GrayImage(clean.Width, clean.Height);
                    for (int f = 0; f < 150; f++)
                    {
                        AddNoise(clean, noisy, noise, random);
                        TrackResult r = tracker.Process(noisy, f / 30.0, new WebcamTrackerOptions());
                        if (f >= 10 && r.Valid)
                        {
                            samples.Add(new Sample { Pose = r.Pose, Sigma = r.RotationSigma });
                            ms += r.PoseMs + r.LocalizerMs;
                        }
                    }
                }

                if (samples.Count < 20)
                {
                    Write(quality + ": face found in only " + samples.Count + " frames");
                    continue;
                }

                Write(quality + " (" + Path.GetFileName(posePath) + "), " + (ms / samples.Count).ToString("0.0") + " ms/frame with 1 thread:");
                Write("  network: yaw sd " + Sd(samples.Select(p => p.Pose.Yaw)).ToString("0.000") + ", pitch sd " + Sd(samples.Select(p => p.Pose.Pitch)).ToString("0.000")
                      + " deg; its own uncertainty says " + samples.Average(p => p.Sigma).ToString("0.000") + " deg");
                Write("  in game, raw (no filtering):                  " + Shake(samples, 0, 0));
                Write("  in game, smoothing 0.5 only (0.2.0's filters): " + Shake(samples, 0, 0.5));
                Write("  in game, steadiness 1 + smoothing 0.5 (0.3.0): " + Shake(samples, 1.0, 0.5));
                Write("  in game, steadiness 1.5 + smoothing 0.6:       " + Shake(samples, 1.5, 0.6));
            }

            if (outPath != null)
            {
                File.WriteAllLines(outPath, lines);
            }

            return 0;
        }

        /// <summary>The samples through HeadTracker at 30 Hz, head held 10/6 degrees off centre.</summary>
        private static string Shake(List<Sample> samples, double steadiness, double smoothing)
        {
            HeadTracker tracker = new HeadTracker(new QueuedLog());
            TrackingSettings settings = new TrackingSettings { AutoCenterOnStart = false, Steadiness = steadiness, Smoothing = smoothing, Stillness = 0, RecoveryFade = 0 };
            double cy = samples.Average(p => p.Pose.Yaw), cp = samples.Average(p => p.Pose.Pitch);
            List<double> yaws = new List<double>(), pitches = new List<double>();
            for (int i = 0; i < samples.Count; i++)
            {
                double t = 100 + i / 30.0;
                HeadPose p = samples[i].Pose;
                PoseSnapshot snapshot = new PoseSnapshot
                {
                    HasPose = true,
                    Pose = new Pose(p.X, p.Y, p.Z, p.Yaw - cy + 10, p.Pitch - cp + 6, p.Roll),
                    ArrivalTime = t,
                    LastChangeTime = t,
                    ReportsValidity = true,
                    Valid = true,
                    RotationSigma = samples[i].Sigma,
                };
                tracker.Tick(true, snapshot, t, settings);
                if (i >= 20)
                {
                    yaws.Add(tracker.OutputYaw);
                    pitches.Add(tracker.OutputPitch);
                }
            }

            return "yaw sd " + Sd(yaws).ToString("0.000") + ", pitch sd " + Sd(pitches).ToString("0.000")
                   + " deg; frame-to-frame " + MeanStep(yaws, pitches).ToString("0.000") + " deg";
        }

        private static void AddNoise(GrayImage clean, GrayImage noisy, double sigma, Random random)
        {
            for (int i = 0; i < clean.Data.Length; i++)
            {
                double u1 = 1.0 - random.NextDouble(), u2 = random.NextDouble();
                double n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sigma;
                int v = (int)Math.Round(clean.Data[i] + n);
                noisy.Data[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }

        private static double Sd(IEnumerable<double> values)
        {
            double[] v = values.ToArray();
            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
        }

        private static double MeanStep(List<double> a, List<double> b)
        {
            double sum = 0;
            for (int i = 1; i < a.Count; i++)
            {
                double dy = a[i] - a[i - 1], dp = b[i] - b[i - 1];
                sum += Math.Sqrt(dy * dy + dp * dp);
            }

            return sum / (a.Count - 1);
        }
    }
}
