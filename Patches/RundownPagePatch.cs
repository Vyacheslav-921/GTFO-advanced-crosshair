using CellMenu;
using HarmonyLib;

namespace GtfoTestmod.Patches;

[HarmonyPatch]
internal static class RundownPagePatch
{
    [HarmonyPatch(typeof(CM_PageRundown_New), nameof(CM_PageRundown_New.Setup))]
    [HarmonyPostfix]
    private static void Setup_Postfix(CM_PageRundown_New __instance)
    {
        Plugin.Instance.Log.LogInfo($"Rundown page Setup patched. Instance={__instance != null}");
    }
}
