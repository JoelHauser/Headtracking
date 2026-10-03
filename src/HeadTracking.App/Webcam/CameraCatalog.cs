using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlashCap;

namespace HeadTracking.App.Webcam
{
    /// <summary>One way a camera can deliver frames.</summary>
    public sealed class CameraFormat
    {
        public int Width;
        public int Height;
        public double Fps;
        public PixelFormats PixelFormat;
        public VideoCharacteristics Characteristics;

        public bool IsCompressed => PixelFormat == PixelFormats.JPEG || PixelFormat == PixelFormats.PNG;

        /// <summary>Stable text form, saved in the settings.</summary>
        public string Key => Width + "x" + Height + "@" + Math.Round(Fps) + " " + PixelFormat;

        public override string ToString()
        {
            return Width + " x " + Height + "  " + Math.Round(Fps) + " fps  " + (IsCompressed ? "MJPEG" : PixelFormat.ToString());
        }
    }

    public sealed class CameraInfo
    {
        public string Name;
        public CaptureDeviceDescriptor Descriptor;
        public List<CameraFormat> Formats = new List<CameraFormat>();

        public override string ToString() => Name;
    }

    /// <summary>The cameras Windows has, and the formats the webcam tracker can read from each.</summary>
    public static class CameraCatalog
    {
        private static readonly PixelFormats[] Readable =
        {
            PixelFormats.YUYV, PixelFormats.UYVY, PixelFormats.NV12, PixelFormats.RGB24, PixelFormats.RGB32, PixelFormats.JPEG,
        };

        public static List<CameraInfo> List()
        {
            List<CameraInfo> cameras = new List<CameraInfo>();
            foreach (CaptureDeviceDescriptor d in new CaptureDevices().EnumerateDescriptors())
            {
                // FlashCap lists most cameras twice on Windows, through DirectShow and through the
                // older Video for Windows; DirectShow is the one with every format.
                if (d.DeviceType != DeviceTypes.DirectShow)
                {
                    continue;
                }

                CameraInfo info = new CameraInfo { Name = d.Name, Descriptor = d };
                foreach (VideoCharacteristics c in d.Characteristics)
                {
                    if (!Readable.Contains(c.PixelFormat) || c.Width <= 0 || c.Height <= 0)
                    {
                        continue;
                    }

                    double fps = c.FramesPerSecond.Denominator != 0 ? (double)c.FramesPerSecond.Numerator / c.FramesPerSecond.Denominator : 0;
                    info.Formats.Add(new CameraFormat { Width = c.Width, Height = c.Height, Fps = fps, PixelFormat = c.PixelFormat, Characteristics = c });
                }

                // Distinct by what the user sees, uncompressed first, then size and rate.
                info.Formats = info.Formats
                    .GroupBy(f => f.Key).Select(g => g.First())
                    .OrderBy(f => f.Width * f.Height).ThenByDescending(f => f.Fps).ThenBy(f => f.IsCompressed)
                    .ToList();

                if (info.Formats.Count > 0)
                {
                    cameras.Add(info);
                }
            }

            return cameras;
        }

        /// <summary>
        /// The saved format if the camera still offers it; otherwise the best for head tracking:
        /// around 640x480 (the networks work on small crops, so more pixels only cost decoding
        /// time), as fast as offered up to 60 fps, uncompressed rather than MJPEG.
        /// </summary>
        public static CameraFormat Choose(CameraInfo camera, string savedKey)
        {
            CameraFormat saved = camera.Formats.FirstOrDefault(f => f.Key == savedKey);
            if (saved != null)
            {
                return saved;
            }

            return camera.Formats
                .OrderBy(f => Math.Abs(f.Width - 640) + Math.Abs(f.Height - 480) * 0.5)
                .ThenByDescending(f => Math.Min(f.Fps, 60))
                .ThenBy(f => f.IsCompressed)
                .FirstOrDefault();
        }
    }

    public enum ModelQuality
    {
        /// <summary>Small network, 8-bit weights: least CPU.</summary>
        Fast,
        /// <summary>Small network, full precision. The default.</summary>
        Balanced,
        /// <summary>Big network, 8-bit weights: steadier, more CPU.</summary>
        Accurate,
    }

    /// <summary>Finds OpenTrack's neuralnet models: shipped in HeadTrackingApp\models.</summary>
    public static class ModelLocator
    {
        public const string LocalizerFile = "head-localizer.onnx";

        public static string PoseFileFor(ModelQuality quality)
        {
            switch (quality)
            {
                case ModelQuality.Fast: return "head-pose-0.4-small-int8.onnx";
                case ModelQuality.Accurate: return "head-pose-0.4-big-int8.onnx";
                default: return "head-pose-0.4-small-f32.onnx";
            }
        }

        public static IEnumerable<string> SearchDirectories()
        {
            yield return Path.Combine(AppPaths.DataDirectory, "models");
            yield return Path.Combine(AppPaths.ExeDirectory, "models");
        }

        /// <summary>Full path of <paramref name="file"/>, or null.</summary>
        public static string Find(string file)
        {
            foreach (string dir in SearchDirectories())
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>The pose model for <paramref name="quality"/>, or any pose model present.</summary>
        public static string FindPoseModel(ModelQuality quality, out bool fellBack)
        {
            fellBack = false;
            string exact = Find(PoseFileFor(quality));
            if (exact != null)
            {
                return exact;
            }

            foreach (string dir in SearchDirectories().Where(Directory.Exists))
            {
                string any = Directory.GetFiles(dir, "head-pose-*.onnx").OrderBy(f => f).FirstOrDefault();
                if (any != null)
                {
                    fellBack = true;
                    return any;
                }
            }

            return null;
        }
    }
}
