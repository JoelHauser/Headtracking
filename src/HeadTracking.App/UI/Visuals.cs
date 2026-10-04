using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HeadTracking.Tracking;

namespace HeadTracking.App.UI
{
    internal static class Paint
    {
        public static readonly Brush Background = Frozen(Color.FromRgb(0x18, 0x1B, 0x1F));
        public static readonly Brush Grid = Frozen(Color.FromRgb(0x2A, 0x2F, 0x36));
        public static readonly Brush Axis = Frozen(Color.FromRgb(0x3A, 0x40, 0x49));
        public static readonly Brush DeadZone = Frozen(Color.FromArgb(0x30, 0x9B, 0xA1, 0xA8));
        public static readonly Brush Accent = Frozen(Color.FromRgb(0xD6, 0xAA, 0x5C));
        public static readonly Brush AccentFaint = Frozen(Color.FromArgb(0x55, 0xD6, 0xAA, 0x5C));
        public static readonly Brush Good = Frozen(Color.FromRgb(0x7C, 0xC4, 0x7F));
        public static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x7A));
        public static readonly Brush Text = Frozen(Color.FromRgb(0x9B, 0xA1, 0xA8));
        public static readonly Typeface Font = new Typeface("Segoe UI");

        public static Pen Pen(Brush brush, double thickness, bool dashed = false)
        {
            Pen pen = new Pen(brush, thickness);
            if (dashed)
            {
                pen.DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0);
            }

            pen.Freeze();
            return pen;
        }

        public static void Label(DrawingContext dc, string text, Point at, Brush brush, double size = 11, bool alignRight = false)
        {
            FormattedText ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush, 1.0);
            dc.DrawText(ft, alignRight ? new Point(at.X - ft.Width, at.Y) : at);
        }

        private static Brush Frozen(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>
    /// A square pad with a crosshair: shows where something points, X right and Y up, within
    /// +-<see cref="Range"/> degrees. Optional dead-zone box and limit frame.
    /// </summary>
    public sealed class HeadPad : FrameworkElement
    {
        public static readonly DependencyProperty XProperty = Reg(nameof(X), 0.0);
        public static readonly DependencyProperty YProperty = Reg(nameof(Y), 0.0);
        public static readonly DependencyProperty RangeProperty = Reg(nameof(Range), 30.0);
        public static readonly DependencyProperty DeadXProperty = Reg(nameof(DeadX), 0.0);
        public static readonly DependencyProperty DeadYProperty = Reg(nameof(DeadY), 0.0);
        public static readonly DependencyProperty LimitXProperty = Reg(nameof(LimitX), 0.0);
        public static readonly DependencyProperty LimitYProperty = Reg(nameof(LimitY), 0.0);
        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active), typeof(bool), typeof(HeadPad),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(HeadPad),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
        public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
        public double Range { get => (double)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }
        public double DeadX { get => (double)GetValue(DeadXProperty); set => SetValue(DeadXProperty, value); }
        public double DeadY { get => (double)GetValue(DeadYProperty); set => SetValue(DeadYProperty, value); }
        public double LimitX { get => (double)GetValue(LimitXProperty); set => SetValue(LimitXProperty, value); }
        public double LimitY { get => (double)GetValue(LimitYProperty); set => SetValue(LimitYProperty, value); }
        public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
        public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

        private static readonly Pen GridPen = Paint.Pen(Paint.Grid, 1);
        private static readonly Pen AxisPen = Paint.Pen(Paint.Axis, 1);
        private static readonly Pen LimitPen = Paint.Pen(Paint.AccentFaint, 1, true);
        private static readonly Pen DotPen = Paint.Pen(Paint.Background, 2);

        private static DependencyProperty Reg(string name, double value)
        {
            return DependencyProperty.Register(name, typeof(double), typeof(HeadPad), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 20)
            {
                return;
            }

            Point c = new Point(ActualWidth / 2, ActualHeight / 2);
            double half = size / 2 - 2;
            double scale = half / Math.Max(1, Range);
            Rect box = new Rect(c.X - half, c.Y - half, half * 2, half * 2);
            dc.DrawRoundedRectangle(Paint.Background, Paint.Pen(Paint.Grid, 1), box, 6, 6);

            for (int i = 1; i < 4; i++)
            {
                double o = half * i / 4;
                dc.DrawLine(GridPen, new Point(c.X - o, box.Top + 4), new Point(c.X - o, box.Bottom - 4));
                dc.DrawLine(GridPen, new Point(c.X + o, box.Top + 4), new Point(c.X + o, box.Bottom - 4));
                dc.DrawLine(GridPen, new Point(box.Left + 4, c.Y - o), new Point(box.Right - 4, c.Y - o));
                dc.DrawLine(GridPen, new Point(box.Left + 4, c.Y + o), new Point(box.Right - 4, c.Y + o));
            }

            dc.DrawLine(AxisPen, new Point(c.X, box.Top), new Point(c.X, box.Bottom));
            dc.DrawLine(AxisPen, new Point(box.Left, c.Y), new Point(box.Right, c.Y));

            if (DeadX > 0 || DeadY > 0)
            {
                dc.DrawRectangle(Paint.DeadZone, null, new Rect(c.X - DeadX * scale, c.Y - DeadY * scale, 2 * DeadX * scale, 2 * DeadY * scale));
            }

            if (LimitX > 0 && LimitY > 0)
            {
                double lx = Math.Min(half, LimitX * scale), ly = Math.Min(half, LimitY * scale);
                dc.DrawRectangle(null, LimitPen, new Rect(c.X - lx, c.Y - ly, 2 * lx, 2 * ly));
            }

            double x = Clamp(X * scale, -half + 6, half - 6);
            double y = Clamp(-Y * scale, -half + 6, half - 6);
            Point p = new Point(c.X + x, c.Y + y);
            Brush dot = Active ? Paint.Accent : Paint.Muted;
            dc.DrawEllipse(Paint.AccentFaint, null, p, 13, 13);
            dc.DrawEllipse(dot, DotPen, p, 7, 7);

            Paint.Label(dc, Caption ?? "", new Point(box.Left + 8, box.Top + 6), Paint.Text);
            Paint.Label(dc, "±" + Range.ToString("0") + "°", new Point(box.Right - 8, box.Bottom - 20), Paint.Text, 11, true);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// The response curve for one axis: in-game degrees against real head degrees, with the dead
    /// zone shaded and the current head position marked. Uses the tracker's own Shape(), so what
    /// is drawn is exactly what is applied.
    /// </summary>
    public sealed class CurveGraph : FrameworkElement
    {
        public static readonly DependencyProperty DeadZoneProperty = Reg(nameof(DeadZone), 1.5);
        public static readonly DependencyProperty GainProperty = Reg(nameof(Gain), 2.5);
        public static readonly DependencyProperty MaxProperty = Reg(nameof(Max), 40.0);
        public static readonly DependencyProperty CurveProperty = Reg(nameof(Curve), 1.0);
        public static readonly DependencyProperty CurrentProperty = Reg(nameof(Current), 0.0);

        public double DeadZone { get => (double)GetValue(DeadZoneProperty); set => SetValue(DeadZoneProperty, value); }
        public double Gain { get => (double)GetValue(GainProperty); set => SetValue(GainProperty, value); }
        public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
        public double Curve { get => (double)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
        public double Current { get => (double)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

        private static readonly Pen CurvePen = Paint.Pen(Paint.Accent, 2);
        private string _curveKey;
        private StreamGeometry _curve;
        private static readonly Pen GridPen = Paint.Pen(Paint.Grid, 1);
        private static readonly Pen MarkerPen = Paint.Pen(Paint.Good, 1, true);

        private static DependencyProperty Reg(string name, double value)
        {
            return DependencyProperty.Register(name, typeof(double), typeof(CurveGraph), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40)
            {
                return;
            }

            Rect plot = new Rect(34, 8, w - 42, h - 30);
            dc.DrawRoundedRectangle(Paint.Background, Paint.Pen(Paint.Grid, 1), new Rect(0, 0, w, h), 6, 6);

            double max = Math.Max(1, Max);
            double reach = DeadZone + max / Math.Max(0.1, Gain);
            // Ranges in multiples of 8 so the four grid steps fall on whole numbers.
            double xRange = Math.Max(8, Math.Ceiling(reach * 1.2 / 8) * 8);
            double yRange = Math.Max(8, Math.Ceiling(max * 1.1 / 8) * 8);

            for (int i = 0; i <= 4; i++)
            {
                double gy = plot.Bottom - plot.Height * i / 4;
                dc.DrawLine(GridPen, new Point(plot.Left, gy), new Point(plot.Right, gy));
                Paint.Label(dc, (yRange * i / 4).ToString("0"), new Point(plot.Left - 6, gy - 7), Paint.Text, 10, true);
                double gx = plot.Left + plot.Width * i / 4;
                Paint.Label(dc, (xRange * i / 4).ToString("0") + "°", new Point(gx - 6, plot.Bottom + 4), Paint.Text, 10);
            }

            double deadPx = plot.Width * Math.Min(1, DeadZone / xRange);
            dc.DrawRectangle(Paint.DeadZone, null, new Rect(plot.Left, plot.Top, deadPx, plot.Height));

            // The curve only changes with the settings; the marker moves 20 times a second.
            string key = plot + "|" + DeadZone + "|" + Gain + "|" + max + "|" + Curve;
            StreamGeometry geometry = _curveKey == key ? _curve : new StreamGeometry();
            if (_curveKey != key)
            using (StreamGeometryContext g = geometry.Open())
            {
                const int steps = 120;
                for (int i = 0; i <= steps; i++)
                {
                    double head = xRange * i / steps;
                    double outDeg = HeadTracker.Shape(head, DeadZone, Gain, max, Curve);
                    Point p = new Point(plot.Left + plot.Width * head / xRange, plot.Bottom - plot.Height * Math.Min(1, outDeg / yRange));
                    if (i == 0) g.BeginFigure(p, false, false);
                    else g.LineTo(p, true, false);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, CurvePen, geometry);
            _curveKey = key;
            _curve = geometry;

            double current = Math.Min(xRange, Math.Abs(Current));
            double cx = plot.Left + plot.Width * current / xRange;
            double cy = plot.Bottom - plot.Height * Math.Min(1, HeadTracker.Shape(current, DeadZone, Gain, max, Curve) / yRange);
            dc.DrawLine(MarkerPen, new Point(cx, plot.Bottom), new Point(cx, cy));
            dc.DrawEllipse(Paint.Good, null, new Point(cx, cy), 4.5, 4.5);
        }
    }
}
