using System;
using HeadTracking.Shared;

namespace HeadTracking.Tracking
{
    /// <summary>
    /// One head pose as OpenTrack sends it. Translation in centimetres, rotation in degrees.
    /// </summary>
    public struct Pose
    {
        public double X, Y, Z, Yaw, Pitch, Roll;

        public Pose(double x, double y, double z, double yaw, double pitch, double roll)
        {
            X = x; Y = y; Z = z; Yaw = yaw; Pitch = pitch; Roll = roll;
        }

        /// <summary>
        /// Bit-for-bit equality. OpenTrack resends its last value unchanged when the tracker loses
        /// the face (see <see cref="OpenTrackPacket"/>), so "the same doubles again" is the signal,
        /// and a tolerance would blur it.
        /// </summary>
        public bool SameBits(in Pose other)
        {
            return BitConverter.DoubleToInt64Bits(X) == BitConverter.DoubleToInt64Bits(other.X)
                && BitConverter.DoubleToInt64Bits(Y) == BitConverter.DoubleToInt64Bits(other.Y)
                && BitConverter.DoubleToInt64Bits(Z) == BitConverter.DoubleToInt64Bits(other.Z)
                && BitConverter.DoubleToInt64Bits(Yaw) == BitConverter.DoubleToInt64Bits(other.Yaw)
                && BitConverter.DoubleToInt64Bits(Pitch) == BitConverter.DoubleToInt64Bits(other.Pitch)
                && BitConverter.DoubleToInt64Bits(Roll) == BitConverter.DoubleToInt64Bits(other.Roll);
        }

        public override string ToString()
        {
            return "yaw " + Yaw.ToString("+0.00;-0.00") + " pitch " + Pitch.ToString("+0.00;-0.00")
                   + " roll " + Roll.ToString("+0.00;-0.00") + " | x " + X.ToString("0.0")
                   + " y " + Y.ToString("0.0") + " z " + Z.ToString("0.0");
        }
    }

    public enum PacketResult
    {
        Ok,
        /// <summary>Longer than 48 bytes. The first 48 were used.</summary>
        OkOversized,
        TooShort,
        NotFinite,
        OutOfRange,
    }

    /// <summary>
    /// OpenTrack's "UDP over network" output. From its source (proto-udp/ftnoir_protocol_ftn.cpp):
    /// <c>writeDatagram((const char*) headpose, sizeof(double[6]), ...)</c>, so a datagram is
    /// exactly 48 bytes: six native doubles in the order x, y, z, yaw, pitch, roll. There is no
    /// header, version or frame counter. Native means little-endian on every PC that runs EFT.
    /// </summary>
    public static class OpenTrackPacket
    {
        public const int Size = 48;

        /// <summary>
        /// Anything beyond this is not a head angle. OpenTrack wraps rotation to +-180, and
        /// translation is centimetres from the camera.
        /// </summary>
        private const double Sane = 10000.0;

        public static PacketResult TryParse(byte[] buffer, int length, out Pose pose)
        {
            pose = default;
            if (buffer == null || length < Size)
            {
                return PacketResult.TooShort;
            }

            double x = Read(buffer, 0);
            double y = Read(buffer, 8);
            double z = Read(buffer, 16);
            double yaw = Read(buffer, 24);
            double pitch = Read(buffer, 32);
            double roll = Read(buffer, 40);

            if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(yaw) || !Finite(pitch) || !Finite(roll))
            {
                return PacketResult.NotFinite;
            }

            if (Math.Abs(x) > Sane || Math.Abs(y) > Sane || Math.Abs(z) > Sane
                || Math.Abs(yaw) > 360.0 || Math.Abs(pitch) > 360.0 || Math.Abs(roll) > 360.0)
            {
                return PacketResult.OutOfRange;
            }

            pose = new Pose(x, y, z, yaw, pitch, roll);
            return length == Size ? PacketResult.Ok : PacketResult.OkOversized;
        }

        /// <summary>Writes a pose in the same layout. Used by the tests and the test sender.</summary>
        public static byte[] Encode(in Pose pose)
        {
            byte[] buffer = new byte[Size];
            Write(buffer, 0, pose.X);
            Write(buffer, 8, pose.Y);
            Write(buffer, 16, pose.Z);
            Write(buffer, 24, pose.Yaw);
            Write(buffer, 32, pose.Pitch);
            Write(buffer, 40, pose.Roll);
            return buffer;
        }

        private static double Read(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToDouble(buffer, offset);
            }

            byte[] swapped = new byte[8];
            Array.Copy(buffer, offset, swapped, 0, 8);
            Array.Reverse(swapped);
            return BitConverter.ToDouble(swapped, 0);
        }

        private static void Write(byte[] buffer, int offset, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            Array.Copy(bytes, 0, buffer, offset, 8);
        }

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
