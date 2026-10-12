using System;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using UnityEngine;

namespace V921;

/// <summary>
/// Blue circle with a white outline just under the mag hint.
/// It appears when the local player's bio tracker becomes ready, then fades away.
/// A tag and the recharge after it both hide the circle. The recharge timer is read
/// from the tool itself, so the circle can return while another item is wielded.
/// </summary>
internal sealed class BioScanReadyOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<float> Size { get; private set; } = null!;
    public static ConfigEntry<string> FillColorHex { get; private set; } = null!;
    public static ConfigEntry<string> OutlineColorHex { get; private set; } = null!;
    public static ConfigEntry<float> OutlineThickness { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;
    public static ConfigEntry<int> BlinkCount { get; private set; } = null!;
    public static ConfigEntry<float> BlinkDuration { get; private set; } = null!;
    public static ConfigEntry<float> BlinkMinimumOpacity { get; private set; } = null!;
    public static ConfigEntry<float> DisplayDuration { get; private set; } = null!;
    public static ConfigEntry<float> FadeDuration { get; private set; } = null!;
    public static ConfigEntry<float> HorizontalOffset { get; private set; } = null!;
    public static ConfigEntry<float> VerticalGap { get; private set; } = null!;

    private UI_Sprite? _outline;
    private UI_Sprite? _fill;
    private IntPtr _gearPtr;
    private EnemyScanner? _scanner;
    private float _appliedX = float.NaN;
    private float _appliedY = float.NaN;
    private float _appliedSize = float.NaN;
    private float _appliedOutlineThickness = float.NaN;
    private float _appliedOpacity = -1f;
    private string? _appliedFillColorHex;
    private string? _appliedOutlineColorHex;
    private bool _wasReady;
    private float _shownAt = -1f;
    private bool _hidden = true;
    private bool _errorLogged;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "10 - Bio Scan",
            "Enabled",
            true,
            "Show a blue circle with a white outline below the mag hint while the bio tracker can scan. The circle hides during a tag and while the tracker is recharging.");

        Size = config.Bind(
            "10 - Bio Scan",
            "Size",
            4f,
            new ConfigDescription(
                "Scan-ready circle diameter in HUD units.",
                new AcceptableValueRange<float>(1f, 16f)));

        FillColorHex = config.Bind(
            "10 - Bio Scan",
            "Fill color",
            "168cff",
            "Scan-ready circle fill color as RRGGBB or RRGGBBAA.");

        OutlineColorHex = config.Bind(
            "10 - Bio Scan",
            "Outline color",
            "ffffff",
            "Scan-ready circle outline color as RRGGBB or RRGGBBAA.");

        OutlineThickness = config.Bind(
            "10 - Bio Scan",
            "Outline thickness",
            0.45f,
            new ConfigDescription(
                "White border width in HUD units.",
                new AcceptableValueRange<float>(0f, 4f)));

        Opacity = config.Bind(
            "10 - Bio Scan",
            "Opacity",
            0.6f,
            new ConfigDescription(
                "Opacity of the scan-ready circle and its outline. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        BlinkCount = config.Bind(
            "10 - Bio Scan",
            "Blink count",
            2,
            new ConfigDescription(
                "Number of noticeable blinks when the scan becomes ready. 0 disables blinking.",
                new AcceptableValueRange<int>(0, 8)));

        BlinkDuration = config.Bind(
            "10 - Bio Scan",
            "Blink duration",
            0.6f,
            new ConfigDescription(
                "Total time in seconds used for all ready blinks.",
                new AcceptableValueRange<float>(0.1f, 3f)));

        BlinkMinimumOpacity = config.Bind(
            "10 - Bio Scan",
            "Blink minimum opacity",
            0.12f,
            new ConfigDescription(
                "Lowest share of the configured opacity reached during each blink.",
                new AcceptableValueRange<float>(0f, 1f)));

        DisplayDuration = config.Bind(
            "10 - Bio Scan",
            "Display duration",
            1f,
            new ConfigDescription(
                "Seconds to show the indicator at full opacity after the bio scan becomes ready. 0 keeps it visible indefinitely.",
                new AcceptableValueRange<float>(0f, 30f)));

        FadeDuration = config.Bind(
            "10 - Bio Scan",
            "Fade duration",
            0.35f,
            new ConfigDescription(
                "Seconds used to smoothly fade the indicator after its display duration.",
                new AcceptableValueRange<float>(0.05f, 5f)));

        HorizontalOffset = config.Bind(
            "10 - Bio Scan",
            "Horizontal offset",
            0f,
            new ConfigDescription(
                "Horizontal offset from the center of the magazine pip stack.",
                new AcceptableValueRange<float>(-64f, 64f)));

        VerticalGap = config.Bind(
            "10 - Bio Scan",
            "Vertical gap",
            3f,
            new ConfigDescription(
                "Gap below the magazine pip stack in HUD units.",
                new AcceptableValueRange<float>(-16f, 64f)));
    }

    public void Create(RectTransform parent, int layer, SpriteRenderer? reference, Sprite? disc)
    {
        if (disc == null)
            return;

        _outline = CrosshairOverlay.CreateSprite(parent, disc, "Bio Scan Outline", layer, reference);
        _fill = CrosshairOverlay.CreateSprite(parent, disc, "Bio Scan Fill", layer, reference);
        if (reference != null)
        {
            if (_outline.m_sprite != null)
                _outline.m_sprite.sortingOrder = reference.sortingOrder + 13;
            if (_fill.m_sprite != null)
                _fill.m_sprite.sortingOrder = reference.sortingOrder + 14;
        }

        SetActive(_outline, false);
        SetActive(_fill, false);
    }

    public bool Update(bool inHudContext, float magCenterX, float magBottom)
    {
        if (!Enabled.Value || !inHudContext)
        {
            Hide();
            return false;
        }

        var ready = ScanIsReady();
        if (!ready)
        {
            _wasReady = false;
            _shownAt = -1f;
            Hide();
            return false;
        }

        if (!_wasReady)
        {
            _wasReady = true;
            _shownAt = Time.unscaledTime;
        }

        var visibility = Visibility();
        if (visibility <= 0f)
        {
            Hide();
            return false;
        }

        if (!Alive(_outline) || !Alive(_fill))
            return false;

        ApplyStyle(visibility * BlinkFactor());
        var diameter = Mathf.Clamp(Size.Value, 1f, 16f);
        var centerX = magCenterX + Mathf.Clamp(HorizontalOffset.Value, -64f, 64f);
        var centerY = magBottom
            - Mathf.Clamp(VerticalGap.Value, -16f, 64f)
            - diameter * 0.5f;
        Place(centerX, centerY);
        return true;
    }

    public void Detach()
    {
        _outline = null;
        _fill = null;
        _gearPtr = IntPtr.Zero;
        _scanner = null;
        _appliedX = float.NaN;
        _appliedY = float.NaN;
        _appliedSize = float.NaN;
        _appliedOutlineThickness = float.NaN;
        _appliedOpacity = -1f;
        _appliedFillColorHex = null;
        _appliedOutlineColorHex = null;
        _wasReady = false;
        _shownAt = -1f;
        _hidden = true;
    }

    private float Visibility()
    {
        var display = Mathf.Clamp(DisplayDuration.Value, 0f, 30f);
        if (display <= 0f)
            return 1f;

        var elapsed = Mathf.Max(0f, Time.unscaledTime - _shownAt);
        if (elapsed <= display)
            return 1f;

        var fade = Mathf.Clamp(FadeDuration.Value, 0.05f, 5f);
        var progress = Mathf.Clamp01((elapsed - display) / fade);
        return 1f - Mathf.SmoothStep(0f, 1f, progress);
    }

    private float BlinkFactor()
    {
        var count = Mathf.Clamp(BlinkCount.Value, 0, 8);
        if (count <= 0)
            return 1f;

        var duration = Mathf.Clamp(BlinkDuration.Value, 0.1f, 3f);
        var elapsed = Mathf.Max(0f, Time.unscaledTime - _shownAt);
        if (elapsed >= duration)
            return 1f;

        var minimum = Mathf.Clamp01(BlinkMinimumOpacity.Value);
        var wave = 0.5f + 0.5f * Mathf.Cos(Mathf.PI * 2f * count * elapsed / duration);
        return Mathf.Lerp(minimum, 1f, wave);
    }

    private void ApplyStyle(float visibility)
    {
        var diameter = Mathf.Clamp(Size.Value, 1f, 16f);
        var border = Mathf.Clamp(OutlineThickness.Value, 0f, diameter * 0.45f);
        var opacity = Mathf.Clamp01(Opacity.Value) * visibility;
        var fillHex = FillColorHex.Value;
        var outlineHex = OutlineColorHex.Value;
        if (Mathf.Approximately(_appliedSize, diameter)
            && Mathf.Approximately(_appliedOutlineThickness, border)
            && Mathf.Approximately(_appliedOpacity, opacity)
            && fillHex == _appliedFillColorHex
            && outlineHex == _appliedOutlineColorHex)
            return;

        _appliedSize = diameter;
        _appliedOutlineThickness = border;
        _appliedOpacity = opacity;
        _appliedFillColorHex = fillHex;
        _appliedOutlineColorHex = outlineHex;
        var outlineColor = HudColor.Parse(outlineHex, Color.white);
        outlineColor.a *= opacity;
        var fillColor = HudColor.Parse(fillHex, new Color(0.086f, 0.55f, 1f, 1f));
        fillColor.a *= opacity;
        CrosshairOverlay.SetSprite(
            _outline!,
            outlineColor,
            diameter);
        CrosshairOverlay.SetSprite(
            _fill!,
            fillColor,
            Mathf.Max(0.1f, diameter - border * 2f));
    }

    private void Place(float centerX, float centerY)
    {
        if (float.IsNaN(_appliedX)
            || float.IsNaN(_appliedY)
            || !Mathf.Approximately(_appliedX, centerX)
            || !Mathf.Approximately(_appliedY, centerY))
        {
            _appliedX = centerX;
            _appliedY = centerY;
            var position = new Vector2(centerX, centerY);
            Move(_outline, position);
            Move(_fill, position);
        }

        if (!_hidden)
            return;

        _hidden = false;
        SetActive(_outline, true);
        SetActive(_fill, true);
    }

    private void Hide()
    {
        if (_hidden)
            return;

        _hidden = true;
        _appliedX = float.NaN;
        _appliedY = float.NaN;
        SetActive(_outline, false);
        SetActive(_fill, false);
    }

    private bool ScanIsReady()
    {
        try
        {
            if (!TryGetScanner(out var scanner) || !Alive(scanner))
                return false;

            return !Cooling(scanner!.m_tagging, scanner.m_tagStartTime, EnemyScanner.TagDuration)
                && !Cooling(scanner.m_recharging, scanner.m_rechargeStartTime, scanner.m_rechargeDuration);
        }
        catch (Exception ex)
        {
            LogOnce($"Bio scan ready check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The tracker's own update clears these flags. While it is holstered that update can
    /// stall, so a flag whose timer has already elapsed still counts as finished.
    /// </summary>
    private static bool Cooling(bool active, float startedAt, float duration)
    {
        if (!active)
            return false;

        if (duration <= 0.05f)
            return true;

        return Clock.Time < startedAt + duration;
    }

    private bool TryGetScanner(out EnemyScanner? scanner)
    {
        scanner = null;
        var gear = ClassGearItem();
        if (!Alive(gear))
        {
            _gearPtr = IntPtr.Zero;
            _scanner = null;
            return false;
        }

        var ptr = gear!.Pointer;
        if (ptr != _gearPtr)
        {
            _gearPtr = ptr;
            _scanner = gear.TryCast<EnemyScanner>();
        }

        if (!Alive(_scanner))
            return false;

        scanner = _scanner;
        return true;
    }

    private static Item? ClassGearItem()
    {
        if (WieldedFrame.TryGetSlot(out var slot)
            && slot == InventorySlot.GearClass
            && WieldedFrame.TryGetItem(out var held)
            && Alive(held))
            return held;

        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack))
            return null;

        if (!backpack!.TryGetBackpackItem(InventorySlot.GearClass, out var slotItem) || !Alive(slotItem))
            return null;

        var instance = slotItem!.Instance;
        return Alive(instance) ? instance : null;
    }

    private static void Move(UI_Sprite? sprite, Vector2 position)
    {
        var rect = sprite?.m_rect;
        if (rect != null)
            rect.anchoredPosition = position;
    }

    private static void SetActive(UI_Sprite? sprite, bool active)
    {
        if (!Alive(sprite))
            return;

        if (sprite!.gameObject.activeSelf != active)
            sprite.gameObject.SetActive(active);
    }

    private void LogOnce(string message)
    {
        if (_errorLogged)
            return;

        _errorLogged = true;
        Plugin.Instance.Log.LogWarning(message);
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
