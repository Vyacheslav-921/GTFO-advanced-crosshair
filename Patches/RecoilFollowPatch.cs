using HarmonyLib;

namespace V921.Patches;

/// <summary>
/// Runs after the game integrates recoil, same point AccurateCrosshair uses to
/// move the vanilla reticle from <see cref="FPS_RecoilSystem.CurrentVSPos"/>.
/// </summary>
[HarmonyPatch(typeof(FPS_RecoilSystem), nameof(FPS_RecoilSystem.FPS_Update))]
internal static class RecoilFollowPatch
{
    [HarmonyPostfix]
    private static void AfterRecoilUpdate()
    {
        CenterDotOverlay.Current?.ApplyRecoil();
    }
}
