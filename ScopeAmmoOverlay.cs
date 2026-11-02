using System;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// Magazine and reserve ammo below the visor center while aiming through a magnified scope.
/// The upper line is rounds in the magazine over magazine size. The lower line is reserve ammo.
/// </summary>
internal sealed class ScopeAmmoOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<float> Margin { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<bool> LowColorEnabled { get; private set; } = null!;
    public static ConfigEntry<string> LowColorHex { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;

    private const float TmpPointsPerHudUnit = 10f;
    private const float ReserveSizeScale = 0.85f;

    private TextMeshPro? _magazineText;
    private TextMeshPro? _reserveText;
    private string? _appliedMagazine;
    private string? _appliedReserve;
    private string? _appliedColorHex;
    private string? _appliedLowColorHex;
    private bool _appliedLowColorEnabled;
    private int _appliedSize = -1;
    private float _appliedMargin = -1f;
    private bool _errorLogged;
    private bool _hidden = true;
    private int _clip = -1;
    private int _magazineSize = -1;
    private int _reserveRounds = -1;
    private string _magazineValue = string.Empty;
    private string _reserveValue = string.Empty;
    private Color _fullColor = Color.white;
    private Color _lowColor = new(1f, 0.165f, 0.165f, 1f);
    private string? _parsedColorHex;
    private string? _parsedLowHex;

    public static void Bind(ConfigFile config)
    {
        const string section = "8 - Weapon Ammo";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "While aiming through a magnified scope, show magazine rounds and reserve ammo below the screen center.");

        Margin = config.Bind(
            section,
            "Margin",
            110f,
            new ConfigDescription(
                "Distance from the screen center to the weapon ammo hint.",
                new AcceptableValueRange<float>(0f, 480f)));

        ColorHex = config.Bind(
            section,
            "Color",
            "ffffff",
            "Weapon ammo color at a full magazine or full reserve, as RRGGBB or RRGGBBAA. Example: FFFFFF.");

        LowColorEnabled = config.Bind(
            section,
            "Low amount red color hint",
            true,
            "Shift each weapon ammo line from Color toward Low color as that ammo pool runs out. The magazine line follows rounds left in the magazine. The reserve line follows ammo outside the magazine.");

        LowColorHex = config.Bind(
            section,
            "Low color",
            "ff2a2a",
            "Color a line reaches when that ammo pool is empty, as RRGGBB or RRGGBBAA. Used while Low amount red color hint is on.");

        Size = config.Bind(
            section,
            "Size",
            18,
            new ConfigDescription(
                "Magazine line height in HUD units. Reserve ammo uses a smaller size.",
                new AcceptableValueRange<int>(2, 48)));
    }

    public bool Update(Transform? parent, int layer, bool scoped)
    {
        if (!Enabled.Value || !scoped || !TryGetAmmo(out var magazine, out var reserve, out var magazineLeft, out var reserveLeft))
        {
            Hide();
            return false;
        }

        if (!Alive(parent))
            return false;

        if (!Alive(_magazineText))
            _magazineText = Create(parent!, layer, "Scope Magazine");
        if (!Alive(_reserveText))
            _reserveText = Create(parent!, layer, "Scope Reserve");
        if (!Alive(_magazineText) || !Alive(_reserveText))
            return false;

        var size = Mathf.Clamp(Size.Value, 2, 48);
        var reserveSize = Mathf.Max(2, Mathf.RoundToInt(size * ReserveSizeScale));
        var margin = Mathf.Clamp(Margin.Value, 0f, 480f);
        var colorHex = ColorHex.Value;
        var lowColorHex = LowColorHex.Value;
        var lowColorEnabled = LowColorEnabled.Value;
        if (colorHex != _parsedColorHex || lowColorHex != _parsedLowHex)
        {
            _parsedColorHex = colorHex;
            _parsedLowHex = lowColorHex;
            _fullColor = ParseColor(colorHex, Color.white);
            _lowColor = ParseColor(lowColorHex, new Color(1f, 0.165f, 0.165f, _fullColor.a));
        }

        var styleChanged = colorHex != _appliedColorHex
            || lowColorHex != _appliedLowColorHex
            || lowColorEnabled != _appliedLowColorEnabled
            || size != _appliedSize;
        var magazineColor = lowColorEnabled ? Color.Lerp(_lowColor, _fullColor, magazineLeft) : _fullColor;
        var reserveColor = lowColorEnabled ? Color.Lerp(_lowColor, _fullColor, reserveLeft) : _fullColor;
        var lineStep = size * 2.2f;
        ApplyLabel(
            _magazineText!,
            magazine,
            magazineColor,
            size,
            new Vector2(0f, -margin),
            ref _appliedMagazine,
            updateLayout: magazine != _appliedMagazine
                || styleChanged
                || !Mathf.Approximately(margin, _appliedMargin));
        ApplyLabel(
            _reserveText!,
            reserve,
            reserveColor,
            reserveSize,
            new Vector2(0f, -(margin + lineStep)),
            ref _appliedReserve,
            updateLayout: reserve != _appliedReserve || styleChanged || !Mathf.Approximately(margin, _appliedMargin));

        _appliedMagazine = magazine;
        _appliedReserve = reserve;
        _appliedColorHex = colorHex;
        _appliedLowColorHex = lowColorHex;
        _appliedLowColorEnabled = lowColorEnabled;
        _appliedSize = size;
        _appliedMargin = margin;
        _hidden = false;
        return true;
    }

