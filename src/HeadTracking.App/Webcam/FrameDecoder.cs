using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace HeadTracking.App.Webcam
{
    /// <summary>
    /// Turns one camera frame, as FlashCap hands it over with transcoding off, into greyscale.
    /// The trackers only use brightness, so for the common YUV formats this is just the Y bytes,
    /// with no colour conversion at all.
    ///
    /// FlashCap's layout (read from FlashCap.Core 1.12's PixelBuffer.CopyIn): MJPEG/JPEG frames are
    /// the bare compressed stream; everything else is a BMP file in memory, BITMAPFILEHEADER +
    /// the driver's BITMAPINFOHEADER + the raw pixels, whatever their format.
    /// </summary>
    public static class FrameDecoder
    {
        private const uint FourCcYuy2 = 0x32595559; // 'YUY2'
        private const uint FourCcYuyv = 0x56595559; // 'YUYV'
        private const uint FourCcUyvy = 0x59565955; // 'UYVY'
        private const uint FourCcNv12 = 0x3231564E; // 'NV12'
        private const uint BiRgb = 0;
        private const uint BiBitfields = 3;

        /// <summary>
        /// Decodes into <paramref name="reuse"/> when its size matches, so a running camera does
        /// not allocate per frame. Returns null for a format it cannot read, with
        /// <paramref name="error"/> saying which.
        /// </summary>
        public static GrayImage Decode(ArraySegment<byte> frame, GrayImage reuse, out string error)
        {
            error = null;
            byte[] b = frame.Array;
            int o = frame.Offset;
            if (b == null || frame.Count < 2)
            {
                error = "empty frame";
                return null;
            }

            // JPEG starts FF D8; a BMP starts 'BM'.
            if (b[o] == 0xFF && b[o + 1] == 0xD8)
            {
                return DecodeJpeg(frame, reuse, out error);
            }

            if (b[o] != (byte)'B' || b[o + 1] != (byte)'M' || frame.Count < 54)
            {
                error = "unknown frame layout (first bytes " + b[o].ToString("X2") + " " + b[o + 1].ToString("X2") + ")";
                return null;
            }

            int dataOffset = BitConverter.ToInt32(b, o + 10);
            int width = BitConverter.ToInt32(b, o + 18);
            int height = BitConverter.ToInt32(b, o + 22);
            int bitCount = BitConverter.ToUInt16(b, o + 28);
            uint compression = BitConverter.ToUInt32(b, o + 30);
            bool bottomUp = height > 0;
            height = Math.Abs(height);

            if (width <= 0 || height <= 0)
            {
                error = "bad frame size " + width + "x" + height;
                return null;
            }

            GrayImage gray = reuse != null && reuse.Width == width && reuse.Height == height ? reuse : new GrayImage(width, height);
            int src = o + dataOffset;
            int available = frame.Count - dataOffset;
            byte[] dst = gray.Data;

            switch (compression)
            {
                case FourCcYuy2:
                case FourCcYuyv:
                    if (!Enough(available, width * height * 2, out error)) return null;
                    for (int y = 0; y < height; y++)
                    {
                        int s = src + y * width * 2, d = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            dst[d + x] = b[s + x * 2];
                        }
                    }

                    return gray;

                case FourCcUyvy:
                    if (!Enough(available, width * height * 2, out error)) return null;
                    for (int y = 0; y < height; y++)
                    {
                        int s = src + y * width * 2 + 1, d = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            dst[d + x] = b[s + x * 2];
                        }
                    }

                    return gray;

                case FourCcNv12:
                    if (!Enough(available, width * height, out error)) return null;
                    Buffer.BlockCopy(b, src, dst, 0, width * height);
                    return gray;

                case BiRgb:
                case BiBitfields:
                    if (bitCount != 24 && bitCount != 32)
                    {
                        error = "unsupported RGB depth " + bitCount + " bits";
                        return null;
                    }

                    int bytesPerPixel = bitCount / 8;
                    int stride = (width * bytesPerPixel + 3) & ~3;
                    if (!Enough(available, stride * height, out error)) return null;
                    for (int y = 0; y < height; y++)
                    {
                        int row = bottomUp ? height - 1 - y : y;
                        int s = src + row * stride, d = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int p = s + x * bytesPerPixel;
                            dst[d + x] = Luma(b[p + 2], b[p + 1], b[p]);
                        }
                    }

                    return gray;

                default:
                    error = "unsupported pixel format " + FourCcName(compression) + " (" + bitCount + " bits)";
                    return null;
            }
        }

        public static string FourCcName(uint fourCc)
        {
            if (fourCc == BiRgb) return "RGB";
            char[] c = { (char)(fourCc & 0xFF), (char)((fourCc >> 8) & 0xFF), (char)((fourCc >> 16) & 0xFF), (char)((fourCc >> 24) & 0xFF) };
            foreach (char ch in c)
            {
                if (ch < 32 || ch > 126) return "0x" + fourCc.ToString("X8");
            }

            return new string(c);
        }

        /// <summary>Rec. 601 luma, the same weights as OpenCV's BGR2GRAY.</summary>
        public static byte Luma(byte r, byte g, byte b)
        {
            return (byte)((77 * r + 150 * g + 29 * b + 128) >> 8);
        }

        private static GrayImage DecodeJpeg(ArraySegment<byte> frame, GrayImage reuse, out string error)
        {
            error = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(frame.Array, frame.Offset, frame.Count, false))
                using (Bitmap bitmap = new Bitmap(stream))
                {
                    return FromBitmap(bitmap, reuse);
                }
            }
            catch (Exception e)
            {
                error = "JPEG decode failed: " + e.Message;
                return null;
            }
        }

        /// <summary>Any GDI+ bitmap to greyscale (JPEG frames, and still images for testing).</summary>
        public static unsafe GrayImage FromBitmap(Bitmap bitmap, GrayImage reuse = null)
        {
            int width = bitmap.Width, height = bitmap.Height;
            GrayImage gray = reuse != null && reuse.Width == width && reuse.Height == height ? reuse : new GrayImage(width, height);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] dst = gray.Data;
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int d = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        byte* p = row + x * 3;
                        dst[d + x] = Luma(p[2], p[1], p[0]);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return gray;
        }

        private static bool Enough(int available, int needed, out string error)
        {
            error = available >= needed ? null : "frame holds " + available + " bytes, expected " + needed;
            return error == null;
        }
    }
}
