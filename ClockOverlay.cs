using System;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// Drives the persistent helmet-corner watermark text with the local system clock.
///
/// Game chain this binds to (GTFO UI, not Harmony-patched):
///   GuiManager.WatermarkLayer          — static accessor for the watermark UI layer
///     → .m_watermark                   — PUI_Watermark instance
///       → .m_watermarkText             — TMPro.TextMeshPro that normally shows build/version text
///
/// Why poll instead of Harmony?
/// Patching watermark Setup/Awake can run before GuiManager is ready and crash startup.
/// Polling until the object exists is safer for a "tweak existing UI text" feature.
///
/// Types live in the profile interop assemblies (BepInEx\interop\ from local.props).
/// Inspect with ILSpy / dnSpyEx. Watermark UI is typically around GuiManager / PUI_Watermark.
/// </summary>
internal class ClockOverlay : MonoBehaviour
{
    public static ClockOverlay? Instance { get; private set; }

    /// <summary>Cached pointer to the game's watermark TextMeshPro once found.</summary>
    private TextMeshPro? _watermarkText;

    /// <summary>Last second we wrote; avoids rewriting the same string 60×/sec.</summary>
    private int _cachedSecond = -1;

    /// <summary>Unity unscaledTime gate so bind attempts run ~twice per second, not every frame.</summary>
    private float _nextBindAttempt;

    /// <summary>
    /// IL2CPP MonoBehaviour ctor — required so the interop layer can construct this from native.
    /// https://docs.bepinex.dev/articles/advanced/il2cpp_interop.html
    /// </summary>
    public ClockOverlay(IntPtr ptr) : base(ptr)
    {
    }

    /// <summary>Unity lifecycle: first frame this component exists. Stores the singleton.</summary>
    private void Awake()
    {
        Instance = this;
    }

    /// <summary>
    /// Unity lifecycle: every frame.
    /// 1) If not bound yet → try to find PUI_Watermark text.
    /// 2) If bound → set text to HH:mm:ss when the second changes.
    /// </summary>
    private void Update()
    {
        // Pointer == Zero means the IL2CPP object was destroyed / invalid — rebind.
        if (_watermarkText == null || _watermarkText.Pointer == IntPtr.Zero)
        {
            TryBindWatermark();
            return;
        }

        var now = DateTimeOffset.Now;

        // Only touch TMP when the displayed second actually changes.
        if (now.Second == _cachedSecond)
            return;

        _cachedSecond = now.Second;

        // This is the actual "tweak": overwrite whatever the game put in m_watermarkText.
        // Target field: PUI_Watermark.m_watermarkText (TextMeshPro).text
        _watermarkText.text = now.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Walks GuiManager → WatermarkLayer → PUI_Watermark → m_watermarkText.
    /// Retries every 0.5s until the UI exists (menu / in-expedition after load).
    ///
    /// Game APIs touched (read-only until bind succeeds):
    /// - GuiManager.WatermarkLayer (static property)
    /// - WatermarkLayer.m_watermark (PUI_Watermark)
    /// - PUI_Watermark.m_watermarkText (TextMeshPro)
    /// </summary>
    private void TryBindWatermark()
    {
        // Throttle: Time.unscaledTime ignores pause and keeps ticking in menus.
        if (Time.unscaledTime < _nextBindAttempt)
            return;

        _nextBindAttempt = Time.unscaledTime + 0.5f;

        try
        {
            // GuiManager is a GTFO singleton-style UI hub (game assembly, via interop).
            var layer = GuiManager.WatermarkLayer;
            if (layer == null)
                return;

            // PUI_Watermark: persistent corner watermark widget.
            var pui = layer.m_watermark;
            if (pui == null)
                return;

            // The TMP label we will overwrite each second.
            var text = pui.m_watermarkText;
            if (text == null)
                return;

            _watermarkText = text;
            _cachedSecond = -1; // force an immediate refresh on next Update
            Plugin.Instance.Log.LogInfo("Clock bound to PUI_Watermark (poll).");
        }
        catch (Exception ex)
        {
            // Null / not-ready during early boot is expected; warn so crashes are visible.
            Plugin.Instance.Log.LogWarning($"Watermark bind attempt failed: {ex.Message}");
        }
    }
}
