using HarmonyLib;
using UnityEngine;
using EHS;

namespace FlippingIsHardTAS
{
    [HarmonyPatch(typeof(PlayerInputHandler))]
    public class GameInputPatch
    {
        public static InputMacroSystem MacroSystem;

        [HarmonyPatch(nameof(PlayerInputHandler.MoveInput), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool MoveInputPrefix(ref Vector2 __result)
        {
            if (MacroSystem != null && MacroSystem.IsPlaying)
            {
                __result = MacroSystem.GetCurrentMoveInput();
                return false;
            }
            return true;
        }

        [HarmonyPatch(nameof(PlayerInputHandler.LookInput))]
        [HarmonyPrefix]
        public static bool LookInputPrefix(ref Vector2 __result)
        {
            if (MacroSystem != null && MacroSystem.IsPlaying)
            {
                __result = MacroSystem.GetCurrentLookInput();
                return false;
            }
            return true;
        }

        [HarmonyPatch(nameof(PlayerInputHandler.IsHeld))]
        [HarmonyPrefix]
        public static bool IsHeldPrefix(int __0, ref bool __result)
        {
            if (MacroSystem != null && MacroSystem.IsPlaying)
            {
                // Only intercept the buttons we actually record (Jump=4, Interact=8).
                // All other button IDs (pause, slowmo, frame advance, etc.) pass through
                // to the real game handler so they keep working during replay.
                if (__0 == 4 || __0 == 8)
                {
                    __result = MacroSystem.GetButtonHeld(__0);
                    return false;
                }
            }
            return true;
        }

        [HarmonyPatch(nameof(PlayerInputHandler.WasPressed))]
        [HarmonyPrefix]
        public static bool WasPressedPrefix(int __0, bool __1, ref bool __result)
        {
            if (MacroSystem != null && MacroSystem.IsPlaying)
            {
                // Same as IsHeld: only intercept recorded buttons, let others through.
                if (__0 == 4 || __0 == 8)
                {
                    __result = MacroSystem.GetButtonPressed(__0);
                    return false;
                }
            }
            return true;
        }
    }
}
