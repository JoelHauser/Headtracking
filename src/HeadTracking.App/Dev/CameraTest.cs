using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FlashCap;
using HeadTracking.App.Webcam;

namespace HeadTracking.App.Dev
{
    /// <summary>
    /// Developer aid: what frame rate does the camera really deliver, per format?
    ///   HeadTracking.exe --camera-test [--seconds 6] [--out results.txt]
    /// Opens each likely format in turn and measures frame intervals and average brightness.
    /// Frames are converted to greyscale for the brightness number and immediately discarded;
    /// nothing is kept, shown or written except the numbers.
    /// </summary>
    public static class CameraTest
    {
        public static int Run(string[] args, AppLog log)
        {
            double seconds = 6;
            string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--seconds" && i + 1 < args.Length) double.TryParse(args[++i], out seconds);
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            }

            List<string> lines = new List<string>();
            void Write(string line)
            {
                lines.Add(line);
                log.Info("[camera-test] " + line);
            }

            List<CameraInfo> cameras = CameraCatalog.List();
            if (cameras.Count == 0)
            {
                Write("No camera.");
                return Finish(lines, outPath, 1);
            }

            CameraInfo camera = cameras[0];
            Write(camera.Name + ": " + camera.Formats.Count + " formats: " + string.Join("; ", camera.Formats.Select(f => f.ToString())));

            CameraFormat vga = camera.Formats.FirstOrDefault(f => f.Width == 640 && f.Height == 480 && f.Fps >= 29 && !f.IsCompressed)
                               ?? camera.Formats.FirstOrDefault(f => f.Width == 640 && f.Height == 480);
            if (vga == null)
            {
                Write("No 640x480 format.");
                return Finish(lines, outPath, 1);
            }

            // The same format with the camera's low-light frame-rate halving allowed, then prevented.
            Write(CameraControl.SetFrameRatePriority(camera.Name, false));
            Measure(camera, vga, seconds, s => Write("low light compensation ON:  " + s));
            Write(CameraControl.SetFrameRatePriority(camera.Name, true));
            Measure(camera, vga, seconds, s => Write("low light compensation OFF: " + s));

            return Finish(lines, outPath, 0);
        }

        private static void Measure(CameraInfo camera, CameraFormat format, double seconds, Action<string> write)
        {
            List<double> arrivals = new List<double>();
            double brightnessSum = 0;
            int brightnessCount = 0;
            object gate = new object();
            Stopwatch clock = Stopwatch.StartNew();
            GrayImage reuse = null;

            CaptureDevice device = null;
            try
            {
                device = camera.Descriptor.OpenAsync(format.Characteristics, TranscodeFormats.DoNotTranscode, false, 1, scope =>
                {
                    double t = clock.Elapsed.TotalMilliseconds;
                    GrayImage gray = FrameDecoder.Decode(scope.Buffer.ReferImage(), reuse, out _);
                    reuse = gray;
                    lock (gate)
                    {
                        arrivals.Add(t);
                        if (gray != null)
                        {
                            long sum = 0;
                            int n = 0;
                            for (int i = 0; i < gray.Data.Length; i += 37) { sum += gray.Data[i]; n++; }
                            brightnessSum += (double)sum / n;
                            brightnessCount++;
                        }
                    }

                    scope.ReleaseNow();
                }).GetAwaiter().GetResult();
                device.StartAsync().GetAwaiter().GetResult();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                device.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                write(format + ": could not open (" + e.Message + ")");
                return;
            }
            finally
            {
                device?.Dispose();
            }

            double[] a;
            lock (gate)
            {
                // Skip the first second: exposure settles.
                a = arrivals.Where(t => t > 1000).ToArray();
            }

            if (a.Length < 3)
            {
                write(format + ": only " + a.Length + " frames after the first second");
                return;
            }

            double[] gaps = a.Zip(a.Skip(1), (x, y) => y - x).OrderBy(g => g).ToArray();
            double fps = (a.Length - 1) / ((a[a.Length - 1] - a[0]) / 1000.0);
            write(format + ": " + fps.ToString("0.0") + " fps delivered; frame gaps median " + gaps[gaps.Length / 2].ToString("0.0")
                  + " ms, 95% " + gaps[(int)(gaps.Length * 0.95)].ToString("0.0") + " ms, max " + gaps[gaps.Length - 1].ToString("0.0")
                  + " ms; brightness " + (brightnessCount > 0 ? brightnessSum / brightnessCount : 0).ToString("0") + "/255");
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
