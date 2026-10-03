using System;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using EFT;
using HarmonyLib;

namespace HeadTracking.Game
{
    /// <summary>
    /// The private game state the camera hook reads every frame, resolved once at startup into
    /// delegates. Each is optional: a client update that renames one degrades a single feature,
    /// and the startup log says which.
    /// </summary>
    internal static class GameAccess
    {
        private const string HorizontalField = "_horizontal";
        private const string VerticalField = "_vertical";

        private static AccessTools.FieldRef<Player, float> _horizontal;
        private static AccessTools.FieldRef<Player, float> _vertical;
        private static AccessTools.FieldRef<bool> _ignoreInput;

        /// <summary>
        /// True when the mouse freelook angles are read raw. Otherwise they are taken from
        /// <see cref="Player.HeadRotation"/>, whose pitch the game has already scaled down for
        /// looking over the shoulder; close, not exact.
        /// </summary>
        internal static bool HasRawLook => _horizontal != null && _vertical != null;

        internal static bool HasIgnoreInputFlag => _ignoreInput != null;

        internal static void Resolve(ManualLogSource log)
        {
            _horizontal = FloatField(HorizontalField, log);
            _vertical = FloatField(VerticalField, log);
            log.LogInfo(HasRawLook
                ? "Mouse freelook angles: reading Player." + HorizontalField + " and Player." + VerticalField + " directly."
                : "Mouse freelook angles: falling back to Player.HeadRotation (pitch slightly off when combining with mouse freelook).");

            FieldInfo flag = FindIgnoreInputField(log);
            if (flag != null)
            {
                _ignoreInput = AccessTools.StaticFieldRefAccess<bool>(flag);
                log.LogInfo("Screen-open flag: GamePlayerOwner." + flag.Name + " (the field SetIgnoreInput writes).");
            }
            else
            {
                log.LogWarning("Screen-open flag not found. Tracking will still pause while the cursor shows, which covers every UI screen.");
            }
        }

        internal static void ReadLook(Player player, out float horizontal, out float vertical)
        {
            if (HasRawLook)
            {
                horizontal = _horizontal(player);
                vertical = _vertical(player);
            }
            else
            {
                horizontal = player.HeadRotation.y;
                vertical = player.HeadRotation.x;
            }
        }

        internal static bool IgnoreInput()
        {
            return _ignoreInput != null && _ignoreInput();
        }

        private static AccessTools.FieldRef<Player, float> FloatField(string name, ManualLogSource log)
        {
            FieldInfo field = AccessTools.Field(typeof(Player), name);
            if (field == null || field.FieldType != typeof(float) || field.IsStatic)
            {
                log.LogWarning("Player." + name + " not found as an instance float.");
                return null;
            }

            return AccessTools.FieldRefAccess<Player, float>(field);
        }

        /// <summary>
        /// <c>GamePlayerOwner.SetIgnoreInput(bool)</c> is <c>ldarg.0; stsfld flag; ret</c>. Every
        /// screen except the battle UI and trader dialog calls it with true on open. Found through
        /// the store rather than by the field's name, and insists on exactly one static bool.
        /// </summary>
        private static FieldInfo FindIgnoreInputField(ManualLogSource log)
        {
            MethodInfo setter = AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.SetIgnoreInput), new[] { typeof(bool) });
            if (setter == null)
            {
                log.LogWarning("GamePlayerOwner.SetIgnoreInput(bool) not found.");
                return null;
            }

            FieldInfo found = null;
            try
            {
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(setter))
                {
                    if (instruction.opcode != OpCodes.Stsfld)
                    {
                        continue;
                    }

                    if (found != null || !(instruction.operand is FieldInfo field) || field.FieldType != typeof(bool))
                    {
                        log.LogWarning("GamePlayerOwner.SetIgnoreInput no longer stores to exactly one static bool.");
                        return null;
                    }

                    found = field;
                }
            }
            catch (Exception e)
            {
                log.LogWarning("Could not read GamePlayerOwner.SetIgnoreInput's IL: " + e.Message);
                return null;
            }

            return found;
        }
    }
}
