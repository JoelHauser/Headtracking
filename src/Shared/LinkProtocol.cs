using System;
using System.IO;
using System.Text;

namespace HeadTracking.Shared
{
    // The link between HeadTracking.exe (the app) and the BepInEx plugin (the mod), over UDP on
    // 127.0.0.1. Two one-way channels:
    //   app -> mod, mod's port (default 4243): Pose, ~125 per second; GameSettings, on change and
    //                                          once a second so a game started later picks them up.
    //   mod -> app, app's port (default 4244): Status, 10 per second; Command, when an in-game
    //                                          hotkey is pressed, and Hello until settings arrive.
    //
    // Every datagram starts with the same 6-byte header: magic "HTK1", protocol version, type.
    // Readers ignore unknown types and trailing bytes, so a newer app and an older mod (or the
    // other way round) degrade to "that field is missing" instead of garbage.

    public enum LinkMessageType : byte
    {
        Pose = 1,
        GameSettings = 2,
        Status = 3,
        Command = 4,
    }

    public enum LinkCommand : byte
    {
        /// <summary>The mod started, or has no settings yet: please send them.</summary>
        Hello = 1,
        Recenter = 2,
        Toggle = 3,
    }

    /// <summary>Mirrors the app's tracker state, for display in the mod's log.</summary>
    public enum LinkTrackState : byte
    {
        NoData = 0,
        Tracking = 1,
        Holding = 2,
        Returning = 3,
        Lost = 4,
    }

    [Flags]
    public enum KeyModifiers
    {
        None = 0,
        Control = 1,
        Shift = 2,
        Alt = 4,
    }

    /// <summary>
    /// The head offset to apply, already through the app's whole pipeline (centre, smoothing,
    /// dead zones, curves, caps, tracking-loss handling). Game convention: degrees, positive yaw
    /// left, positive pitch down.
    /// </summary>
    public struct PoseMessage
    {
        public uint Sequence;
        public LinkTrackState State;
        public bool Enabled;
        public float Yaw;
        public float Pitch;
    }

    /// <summary>The settings only the mod can act on, because they depend on game state.</summary>
    public sealed class GameSettingsMessage
    {
        public uint Revision;
        public bool PauseWhileAiming = true;
        public bool PauseWhenCursorVisible = true;
        public bool PauseWhenUnfocused = true;
        public float PauseFadeMs = 250;

        /// <summary>
        /// Time constant of the plugin's critically damped follow toward the newest pose, run every
        /// rendered frame. Turns camera-rate steps (30 Hz) into continuous motion. 0 is off.
        /// </summary>
        public float MotionSmoothingMs = 60;

        /// <summary>With no pose for this long the app is treated as gone and the view eases home.</summary>
        public float LinkTimeoutMs = 300;

        /// <summary>Unity KeyCode values.</summary>
        public int ToggleKey = 289;     // F8
        public KeyModifiers ToggleModifiers;
        public int RecenterKey = 288;   // F7
        public KeyModifiers RecenterModifiers;

        public GameSettingsMessage Clone()
        {
            return (GameSettingsMessage)MemberwiseClone();
        }
    }

    public struct StatusMessage
    {
        public uint Sequence;
        public bool InRaid;
        public bool Applying;
        public bool HasSettings;
        public int PauseReasons;
        public float AppliedYaw;
        public float AppliedPitch;
        public float HookMicros;
        public float GameFps;
        public string ModVersion;
    }

    public static class LinkProtocol
    {
        public const uint Magic = 0x314B5448; // "HTK1" as little-endian bytes
        /// <summary>2 since 0.3.0: zoom removed, motion smoothing added. A mismatched app and plugin ignore each other.</summary>
        public const byte Version = 2;
        public const int HeaderSize = 6;
        public const int DefaultModPort = 4243;
        public const int DefaultAppPort = 4244;

        public static bool TryReadHeader(byte[] buffer, int length, out LinkMessageType type)
        {
            type = 0;
            if (buffer == null || length < HeaderSize || BitConverter.ToUInt32(buffer, 0) != Magic || buffer[4] != Version)
            {
                return false;
            }

            type = (LinkMessageType)buffer[5];
            return true;
        }

        // ---- Pose ------------------------------------------------------------------

        public static byte[] Encode(PoseMessage m)
        {
            return Write(LinkMessageType.Pose, w =>
            {
                w.Write(m.Sequence);
                w.Write((byte)m.State);
                w.Write(m.Enabled);
                w.Write(m.Yaw);
                w.Write(m.Pitch);
            });
        }