    public bool TryGetContentBottom(out float bottom)
    {
        var magazine = TextBottom(_magazineText);
        var reserve = TextBottom(_reserveText);
        if (float.IsNaN(magazine) && float.IsNaN(reserve))
        {
            bottom = 0f;
            return false;
        }

        if (float.IsNaN(magazine))
            bottom = reserve;
        else if (float.IsNaN(reserve))
            bottom = magazine;
        else
            bottom = Mathf.Min(magazine, reserve);
        return true;
    }

    public void Detach()
    {
        DestroyText(ref _magazineText);
        DestroyText(ref _reserveText);
        ResetAppliedStyle();
    }

    private bool TryGetAmmo(out string magazine, out string reserve, out float magazineLeft, out float reserveLeft)
    {
        magazine = string.Empty;
        reserve = string.Empty;
        magazineLeft = 0f;
        reserveLeft = 0f;

        try
        {
        if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
            return false;

        var clip = Math.Max(0, item!.GetCurrentClip());
        var mag = Math.Max(0, item.GetMaxClip());
        if (mag <= 0)
            return false;

        var ammo = SlotAmmo(item);
        var reserveRounds = CountReserve(ammo);
        if (clip != _clip || mag != _magazineSize)
        {
            _clip = clip;
            _magazineSize = mag;
            _magazineValue = clip + " / " + mag;
        }

        if (reserveRounds != _reserveRounds)
        {
            _reserveRounds = reserveRounds;
            _reserveValue = reserveRounds.ToString();
        }

        magazine = _magazineValue;
        reserve = _reserveValue;
        magazineLeft = Mathf.Clamp01(clip / (float)mag);
        reserveLeft = ReserveLeft(ammo, reserveRounds);
        return true;
        }
        catch (Exception ex)
        {
            LogOnce($"Scope ammo update failed: {ex.Message}");
            return false;
        }
    }

    private static int CountReserve(InventorySlotAmmo? ammo)
    {
        if (!Alive(ammo))
            return 0;

        if (ammo!.BulletsInPack > 0)
            return ammo.BulletsInPack;

        if (ammo.CostOfBullet > 0.0001f)
            return Mathf.Max(0, Mathf.FloorToInt(ammo.AmmoInPack / ammo.CostOfBullet));

        return 0;
    }

    /// <summary>
    /// Share of reserve ammo still outside the magazine. Bullet packs use their bullet cap.
    /// Other packs use the ammo cap, then the pack's own relative fill.
    /// </summary>
    private static float ReserveLeft(InventorySlotAmmo? ammo, int displayed)
    {
        if (displayed <= 0)
            return 0f;

        if (!Alive(ammo))
            return 1f;

        if (ammo!.BulletsInPack > 0 && ammo.BulletsMaxCap > 0)
            return Mathf.Clamp01(ammo.BulletsInPack / (float)ammo.BulletsMaxCap);

        if (ammo.AmmoMaxCap > 0.0001f)
            return Mathf.Clamp01(ammo.AmmoInPack / ammo.AmmoMaxCap);

        return Mathf.Clamp01(ammo.RelInPack);
    }

