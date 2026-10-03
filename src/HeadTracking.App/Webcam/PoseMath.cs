using System;

namespace HeadTracking.App.Webcam
{
    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public override string ToString() => "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ", " + Z.ToString("0.###") + ")";
    }

    /// <summary>Unit quaternion w + xi + yj + zk, Hamilton convention, as cv::Quatf.</summary>
    public struct Quat
    {
        public float W, X, Y, Z;

        public Quat(float w, float x, float y, float z)
        {
            W = w; X = x; Y = y; Z = z;
        }

        public static readonly Quat Identity = new Quat(1, 0, 0, 0);

        public Quat Conjugate => new Quat(W, -X, -Y, -Z);

        public Quat Normalized
        {
            get
            {
                float n = (float)Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
                return n > 0 ? new Quat(W / n, X / n, Y / n, Z / n) : Identity;
            }
        }

        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);
        }

        public static Quat FromAngleAxis(float angle, Vec3 unitAxis)
        {
            float s = (float)Math.Sin(angle * 0.5);
            return new Quat((float)Math.Cos(angle * 0.5), unitAxis.X * s, unitAxis.Y * s, unitAxis.Z * s);
        }

        /// <summary>q v q*, as opencv_contrib.h's rotate().</summary>
        public Vec3 Rotate(Vec3 v)
        {
            Quat r = this * new Quat(0, v.X, v.Y, v.Z) * Conjugate;
            return new Vec3(r.X, r.Y, r.Z);
        }

        /// <summary>Rotation matrix, row-major m[row, col], as cv::Quat::toRotMat3x3 for a unit quaternion.</summary>
        public float[,] ToMatrix()
        {
            float w = W, x = X, y = Y, z = Z;
            return new float[3, 3]
            {
                { 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y) },
                { 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x) },
                { 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y) },
            };
        }

        public override string ToString() => "(" + W.ToString("0.###") + "; " + X.ToString("0.###") + ", " + Y.ToString("0.###") + ", " + Z.ToString("0.###") + ")";
    }

    /// <summary>Normalised pinhole focal lengths, image plane spanning -1..1.</summary>
    public struct CamIntrinsics
    {
        public float FocalW, FocalH;

        /// <summary>OpenTrack's make_intrinsics: from a diagonal field of view in degrees.</summary>
        public static CamIntrinsics FromDiagonalFov(float aspect, float diagonalFovDegrees)
        {
            double diag = diagonalFovDegrees * Math.PI / 180.0;
            double a2 = aspect * aspect;
            double fovW = 2.0 * Math.Atan(Math.Tan(diag / 2.0) / Math.Sqrt(1.0 + 1.0 / a2));
            double fovH = 2.0 * Math.Atan(Math.Tan(diag / 2.0) / Math.Sqrt(1.0 + a2));
            return new CamIntrinsics { FocalW = (float)(1.0 / Math.Tan(0.5 * fovW)), FocalH = (float)(1.0 / Math.Tan(0.5 * fovH)) };
        }
    }

    /// <summary>A head pose in OpenTrack's output convention: degrees and centimetres.</summary>
    public struct HeadPose
    {
        public double Yaw, Pitch, Roll;
        public double X, Y, Z;
    }

    /// <summary>
    /// The geometry of OpenTrack's neuralnet tracker (tracker-neuralnet/ftnoir_tracker_neuralnet.cpp,
    /// ISC licence, Michael Welter), ported line by line so this tracker reports what OpenTrack's
    /// would for the same face.
    ///
    /// World frame (the tracker's): x points from the head toward the camera's far side (the head
    /// sits at negative x), y down, z right in the image.
    /// </summary>
    public static class PoseMath
    {
        /// <summary>Assumed vertical head size. OpenTrack's HEAD_SIZE_MM.</summary>
        public const float HeadSizeMm = 200f;

        /// <summary>Default neck-joint offset from the face, OpenTrack's offset_fwd/up/right (mm).</summary>
        public static readonly Vec3 NeckOffset = new Vec3(-100f, 0f, 0f);

        /// <summary>image_to_world(x, y, size, ...): face centre and size in pixels to millimetres.</summary>
        public static Vec3 ImageToWorld(float x, float y, float size, float referenceSizeMm, int imageWidth, int imageHeight, CamIntrinsics intrinsics)
        {
            // The model's size is about half the real vertical head size.
            float headSizeVertical = 2f * size;
            float xpos = -(intrinsics.FocalW * imageWidth * 0.5f) / headSizeVertical * referenceSizeMm;
            float zpos = (x / imageWidth * 2f - 1f) * xpos / intrinsics.FocalW;
            float ypos = (y / imageHeight * 2f - 1f) * xpos / intrinsics.FocalH;
            return new Vec3(xpos, ypos, zpos);
        }

        /// <summary>image_to_world(Quatf): its own inverse. (w,x,y,z) -> (w,-z,-y,-x).</summary>
        public static Quat ImageToWorld(Quat q)
        {
            return new Quat(q.W, -q.Z, -q.Y, -q.X);
        }

        public static Quat RotationFromTwoVectors(Vec3 a, Vec3 b)
        {
            Vec3 axis = Vec3.Cross(a, b);
            float dot = Vec3.Dot(a, b);
            float len = axis.Length;
            Vec3 normed = len > 0 ? axis * (1f / len) : new Vec3(float.NaN, float.NaN, float.NaN);
            float angle = (float)Math.Atan2(len, dot);
            if (!(IsFinite(normed.X) && IsFinite(normed.Y) && IsFinite(normed.Z)))
            {
                angle = 0f;
                normed = new Vec3(1f, 0f, 0f);
            }

            return Quat.FromAngleAxis(angle, normed);
        }

        /// <summary>
        /// transform_to_world_pose: the network's face rotation (image frame) and centre/size to a
        /// world rotation and the neck joint's position in mm. Corrects the rotation for the head
        /// sitting off the image centre (a face at the edge seen straight on is turned).
        /// </summary>
        public static void TransformToWorld(Quat faceRotation, float faceX, float faceY, float faceSize, int imageWidth, int imageHeight,
                                            CamIntrinsics intrinsics, out Quat rotation, out Vec3 position)
        {
            Vec3 faceWorld = ImageToWorld(faceX, faceY, faceSize, HeadSizeMm, imageWidth, imageHeight, intrinsics);
            Quat correction = RotationFromTwoVectors(new Vec3(-1f, 0f, 0f), faceWorld);
            rotation = correction * ImageToWorld(faceRotation);
            position = faceWorld + rotation.Rotate(NeckOffset);
        }

        /// <summary>
        /// NeuralNetTracker::data(): rotation matrix to yaw/pitch/roll in degrees (Tait-Bryan,
        /// "x forward, y up" as the source comments it) and position to centimetres.
        /// </summary>
        public static HeadPose ToOpenTrack(Quat rotation, Vec3 positionMm)
        {
            float[,] r = rotation.ToMatrix();
            // Columns mx, my, mz; mx(i) is row i of column 0.
            double mx0 = r[0, 0], mx1 = r[1, 0], mx2 = r[2, 0];
            double my1 = r[1, 1];
            double mz1 = r[1, 2];

            double yaw = Math.Atan2(mx2, mx0);
            double pitch = -Math.Atan2(-mx1, Math.Sqrt(mx2 * mx2 + mx0 * mx0));
            double roll = Math.Atan2(-mz1, my1);
            const double rad2deg = 180.0 / Math.PI;

            return new HeadPose
            {
                Yaw = rad2deg * yaw,
                Pitch = rad2deg * pitch,
                Roll = -rad2deg * roll,
                X = -positionMm.Z * 0.1,
                Y = positionMm.Y * 0.1,
                Z = -positionMm.X * 0.1,
            };
        }

        private static bool IsFinite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }
    }
}