        public static bool TryDecode(byte[] buffer, int length, out PoseMessage m)
        {
            PoseMessage result = default;
            bool ok = Read(buffer, length, LinkMessageType.Pose, r =>
            {
                result.Sequence = r.ReadUInt32();
                result.State = (LinkTrackState)r.ReadByte();
                result.Enabled = r.ReadBoolean();
                result.Yaw = r.ReadSingle();
                result.Pitch = r.ReadSingle();
            });
            m = result;
            return ok && Finite(m.Yaw) && Finite(m.Pitch);
        }

        // ---- GameSettings ------------------------------------------------------------

        public static byte[] Encode(GameSettingsMessage m)
        {
            return Write(LinkMessageType.GameSettings, w =>
            {
                w.Write(m.Revision);
                w.Write(m.PauseWhileAiming);
                w.Write(m.PauseWhenCursorVisible);
                w.Write(m.PauseWhenUnfocused);
                w.Write(m.PauseFadeMs);
                w.Write(m.MotionSmoothingMs);
                w.Write(m.LinkTimeoutMs);
                w.Write(m.ToggleKey);
                w.Write((int)m.ToggleModifiers);
                w.Write(m.RecenterKey);
                w.Write((int)m.RecenterModifiers);
            });
        }

        public static bool TryDecode(byte[] buffer, int length, out GameSettingsMessage m)
        {
            GameSettingsMessage result = new GameSettingsMessage();
            bool ok = Read(buffer, length, LinkMessageType.GameSettings, r =>
            {
                result.Revision = r.ReadUInt32();
                result.PauseWhileAiming = r.ReadBoolean();
                result.PauseWhenCursorVisible = r.ReadBoolean();
                result.PauseWhenUnfocused = r.ReadBoolean();
                result.PauseFadeMs = r.ReadSingle();
                result.MotionSmoothingMs = r.ReadSingle();
                result.LinkTimeoutMs = r.ReadSingle();
                result.ToggleKey = r.ReadInt32();
                result.ToggleModifiers = (KeyModifiers)r.ReadInt32();
                result.RecenterKey = r.ReadInt32();
                result.RecenterModifiers = (KeyModifiers)r.ReadInt32();
            });
            m = ok ? result : null;
            return ok;
        }

        // ---- Status ------------------------------------------------------------------

        public static byte[] Encode(StatusMessage m)
        {
            return Write(LinkMessageType.Status, w =>
            {
                w.Write(m.Sequence);
                w.Write(m.InRaid);
                w.Write(m.Applying);
                w.Write(m.HasSettings);
                w.Write(m.PauseReasons);
                w.Write(m.AppliedYaw);
                w.Write(m.AppliedPitch);
                w.Write(m.HookMicros);
                w.Write(m.GameFps);
                w.Write(m.ModVersion ?? "");
            });
        }

        public static bool TryDecode(byte[] buffer, int length, out StatusMessage m)
        {
            StatusMessage result = default;
            bool ok = Read(buffer, length, LinkMessageType.Status, r =>
            {
                result.Sequence = r.ReadUInt32();
                result.InRaid = r.ReadBoolean();
                result.Applying = r.ReadBoolean();
                result.HasSettings = r.ReadBoolean();
                result.PauseReasons = r.ReadInt32();
                result.AppliedYaw = r.ReadSingle();
                result.AppliedPitch = r.ReadSingle();
                result.HookMicros = r.ReadSingle();
                result.GameFps = r.ReadSingle();
                result.ModVersion = r.ReadString();
            });
            m = result;
            return ok;
        }

        // ---- Command -----------------------------------------------------------------

        public static byte[] Encode(LinkCommand command)
        {
            return Write(LinkMessageType.Command, w => w.Write((byte)command));
        }

        public static bool TryDecode(byte[] buffer, int length, out LinkCommand command)
        {
            LinkCommand result = 0;
            bool ok = Read(buffer, length, LinkMessageType.Command, r => result = (LinkCommand)r.ReadByte());
            command = result;
            return ok;
        }

        // ---- plumbing ----------------------------------------------------------------

        private static byte[] Write(LinkMessageType type, Action<BinaryWriter> body)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write((byte)type);
                body(w);
                w.Flush();
                return stream.ToArray();
            }
        }

        private static bool Read(byte[] buffer, int length, LinkMessageType expected, Action<BinaryReader> body)
        {
            if (!TryReadHeader(buffer, length, out LinkMessageType type) || type != expected)
            {
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(buffer, HeaderSize, length - HeaderSize, false))
                using (BinaryReader r = new BinaryReader(stream, Encoding.UTF8))
                {
                    body(r);
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool Finite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }
    }
}
