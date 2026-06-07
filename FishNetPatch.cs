using HarmonyLib;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Patches FishNet's client-side reconciliation to prevent it from snapping the
    /// player back to server-authoritative position during TAS playback.
    ///
    /// NOTE: This class intentionally does NOT use [HarmonyPatch] attributes so that
    /// harmony.PatchAll() does not try to auto-apply it. Instead, it is applied manually
    /// in TASPlugin.Load() inside a try/catch, so a patch failure here cannot crash the
    /// entire plugin.
    /// </summary>
    public class FishNetReconcilePatch
    {
        public static InputMacroSystem MacroSystem;

        // Manual prefix — applied via harmony.Patch() in TASPlugin.Load()
        public static bool BlockReconcilePrefix()
        {
            // If TAS is playing back a macro, prevent FishNet from reconciling the player
            // position against the server. Without this, FishNet snaps the player
            // back to server state every tick, overriding our recorded trajectory.
            if (MacroSystem != null && MacroSystem.IsPlaying)
                return false; // skip original Reconcile_Client_Start

            return true;
        }
    }
}
