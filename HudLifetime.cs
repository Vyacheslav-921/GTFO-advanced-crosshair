using TMPro;
using UnityEngine;

namespace V921;

internal static class HudLifetime
{
    /// <summary>
    /// Drops a HUD label. On level exit the Il2Cpp wrapper can still report a pointer
    /// while <c>Component.gameObject</c> throws a native null reference.
    /// </summary>
    public static void DestroyHudText(ref TextMeshPro? text)
    {
        var current = text;
        text = null;
        if (current == null || current.Pointer == IntPtr.Zero)
            return;

        try
        {
            var go = current.gameObject;
            if (go != null)
                UnityEngine.Object.Destroy(go);
        }
        catch (Exception)
        {
        }
    }
}
