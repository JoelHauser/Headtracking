using System;

namespace HeadTracking.App.Webcam
{
    /// <summary>An 8-bit greyscale image, rows top to bottom, no padding.</summary>
    public sealed class GrayImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Data;

        public GrayImage(int width, int height)
            : this(width, height, new byte[width * height])
        {
        }

        public GrayImage(int width, int height, byte[] data)
        {
            if (width <= 0 || height <= 0 || data == null || data.Length < width * height)
            {
                throw new ArgumentException("Bad image size " + width + "x" + height + ".");
            }

            Width = width;
            Height = height;
            Data = data;
        }

        public byte this[int x, int y] => Data[y * Width + x];
    }

    /// <summary>A rectangle in image pixels (floating point), as OpenCV's Rect2f.</summary>
    public struct RectF
    {
        public float X, Y, Width, Height;

        public RectF(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public float Right => X + Width;
        public float Bottom => Y + Height;
        public float CenterX => X + Width * 0.5f;
        public float CenterY => Y + Height * 0.5f;
        public float Area => Width > 0 && Height > 0 ? Width * Height : 0;

        public RectF Scaled(float factor)
        {
            return new RectF(X * factor, Y * factor, Width * factor, Height * factor);
        }

        /// <summary>Same centre, sides times <paramref name="factor"/> (OpenTrack's expand()).</summary>
        public RectF Expanded(float factor)
        {
            float w = Width * factor, h = Height * factor;
            return new RectF(CenterX - w * 0.5f, CenterY - h * 0.5f, w, h);
        }

        /// <summary>Intersection over union: 1 identical, 0 disjoint.</summary>
        public static float IoU(RectF a, RectF b)
        {
            float x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y);
            float x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
            float inter = x1 > x0 && y1 > y0 ? (x1 - x0) * (y1 - y0) : 0;
            float union = a.Area + b.Area - inter;
            return union > 0 ? inter / union : 0;
        }

        public override string ToString()
        {
            return "(" + X.ToString("0") + "," + Y.ToString("0") + " " + Width.ToString("0") + "x" + Height.ToString("0") + ")";
        }
    }

    /// <summary>
    /// The image work the webcam tracker needs, replacing the OpenCV calls in OpenTrack's
    /// neuralnet tracker (cv::resize INTER_AREA, cv::getRectSubPix, its brightness normalisation).
    /// Pure and allocation-light; covered by tests.
    /// </summary>
    public static class ImageOps
    {
        /// <summary>
        /// Resamples the square or rectangular source region [x0, x0+regionW) x [y0, y0+regionH)
        /// (may extend past the image; edges are replicated, as getRectSubPix does) into a
        /// <paramref name="dstW"/> x <paramref name="dstH"/> image. Shrinking averages every source
        /// pixel by its coverage (cv::INTER_AREA); enlarging samples bilinearly.
        /// </summary>
        public static void ResampleRegion(GrayImage src, float x0, float y0, float regionW, float regionH, int dstW, int dstH, byte[] dst)
        {
            if (dst.Length < dstW * dstH)
            {
                throw new ArgumentException("Destination too small.");
            }

            float sx = regionW / dstW;
            float sy = regionH / dstH;
            if (sx >= 1f && sy >= 1f)
            {
                AreaResample(src, x0, y0, sx, sy, dstW, dstH, dst);
            }
            else
            {
                BilinearResample(src, x0, y0, sx, sy, dstW, dstH, dst);
            }
        }

        /// <summary>The whole image to a float tensor: v / 255 - 0.5, the localizer's input.</summary>
        public static void ResizeToTensor(GrayImage src, int dstW, int dstH, byte[] scratch, float[] tensor)
        {
            ResampleRegion(src, 0, 0, src.Width, src.Height, dstW, dstH, scratch);
            int n = dstW * dstH;
            for (int i = 0; i < n; i++)
            {
                tensor[i] = scratch[i] * (1f / 255f) - 0.5f;
            }
        }

        /// <summary>
        /// OpenTrack's normalize_brightness: find the intensity below which 90% of pixels lie, and
        /// stretch so that level maps to about 0.4; then shift to centre on zero. Bright images
        /// are only scaled by 1/255.
        /// </summary>
        public static void NormalizeBrightness(byte[] pixels, int count, float[] tensor)
        {
            const float pct = 90f;
            int brightness = IntensityQuantile(pixels, count, pct);
            float alpha = brightness < 127 ? pct / 100f * 0.5f / Math.Max(5, brightness) : 1f / 255f;
            for (int i = 0; i < count; i++)
            {
                tensor[i] = pixels[i] * alpha - 0.5f;
            }
        }

        /// <summary>The grey level at which more than <paramref name="percentage"/>% of pixels are at or below it.</summary>
        public static int IntensityQuantile(byte[] pixels, int count, float percentage)
        {
            int[] histogram = new int[256];
            for (int i = 0; i < count; i++)
            {
                histogram[pixels[i]]++;
            }

            int quantile = (int)(count * percentage * 0.01f);
            int accumulated = 0;
            for (int level = 0; level < 256; level++)
            {
                accumulated += histogram[level];
                if (accumulated > quantile)
                {
                    return level;
                }
            }

            return 0;
        }

        private static void AreaResample(GrayImage src, float x0, float y0, float sx, float sy, int dstW, int dstH, byte[] dst)
        {
            // Separable box filter with fractional edge weights: horizontal pass into floats,
            // then vertical. Source coordinates are clamped to the image (edge replication).
            int srcH = src.Height, srcW = src.Width;
            int rowsNeededStart = (int)Math.Floor(y0);
            int rowsNeededEnd = (int)Math.Ceiling(y0 + sy * dstH);
            int rows = Math.Max(1, rowsNeededEnd - rowsNeededStart);
            float[] horizontal = new float[rows * dstW];

            Span1D[] xs = Spans(x0, sx, dstW);
            for (int r = 0; r < rows; r++)
            {
                int sr = Clamp(rowsNeededStart + r, 0, srcH - 1);
                int rowOffset = sr * srcW;
                for (int dx = 0; dx < dstW; dx++)
                {
                    Span1D span = xs[dx];
                    float sum = 0;
                    for (int k = 0; k < span.Count; k++)
                    {
                        int sc = Clamp(span.Start + k, 0, srcW - 1);
                        sum += src.Data[rowOffset + sc] * span.Weight(k);
                    }

                    horizontal[r * dstW + dx] = sum / sx;
                }
            }

            Span1D[] ys = Spans(y0 - rowsNeededStart, sy, dstH);
            for (int dy = 0; dy < dstH; dy++)
            {
                Span1D span = ys[dy];
                for (int dx = 0; dx < dstW; dx++)
                {
                    float sum = 0;
                    for (int k = 0; k < span.Count; k++)
                    {
                        int r = Clamp(span.Start + k, 0, rows - 1);
                        sum += horizontal[r * dstW + dx] * span.Weight(k);
                    }

                    dst[dy * dstW + dx] = ToByte(sum / sy);
                }
            }
        }

        private static void BilinearResample(GrayImage src, float x0, float y0, float sx, float sy, int dstW, int dstH, byte[] dst)
        {
            int srcW = src.Width, srcH = src.Height;
            for (int dy = 0; dy < dstH; dy++)
            {
                // Pixel centres: destination pixel (dx, dy) covers source [x0 + dx*sx, x0 + (dx+1)*sx).
                float fy = y0 + (dy + 0.5f) * sy - 0.5f;
                int iy = (int)Math.Floor(fy);
                float ty = fy - iy;
                int r0 = Clamp(iy, 0, srcH - 1) * srcW;
                int r1 = Clamp(iy + 1, 0, srcH - 1) * srcW;
                for (int dx = 0; dx < dstW; dx++)
                {
                    float fx = x0 + (dx + 0.5f) * sx - 0.5f;
                    int ix = (int)Math.Floor(fx);
                    float tx = fx - ix;
                    int c0 = Clamp(ix, 0, srcW - 1), c1 = Clamp(ix + 1, 0, srcW - 1);
                    float top = src.Data[r0 + c0] + (src.Data[r0 + c1] - src.Data[r0 + c0]) * tx;
                    float bottom = src.Data[r1 + c0] + (src.Data[r1 + c1] - src.Data[r1 + c0]) * tx;
                    dst[dy * dstW + dx] = ToByte(top + (bottom - top) * ty);
                }
            }
        }

        /// <summary>The source cells one destination cell covers, with fractional end weights.</summary>
        private struct Span1D
        {
            public int Start;
            public int Count;
            public float FirstWeight;
            public float LastWeight;

            public float Weight(int k)
            {
                if (Count == 1) return FirstWeight;
                if (k == 0) return FirstWeight;
                if (k == Count - 1) return LastWeight;
                return 1f;
            }
        }

        private static Span1D[] Spans(float origin, float scale, int count)
        {
            Span1D[] spans = new Span1D[count];
            for (int i = 0; i < count; i++)
            {
                float a = origin + i * scale;
                float b = a + scale;
                int start = (int)Math.Floor(a);
                int end = (int)Math.Ceiling(b);
                if (end <= start)
                {
                    end = start + 1;
                }

                Span1D span = new Span1D { Start = start, Count = end - start };
                if (span.Count == 1)
                {
                    span.FirstWeight = b - a;
                }
                else
                {
                    span.FirstWeight = (start + 1) - a;
                    span.LastWeight = b - (end - 1);
                }

                spans[i] = span;
            }

            return spans;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static byte ToByte(float v)
        {
            int i = (int)(v + 0.5f);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }
    }
}
