using HarmonyLib;

namespace V921.Patches;

/// <summary>
/// The vanilla reticle flashes here just before a charged melee auto-attack.
/// </summary>
[HarmonyPatch(typeof(CrosshairGuiLayer), nameof(CrosshairGuiLayer.TriggerChargeUpBlink))]
internal static class ChargeUpBlinkPatch
{
    [HarmonyPostfix]
    private static void AfterChargeUpBlink()
    {
        CrosshairOverlay.Current?.StartAutoAttackBlink();
    }
}
