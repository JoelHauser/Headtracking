using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HeadTracking.App.Webcam
{
    /// <summary>
    /// OpenTrack's face localizer network (head-localizer.onnx): one 288x224 greyscale image in,
    /// a face score and box out. Port of model_adapters.cpp's Localizer.
    /// </summary>
    public sealed class Localizer : IDisposable
    {
        public const int InputWidth = 288;
        public const int InputHeight = 224;

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly float[] _input = new float[InputWidth * InputHeight];
        private readonly byte[] _scratch = new byte[InputWidth * InputHeight];
        private readonly DenseTensor<float> _tensor;

        public double LastMilliseconds { get; private set; }

        public Localizer(string path, SessionOptions options)
        {
            _session = new InferenceSession(path, options);
            // OpenTrack names them "x" and "logit_box"; take the model's own names, so a renamed
            // tensor is not silently a different one.
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.ContainsKey("logit_box") ? "logit_box" : _session.OutputMetadata.Keys.First();
            _tensor = new DenseTensor<float>(_input, new[] { 1, 1, InputHeight, InputWidth });
        }

        public string Describe()
        {
            return "localizer: input " + _inputName + " " + Dims(_session.InputMetadata[_inputName]) + ", output " + _outputName + " " + Dims(_session.OutputMetadata[_outputName]);
        }

        /// <returns>Face score 0..1 and the face box in the frame's pixels.</returns>
        public float Run(GrayImage frame, out RectF box)
        {
            ImageOps.ResizeToTensor(frame, InputWidth, InputHeight, _scratch, _input);

            Stopwatch watch = Stopwatch.StartNew();
            float[] r;
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                   _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, _tensor) }, new[] { _outputName }))
            {
                r = results.First().AsEnumerable<float>().ToArray();
            }

            LastMilliseconds = watch.Elapsed.TotalMilliseconds;

            // Corners in -1..1 to pixels.
            float x0 = Unnormalize(r[1]) * frame.Width, y0 = Unnormalize(r[2]) * frame.Height;
            float x1 = Unnormalize(r[3]) * frame.Width, y1 = Unnormalize(r[4]) * frame.Height;
            box = new RectF(x0, y0, x1 - x0, y1 - y0);
            return Sigmoid(r[0]);
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private static float Unnormalize(float v)
        {
            return 0.5f * (v + 1f);
        }

        internal static float Sigmoid(float x)
        {
            return 1f / (1f + (float)Math.Exp(-x));
        }

        internal static string Dims(NodeMetadata m)
        {
            return "[" + string.Join("x", m.Dimensions) + "]";
        }
    }

    /// <summary>What the pose network found, in the frame's pixels.</summary>
    public struct Face
    {
        public Quat Rotation;
        public RectF Box;
        public float CenterX, CenterY;
        public float Size;

        /// <summary>
        /// One-sigma rotation uncertainty in degrees, from the network's rotaxis_scales_tril
        /// (the Cholesky factor L of the rotation-vector covariance: sigma^2 = trace(L L^T) / 3).
        /// 0 when the model has no such output.
        /// </summary>
        public float RotationSigmaDegrees;
    }

    /// <summary>
    /// OpenTrack's head pose network (head-pose-*.onnx): a square greyscale crop around the head in,
    /// head rotation, centre, size and box out. Port of model_adapters.cpp's PoseEstimator, without
    /// the uncertainty outputs (OpenTrack only uses those for its internal filter, which this app
    /// replaces with its own smoothing).
    /// </summary>
    public sealed class PoseEstimator : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly long _modelVersion;
        private readonly float[] _input;
        private readonly byte[] _patch;
        private readonly DenseTensor<float> _tensor;
        private readonly string[] _outputs;
        private readonly bool _hasRotationUncertainty;

        public int InputWidth { get; }
        public int InputHeight { get; }
        public double LastMilliseconds { get; private set; }

        /// <summary>The last network input, for the preview: greyscale, InputWidth x InputHeight.</summary>
        public byte[] LastPatch => _patch;

        public PoseEstimator(string path, SessionOptions options)
        {
            _session = new InferenceSession(path, options);
            KeyValuePair<string, NodeMetadata> input = _session.InputMetadata.First();
            _inputName = input.Key;
            int[] dims = input.Value.Dimensions;
            if (dims.Length != 4)
            {
                throw new InvalidOperationException("Pose model input has shape " + Localizer.Dims(input.Value) + ", expected 1x1xHxW.");
            }

            InputHeight = dims[2];
            InputWidth = dims[3];
            _hasRotationUncertainty = _session.OutputMetadata.ContainsKey("rotaxis_scales_tril");
            _outputs = _hasRotationUncertainty
                ? new[] { "pos_size", "quat", "box", "rotaxis_scales_tril" }
                : new[] { "pos_size", "quat", "box" };
            foreach (string name in _outputs)
            {
                if (!_session.OutputMetadata.ContainsKey(name))
                {
                    throw new InvalidOperationException("Pose model has no '" + name + "' output. Outputs: " + string.Join(", ", _session.OutputMetadata.Keys));
                }
            }

            // As OpenTrack: the first model release had no version, and reading it gives junk.
            long version = _session.ModelMetadata.Version;
            _modelVersion = version <= 0 || version > 4 ? 1 : version;

            _input = new float[InputWidth * InputHeight];
            _patch = new byte[InputWidth * InputHeight];
            _tensor = new DenseTensor<float>(_input, new[] { 1, 1, InputHeight, InputWidth });
        }

        public string Describe()
        {
            return "pose net: input " + _inputName + " " + Localizer.Dims(_session.InputMetadata[_inputName]) + ", model version "
                   + _session.ModelMetadata.Version + " (treated as " + _modelVersion + "), outputs "
                   + string.Join(", ", _session.OutputMetadata.Select(o => o.Key + " " + Localizer.Dims(o.Value)));
        }

        /// <summary>The raw outputs of one network run, in the network's own crop coordinates.</summary>
        private struct Outputs
        {
            public float[] PosSize, Quat, Box, RotationTril;
        }

        /// <summary>
        /// Null when the crop is degenerate or the network fails. With <paramref name="mirrorAverage"/>
        /// the network also sees the crop mirrored left to right, and the two answers are averaged:
        /// its errors on the two images are partly independent, so they partly cancel (at twice the
        /// cost, about 2 ms more).
        /// </summary>
        public Face? Run(GrayImage frame, RectF box, bool mirrorAverage = false)
        {
            int patchSize = (int)Math.Round(Math.Max(box.Width, box.Height));
            if (patchSize < 8)
            {
                return null;
            }

            float cx = Clamp(box.CenterX, 0f, frame.Width);
            float cy = Clamp(box.CenterY, 0f, frame.Height);

            ImageOps.ResampleRegion(frame, cx - patchSize * 0.5f, cy - patchSize * 0.5f, patchSize, patchSize, InputWidth, InputHeight, _patch);
            ImageOps.NormalizeBrightness(_patch, _patch.Length, _input);

            Stopwatch watch = Stopwatch.StartNew();
            if (!Infer(out Outputs o))
            {
                return null;
            }

            float sigma = o.RotationTril == null ? 0f : RotationSigma(o.RotationTril);
            if (mirrorAverage)
            {
                MirrorRows(_input, InputWidth, InputHeight);
                if (Infer(out Outputs m))
                {
                    // Undo the mirror. In the network's image frame (x right, y down, z into the
                    // picture) a left-right flip is the reflection x -> -x, which maps a rotation's
                    // quaternion (w, x, y, z) to (w, x, -y, -z): yaw and roll change sign, pitch not.
                    float[] mq = { m.Quat[0], -m.Quat[1], -m.Quat[2], m.Quat[3] };
                    float dot = o.Quat[0] * mq[0] + o.Quat[1] * mq[1] + o.Quat[2] * mq[2] + o.Quat[3] * mq[3];
                    float s = dot < 0 ? -1f : 1f;
                    for (int i = 0; i < 4; i++)
                    {
                        o.Quat[i] = 0.5f * (o.Quat[i] + s * mq[i]);
                    }

                    o.PosSize[0] = 0.5f * (o.PosSize[0] - m.PosSize[0]);
                    o.PosSize[1] = 0.5f * (o.PosSize[1] + m.PosSize[1]);
                    o.PosSize[2] = 0.5f * (o.PosSize[2] + m.PosSize[2]);
                    float x0 = 0.5f * (o.Box[0] - m.Box[2]), x1 = 0.5f * (o.Box[2] - m.Box[0]);
                    o.Box[0] = x0;
                    o.Box[2] = x1;
                    o.Box[1] = 0.5f * (o.Box[1] + m.Box[1]);
                    o.Box[3] = 0.5f * (o.Box[3] + m.Box[3]);
                    if (m.RotationTril != null)
                    {
                        // Averaging two partly independent estimates: report the smaller spread honestly
                        // as the mean of the two, not less; the steadiness filter is tuned on that scale.
                        sigma = 0.5f * (sigma + RotationSigma(m.RotationTril));
                    }
                }

                MirrorRows(_input, InputWidth, InputHeight);
            }

            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            float[] posSize = o.PosSize, quat = o.Quat, outBox = o.Box;

            float half = 0.5f * patchSize;
            // The network gives quaternions as x, y, z, w.
            Quat rotation = new Quat(quat[3], quat[0], quat[1], quat[2]).Normalized;
            if (_modelVersion < 2)
            {
                // A change of coordinate conventions after the first release.
                rotation = PoseMath.ImageToWorld(rotation);
            }

            return new Face
            {
                Rotation = rotation,
                CenterX = cx + half * posSize[0],
                CenterY = cy + half * posSize[1],
                Size = half * posSize[2],
                Box = new RectF(cx + half * outBox[0], cy + half * outBox[1], half * (outBox[2] - outBox[0]), half * (outBox[3] - outBox[1])),
                RotationSigmaDegrees = sigma,
            };
        }

        private bool Infer(out Outputs outputs)
        {
            outputs = default;
            try
            {
                using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                       _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, _tensor) }, _outputs))
                {
                    outputs.PosSize = Get(results, "pos_size");
                    outputs.Quat = Get(results, "quat");
                    outputs.Box = Get(results, "box");
                    if (_hasRotationUncertainty)
                    {
                        outputs.RotationTril = Get(results, "rotaxis_scales_tril");
                    }
                }

                return true;
            }
            catch (OnnxRuntimeException)
            {
                return false;
            }
        }

        /// <summary>Flips each row of a width x height image left to right, in place.</summary>
        internal static void MirrorRows(float[] image, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int a = row, b = row + width - 1; a < b; a++, b--)
                {
                    float t = image[a];
                    image[a] = image[b];
                    image[b] = t;
                }
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        /// <summary>sqrt(trace(L L^T) / 3) in degrees: the RMS of the three axis deviations.</summary>
        internal static float RotationSigma(float[] tril)
        {
            double sum = 0;
            for (int i = 0; i < tril.Length && i < 9; i++)
            {
                sum += tril[i] * (double)tril[i];
            }

            return (float)(Math.Sqrt(sum / 3.0) * 180.0 / Math.PI);
        }

        private static float[] Get(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
        {
            return results.First(r => r.Name == name).AsEnumerable<float>().ToArray();
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
