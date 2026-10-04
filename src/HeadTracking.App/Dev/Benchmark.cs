using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using HeadTracking.App.Webcam;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: what the webcam tracker costs in CPU, without a camera.
    ///   HeadTracking.exe --benchmark face.png [--seconds 8] [--out results.txt]
    /// Feeds a still portrait, nudged a few pixels frame to frame like a resting head, through the
    /// real tracker, for each combination of model, inference threads and the mirrored check.
    /// Reports process CPU per frame (all threads, ONNX Runtime's included) and as a share of one
    /// core at 30 fps, plus the wall time per frame (the tracking delay).
    /// Frames run back to back, not paced at 30 fps: Windows charges CPU time at its 15.6 ms clock
    /// tick, and a loop that sleeps wakes just after a tick and finishes before the next, so paced
    /// work goes mostly uncharged (it measured 3.2 ms of CPU for 7.4 ms on one thread).
    /// </summary>
    public static class Benchmark
    {
        private const double FrameInterval = 1.0 / 30.0;

        public static int Run(string[] args, AppLog log)
        {
            string image = null, outPath = null;
            double seconds = 8;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--benchmark") continue;
                if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; continue; }
                if (args[i] == "--seconds" && i + 1 < args.Length) { double.TryParse(args[++i], out seconds); continue; }
                image = args[i];
            }

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[benchmark] " + line);
            }

            if (image == null || !File.Exists(image))
            {
                Write("Give a portrait image: --benchmark face.png");
                return Finish(lines, outPath, 2);
            }

            // A resting head: the same face shifted by a few pixels, cycled.
            List<GrayImage> frames = new List<GrayImage>();
            using (Bitmap source = new Bitmap(image))
            {
                foreach (int shift in new[] { 0, 2, 4, 2, 0, -2, -4, -2 })
                {
                    using (Bitmap canvas = StillImageTest.Compose(source, false, shift))
                    {
                        frames.Add(FrameDecoder.FromBitmap(canvas));
                    }
                }
            }

            var variants = new List<(ModelQuality Model, int Threads, bool Mirror)>
            {
                (ModelQuality.Balanced, 1, true),
                (ModelQuality.Balanced, 2, true),
                (ModelQuality.Balanced, 4, true),
                (ModelQuality.Balanced, 1, false),
                (ModelQuality.Balanced, 2, false),
                (ModelQuality.Fast, 1, true),
                (ModelQuality.Fast, 1, false),
                (ModelQuality.Accurate, 1, true),
            };

            Write("Tracker CPU on " + Path.GetFileName(image) + ", " + seconds.ToString("0") + " s each, frames back to back, " + Environment.ProcessorCount + " logical CPUs.");
            Write("model     threads mirror |  CPU/frame  share of a core at 30 fps |  wall/frame  (delay)");
            string localizerPath = ModelLocator.Find(ModelLocator.LocalizerFile);
            foreach (var v in variants)
            {
                string posePath = ModelLocator.FindPoseModel(v.Model, out _);
                if (localizerPath == null || posePath == null)
                {
                    Write("Models not found in " + string.Join(" or ", ModelLocator.SearchDirectories()));
                    return Finish(lines, outPath, 2);
                }

                using (SessionOptions options = WebcamSource.CreateSessionOptions(v.Threads))
                using (Localizer localizer = new Localizer(localizerPath, options))
                using (PoseEstimator pose = new PoseEstimator(posePath, options))
                {
                    WebcamTracker tracker = new WebcamTracker(localizer, pose);
                    WebcamTrackerOptions trackerOptions = new WebcamTrackerOptions { MirrorAverage = v.Mirror };

                    // Warm up: first runs allocate and pick kernels.
                    for (int i = 0; i < 30; i++)
                    {
                        tracker.Process(frames[i % frames.Count], i * FrameInterval, trackerOptions);
                    }

                    Process self = Process.GetCurrentProcess();
                    self.Refresh();
                    TimeSpan cpuStart = self.TotalProcessorTime;
                    Stopwatch wall = Stopwatch.StartNew();
                    double busy = 0;
                    int n = 0, faces = 0;
                    while (wall.Elapsed.TotalSeconds < seconds)
                    {
                        double start = wall.Elapsed.TotalSeconds;
                        TrackResult r = tracker.Process(frames[n % frames.Count], 1 + n * FrameInterval, trackerOptions);
                        busy += wall.Elapsed.TotalSeconds - start;
                        if (r.Valid) faces++;
                        n++;
                    }

                    self.Refresh();
                    double cpuMs = (self.TotalProcessorTime - cpuStart).TotalMilliseconds;
                    double cpuPerFrame = cpuMs / n;
                    Write(v.Model.ToString().PadRight(9) + v.Threads.ToString().PadLeft(8) + (v.Mirror ? "    yes" : "     no") + " | "
                          + cpuPerFrame.ToString("0.0").PadLeft(6) + " ms   " + (cpuPerFrame * 30 / 10).ToString("0").PadLeft(10) + "%"
                          + "                 | " + (busy / n * 1000).ToString("0.0").PadLeft(7) + " ms"
                          + (faces < n * 0.9 ? "   (face in only " + faces + " of " + n + ")" : ""));
                }
            }

            return Finish(lines, outPath, 0);
        }

        private static int Finish(List<string> lines, string outPath, int code)
        {
            if (outPath != null) File.WriteAllLines(outPath, lines);
            return code;
        }
    }
}
