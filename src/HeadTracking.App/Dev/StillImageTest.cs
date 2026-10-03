using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using HeadTracking.App.Webcam;
using Microsoft.ML.OnnxRuntime;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: runs the webcam pipeline on still images instead of a camera.
    ///   HeadTracking.exe --test-image a.png [b.png ...] [--out results.txt] [--model Fast|Balanced|Accurate]
    /// Each image is placed on a 640x480 canvas as a face would sit in a webcam frame, and also
    /// mirrored and shifted, so the output shows whether yaw flips sign with the mirror image and
    /// how off-centre faces read. Exit code 0 when every original image yields a face.
    /// </summary>
    public static class StillImageTest
    {
        public static int Run(string[] args, AppLog log)
        {
            List<string> images = new List<string>();
            string outPath = null;
            ModelQuality quality = ModelQuality.Balanced;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--test-image") continue;
                if (args[i] == "--out" && i + 1 < args.Length) { outPath = args[++i]; continue; }
                if (args[i] == "--model" && i + 1 < args.Length) { Enum.TryParse(args[++i], true, out quality); continue; }
                images.Add(args[i]);
            }

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[test-image] " + line);
            }

            int failures = 0;
            try
            {
                string localizerPath = ModelLocator.Find(ModelLocator.LocalizerFile);
                string posePath = ModelLocator.FindPoseModel(quality, out _);
                if (localizerPath == null || posePath == null)
                {
                    Write("Models not found in " + string.Join(" or ", ModelLocator.SearchDirectories()));
                    return Finish(lines, outPath, 2);
                }

                using (SessionOptions options = new SessionOptions { IntraOpNumThreads = 2 })
                using (Localizer localizer = new Localizer(localizerPath, options))
                using (PoseEstimator pose = new PoseEstimator(posePath, options))
                {
                    Write(localizer.Describe());
                    Write(Path.GetFileName(posePath) + ": " + pose.Describe());
                    WebcamTracker tracker = new WebcamTracker(localizer, pose);
                    WebcamTrackerOptions trackerOptions = new WebcamTrackerOptions();

                    foreach (string image in images)
                    {
                        using (Bitmap source = new Bitmap(image))
                        {
                            foreach (var variant in new[] { ("original", false, 0), ("mirrored", true, 0), ("shifted left", false, -150), ("shifted right", false, 150) })
                            {
                                GrayImage frame;
                                using (Bitmap canvas = Compose(source, variant.Item2, variant.Item3))
                                {
                                    frame = FrameDecoder.FromBitmap(canvas);
                                }

                                tracker.Reset();
                                TrackResult first = tracker.Process(frame, 0, trackerOptions);
                                TrackResult second = tracker.Process(frame, 0.033, trackerOptions);
                                TrackResult r = second.Valid ? second : first;
                                string text = Path.GetFileName(image) + " " + variant.Item1 + ": ";
                                if (r.Valid)
                                {
                                    text += "face score " + first.LocalizerScore.ToString("0.00") + ", box " + r.FaceBox
                                            + " | yaw " + r.Pose.Yaw.ToString("+0.0;-0.0") + " pitch " + r.Pose.Pitch.ToString("+0.0;-0.0")
                                            + " roll " + r.Pose.Roll.ToString("+0.0;-0.0") + " | x " + r.Pose.X.ToString("+0.0;-0.0")
                                            + " y " + r.Pose.Y.ToString("+0.0;-0.0") + " z " + r.Pose.Z.ToString("0.0") + " cm"
                                            + " | " + first.LocalizerMs.ToString("0.0") + " + " + (first.PoseMs + second.PoseMs).ToString("0.0") + " ms";
                                }
                                else
                                {
                                    text += "NO FACE (" + r.LossReason + ")";
                                    if (variant.Item1 == "original") failures++;
                                }

                                Write(text);
                            }
                        }
                    }

                    // Timing over repeated frames, as the camera loop would run them.
                    if (images.Count > 0)
                    {
                        using (Bitmap source = new Bitmap(images[0]))
                        using (Bitmap canvas = Compose(source, false, 0))
                        {
                            GrayImage frame = FrameDecoder.FromBitmap(canvas);
                            tracker.Reset();
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            const int n = 60;
                            for (int i = 0; i < n; i++)
                            {
                                tracker.Process(frame, i / 30.0, trackerOptions);
                            }

                            Write("Steady state: " + (watch.Elapsed.TotalMilliseconds / n).ToString("0.00") + " ms per frame over " + n + " frames.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Write("FAILED: " + e);
                return Finish(lines, outPath, 3);
            }

            return Finish(lines, outPath, failures == 0 ? 0 : 1);
        }

        /// <summary>The image scaled so it is 240 px tall, on a 640x480 grey background.</summary>
        private static Bitmap Compose(Bitmap source, bool mirror, int shiftX)
        {
            Bitmap canvas = new Bitmap(640, 480);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(96, 96, 96));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int h = 240, w = source.Width * h / source.Height;
                int x = (640 - w) / 2 + shiftX, y = (480 - h) / 2;
                if (mirror)
                {
                    g.DrawImage(source, new Rectangle(x + w, y, -w, h));
                }
                else
                {
                    g.DrawImage(source, new Rectangle(x, y, w, h));
                }
            }

            return canvas;
        }

        private static int Finish(List<string> lines, string outPath, int code)
        {
            if (outPath != null)
            {
                File.WriteAllLines(outPath, lines.Concat(new[] { "exit " + code }));
            }

            return code;
        }
    }
}