    private static InventorySlotAmmo? SlotAmmo(ItemEquippable item)
    {
        var ammoType = item.AmmoType;
        if (ammoType is AmmoType.None or AmmoType.CurrentConsumable or AmmoType.ResourcePackRel)
            return null;

        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack) || !Alive(backpack!.AmmoStorage))
            return null;

        var ammo = backpack.AmmoStorage!.GetInventorySlotAmmo(ammoType);
        return Alive(ammo) ? ammo : null;
    }

    private void Hide()
    {
        if (_hidden)
            return;

        _hidden = true;
        ResetAppliedStyle();
        HideText(_magazineText);
        HideText(_reserveText);
    }

    private static float TextBottom(TextMeshPro? text)
    {
        if (!Alive(text) || !text!.gameObject.activeSelf)
            return float.NaN;

        var rect = text.rectTransform;
        return rect.anchoredPosition.y - rect.sizeDelta.y * rect.pivot.y;
    }

    private static void ApplyLabel(
        TextMeshPro text,
        string value,
        Color color,
        int size,
        Vector2 anchoredPosition,
        ref string? appliedValue,
        bool updateLayout)
    {
        if (!text.gameObject.activeSelf)
            text.gameObject.SetActive(true);

        if (updateLayout)
        {
            text.text = value;
            text.fontSize = size * TmpPointsPerHudUnit;
            text.rectTransform.sizeDelta = new Vector2(size * 8f, size * 2.5f);
            text.color = color;
            text.ForceMeshUpdate();
            appliedValue = value;
            text.rectTransform.anchoredPosition = anchoredPosition;
        }
    }

    private TextMeshPro? Create(Transform parent, int layer, string name)
    {
        GameObject? go = null;
        try
        {
            var source = HudText();
            if (!Alive(source) || source!.font == null)
                return null;

            go = new GameObject(name);
            go.layer = layer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(160f, 40f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            var text = go.AddComponent<TextMeshPro>();
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.enableAutoSizing = false;
            text.extraPadding = true;
            text.text = string.Empty;
            text.color = new Color(1f, 1f, 1f, 0f);
            var renderer = text.renderer;
            var sourceRenderer = source.renderer;
            if (renderer != null && sourceRenderer != null)
            {
                renderer.sortingLayerID = sourceRenderer.sortingLayerID;
                renderer.sortingOrder = sourceRenderer.sortingOrder + 4;
                renderer.renderingLayerMask = sourceRenderer.renderingLayerMask;
            }

            go.SetActive(false);
            return text;
        }
        catch (Exception ex)
        {
            if (go != null)
                UnityEngine.Object.Destroy(go);

            LogOnce($"Scope ammo create failed: {ex.Message}");
            return null;
        }
    }

    private void ResetAppliedStyle()
    {
        _appliedMagazine = null;
        _appliedReserve = null;
        _appliedColorHex = null;
        _appliedLowColorHex = null;
        _appliedLowColorEnabled = false;
        _appliedSize = -1;
        _appliedMargin = -1f;
    }

    private static void HideText(TextMeshPro? text)
    {
        if (!Alive(text))
            return;

        text!.text = string.Empty;
        text.color = new Color(1f, 1f, 1f, 0f);
        if (text.gameObject.activeSelf)
            text.gameObject.SetActive(false);
    }

    private static void DestroyText(ref TextMeshPro? text)
    {
        HudLifetime.DestroyHudText(ref text);
    }

    private void LogOnce(string message)
    {
        if (_errorLogged)
            return;

        _errorLogged = true;
        Plugin.Instance.Log.LogWarning(message);
    }

    private static TextMeshPro? HudText()
    {
        var player = GuiManager.PlayerLayer;
        if (Alive(player) && Alive(player!.m_playerStatus) && Alive(player.m_playerStatus!.m_healthText))
            return player.m_playerStatus.m_healthText;

        var watermark = GuiManager.WatermarkLayer;
        if (Alive(watermark) && Alive(watermark!.m_watermark) && Alive(watermark.m_watermark!.m_watermarkText))
            return watermark.m_watermark.m_watermarkText;

        return null;
    }

    private static Color ParseColor(string raw, Color fallback)
    {
        return HudColor.Parse(raw, fallback);
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
