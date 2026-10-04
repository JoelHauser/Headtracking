using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FlashCap;
using HeadTracking.App.Webcam;
using HeadTracking.Shared;
using HeadTracking.Tracking;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: how much does the view shake with the real camera, on a real face?
    ///   HeadTracking.exe --live-jitter [--seconds 20] [--out results.txt]
    /// For each model, runs three tracker variants side by side on every camera frame (0.3.0's
    /// crop handling; a damped crop; a damped crop plus mirror averaging), records only the
    /// resulting angles, and then pushes them through the filter chains. Nothing but numbers is
    /// kept; frames are discarded as soon as they are processed.
    /// </summary>
    public static class LiveJitter
    {
        private sealed class Variant
        {
            public string Name;
            public ModelQuality Model;
            public WebcamTracker Tracker;
            public WebcamTrackerOptions Options;
            public readonly List<(double T, bool Valid, double Yaw, double Pitch, double Sigma)> Samples = new List<(double, bool, double, double, double)>();
            public readonly List<string> Trace = new List<string>();
            public double Ms;
        }

        public static int Run(string[] args, AppLog log)
        {
            double seconds = 20;
            string outPath = null, csvPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--seconds" && i + 1 < args.Length) double.TryParse(args[++i], out seconds);
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[++i];
            }

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[live-jitter] " + line);
            }

            CameraInfo camera = CameraCatalog.List().FirstOrDefault();
            if (camera == null)
            {
                Write("No camera.");
                return Finish(lines, outPath, 1);
            }

            Write(CameraControl.SetFrameRatePriority(camera.Name, true));
            CameraFormat format = CameraCatalog.Choose(camera, "");
            string localizerPath = ModelLocator.Find(ModelLocator.LocalizerFile);
            List<IDisposable> owned = new List<IDisposable>();
            List<Variant> variants = new List<Variant>();
            SessionOptions options = WebcamSource.CreateSessionOptions(1);
            Localizer localizer = new Localizer(localizerPath, options);
            owned.Add(localizer);
            foreach (ModelQuality model in new[] { ModelQuality.Fast, ModelQuality.Balanced })
            {
                string path = ModelLocator.Find(ModelLocator.PoseFileFor(model));
                if (path == null) continue;
                PoseEstimator pose = new PoseEstimator(path, options);
                owned.Add(pose);
                variants.Add(new Variant { Name = "0.3.0 crop", Model = model, Tracker = new WebcamTracker(localizer, pose), Options = new WebcamTrackerOptions { RoiSmoothing = 1f, MirrorAverage = false } });
                variants.Add(new Variant { Name = "damped crop", Model = model, Tracker = new WebcamTracker(localizer, pose), Options = new WebcamTrackerOptions { RoiSmoothing = 0.3f, MirrorAverage = false } });
                variants.Add(new Variant { Name = "damped + mirror", Model = model, Tracker = new WebcamTracker(localizer, pose), Options = new WebcamTrackerOptions { RoiSmoothing = 0.3f, MirrorAverage = true } });
            }

            Write(camera.Name + " at " + format + "; " + variants.Count + " variants per frame for " + seconds + " s. Sit as you play and keep your head still.");
            Stopwatch clock = Stopwatch.StartNew();
            GrayImage reuse = null;
            int frames = 0;
            CaptureDevice device = null;
            try
            {
                device = camera.Descriptor.OpenAsync(format.Characteristics, TranscodeFormats.DoNotTranscode, false, 1, scope =>
                {
                    GrayImage gray = FrameDecoder.Decode(scope.Buffer.ReferImage(), reuse, out _);
                    reuse = gray;
                    scope.ReleaseNow();
                    if (gray == null) return;
                    double t = clock.Elapsed.TotalSeconds;
                    frames++;
                    double frameMean = Mean(gray, new RectF(0, 0, gray.Width, gray.Height));
                    foreach (Variant v in variants)
                    {
                        TrackResult r = v.Tracker.Process(gray, t, v.Options);
                        v.Ms += r.PoseMs + r.LocalizerMs;
                        v.Samples.Add((t, r.Valid, r.Pose.Yaw, r.Pose.Pitch, r.RotationSigma));
                        if (frames % 3 == 0)
                        {
                            RectF b = r.FaceBox ?? default(RectF);
                            v.Trace.Add(t.ToString("0.00").PadLeft(6) + (r.Valid ? "  ok " : "  -- ")
                                        + " yaw " + r.Pose.Yaw.ToString("+0.0;-0.0").PadLeft(6) + " pitch " + r.Pose.Pitch.ToString("+0.0;-0.0").PadLeft(6)
                                        + " roll " + r.Pose.Roll.ToString("+0.0;-0.0").PadLeft(6) + " sigma " + r.RotationSigma.ToString("0.0").PadLeft(5)
                                        + " | box " + b.X.ToString("0").PadLeft(4) + "," + b.Y.ToString("0").PadLeft(4) + " " + b.Width.ToString("0").PadLeft(4) + "x" + b.Height.ToString("0").PadLeft(4)
                                        + (r.RanLocalizer ? " | detector " + r.LocalizerScore.ToString("0.00") : " |              ")
                                        + " | face light " + (r.FaceBox.HasValue ? Mean(gray, b).ToString("0").PadLeft(3) : "  -") + ", frame " + frameMean.ToString("0").PadLeft(3)
                                        + (r.Valid ? "" : " | " + r.LossReason));
                        }
                    }
                }).GetAwaiter().GetResult();
                device.StartAsync().GetAwaiter().GetResult();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                device.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                device?.Dispose();
            }

            Write(frames + " frames processed (" + (frames / seconds).ToString("0.0") + " fps with all variants).");
            foreach (Variant v in variants)
            {
                var valid = v.Samples.Where(s => s.Valid && s.T > 1.0).ToList();
                if (valid.Count < 30)
                {
                    Write(v.Model + " / " + v.Name + ": face in only " + valid.Count + " frames");
                    continue;
                }

                double yawSpread = Percentile(valid.Select(s => s.Yaw), 95) - Percentile(valid.Select(s => s.Yaw), 5);
                Write(v.Model + " / " + v.Name + " (" + (v.Ms / v.Samples.Count).ToString("0.0") + " ms/frame): raw frame-to-frame yaw "
                      + MedianStep(valid.Select(s => s.Yaw)).ToString("0.00") + " pitch " + MedianStep(valid.Select(s => s.Pitch)).ToString("0.00")
                      + " deg (median), head range " + yawSpread.ToString("0.0") + " deg, net uncertainty " + valid.Average(s => s.Sigma).ToString("0.00"));
                Write("    0.3.0 filters:            " + Chain(valid, 1.0, 0.5, 0));
                Write("    + stillness 0.6:          " + Chain(valid, 1.0, 0.5, 0.6));
                Write("    + stillness 1.0:          " + Chain(valid, 1.0, 0.5, 1.0));
            }

            foreach (Variant v in variants.Where(x => x.Name == "damped + mirror" || x.Name == "0.3.0 crop"))
            {
                Write("");
                Write("Trace, every 3rd frame: " + v.Model + " / " + v.Name);
                foreach (string row in v.Trace)
                {
                    Write(row);
                }
            }

            if (csvPath != null)
            {
                // Head angles only (for replaying through filter settings), never pictures.
                foreach (Variant v in variants.Where(x => x.Name == "damped + mirror"))
                {
                    string path = Path.Combine(Path.GetDirectoryName(csvPath), Path.GetFileNameWithoutExtension(csvPath) + "-" + v.Model + ".csv");
                    File.WriteAllLines(path, new[] { "t,valid,yaw,pitch,sigma" }.Concat(v.Samples.Select(x =>
                        x.T.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + "," + (x.Valid ? 1 : 0) + ","
                        + x.Yaw.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ","
                        + x.Pitch.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ","
                        + x.Sigma.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))));
                    Write("Angles written to " + path);
                }
            }

            foreach (IDisposable d in owned) d.Dispose();
            return Finish(lines, outPath, 0);
        }

        /// <summary>The samples through HeadTracker as the app runs them (no centre dead zone, so nothing hides the shake).</summary>
        private static string Chain(List<(double T, bool Valid, double Yaw, double Pitch, double Sigma)> samples, double steadiness, double smoothing, double band)
        {
            HeadTracker tracker = new HeadTracker(new QueuedLog());
            TrackingSettings s = new TrackingSettings
            {
                Steadiness = steadiness, Smoothing = smoothing, StillnessBand = band, Stillness = band > 0 ? 1 : 0, YawDeadZone = 0, PitchDeadZone = 0, RecoveryFade = 0,
            };
            List<double> steps = new List<double>();
            double lastY = double.NaN, lastP = 0;
            foreach (var x in samples)
            {
                PoseSnapshot snapshot = new PoseSnapshot
                {
                    HasPose = true, Pose = new Pose(0, 0, 60, x.Yaw, x.Pitch, 0), ArrivalTime = x.T, LastChangeTime = x.T,
                    ReportsValidity = true, Valid = true, RotationSigma = x.Sigma,
                };
                tracker.Tick(true, snapshot, x.T, s);
                if (!double.IsNaN(lastY))
                {
                    double dy = tracker.OutputYaw - lastY, dp = tracker.OutputPitch - lastP;
                    steps.Add(Math.Sqrt(dy * dy + dp * dp));
                }

                lastY = tracker.OutputYaw;
                lastP = tracker.OutputPitch;
            }

            steps = steps.Skip(15).ToList();
            double still = steps.Count(v => v < 0.005) * 100.0 / steps.Count;
            return "in-game change per frame median " + Percentile(steps, 50).ToString("0.000") + ", p90 " + Percentile(steps, 90).ToString("0.000")
                   + " deg; " + still.ToString("0") + "% of frames perfectly still";
        }

        /// <summary>Mean grey level inside a box (clamped to the frame). A number, not a picture.</summary>
        private static double Mean(GrayImage g, RectF box)
        {
            int x0 = Math.Max(0, (int)box.X), y0 = Math.Max(0, (int)box.Y);
            int x1 = Math.Min(g.Width, (int)box.Right), y1 = Math.Min(g.Height, (int)box.Bottom);
            long sum = 0;
            int n = 0;
            for (int y = y0; y < y1; y += 4)
            {
                for (int x = x0; x < x1; x += 4)
                {
                    sum += g.Data[y * g.Width + x];
                    n++;
                }
            }

            return n > 0 ? (double)sum / n : 0;
        }

        private static double MedianStep(IEnumerable<double> values)
        {
            double[] v = values.ToArray();
            return Percentile(v.Zip(v.Skip(1), (a, b) => Math.Abs(b - a)), 50);
        }

        private static double Percentile(IEnumerable<double> values, double p)
        {
            double[] v = values.OrderBy(x => x).ToArray();
            if (v.Length == 0) return 0;
            return v[Math.Min(v.Length - 1, (int)(p / 100.0 * v.Length))];
        }

        private static int Finish(List<string> lines, string outPath, int code)
        {
            if (outPath != null)
            {
                File.WriteAllLines(outPath, lines);
            }

            return code;
        }
    }
}
