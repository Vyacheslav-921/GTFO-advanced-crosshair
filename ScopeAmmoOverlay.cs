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
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Offset { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;

    private const float TmpPointsPerHudUnit = 10f;
    private const float ReserveSizeScale = 0.85f;

    private TextMeshPro? _magazineText;
    private TextMeshPro? _reserveText;
    private string? _appliedMagazine;
    private string? _appliedReserve;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private float _appliedOffset = -1f;
    private float _appliedOpacity = -1f;
    private bool _errorLogged;

    public static void Bind(ConfigFile config)
    {
        const string section = "8 - Scope Ammo";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "While aiming through a magnified scope, show magazine rounds and reserve ammo below the screen center.");

        Size = config.Bind(
            section,
            "Size",
            18,
            new ConfigDescription(
                "Magazine line height in HUD units. Reserve ammo uses a smaller size.",
                new AcceptableValueRange<int>(2, 48)));

        Offset = config.Bind(
            section,
            "Offset",
            150f,
            new ConfigDescription(
                "Distance below the screen center, toward the weapon.",
                new AcceptableValueRange<float>(0f, 480f)));

        Opacity = config.Bind(
            section,
            "Opacity",
            0.85f,
            new ConfigDescription(
                "Scope ammo text opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        ColorHex = config.Bind(
            section,
            "Color",
            "ffffff",
            "Scope ammo color as RRGGBB or RRGGBBAA. Example: FF3355.");
    }

    public bool Update(Transform? parent, int layer, bool scoped)
    {
        if (!Enabled.Value || !scoped || !TryGetAmmo(out var magazine, out var reserve, out var color))
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
        var offset = Mathf.Clamp(Offset.Value, 0f, 480f);
        var opacity = Mathf.Clamp01(Opacity.Value);
        var colorHex = ColorHex.Value;
        color.a *= opacity;
        var styleChanged = colorHex != _appliedColorHex
            || size != _appliedSize
            || !Mathf.Approximately(opacity, _appliedOpacity);
        var lineStep = size * 2.2f;
        ApplyLabel(
            _magazineText!,
            magazine,
            color,
            size,
            new Vector2(0f, -offset),
            ref _appliedMagazine,
            updateLayout: magazine != _appliedMagazine
                || styleChanged
                || !Mathf.Approximately(offset, _appliedOffset));
        ApplyLabel(
            _reserveText!,
            reserve,
            color,
            reserveSize,
            new Vector2(0f, -(offset + lineStep)),
            ref _appliedReserve,
            updateLayout: reserve != _appliedReserve || styleChanged || !Mathf.Approximately(offset, _appliedOffset));

        _appliedMagazine = magazine;
        _appliedReserve = reserve;
        _appliedColorHex = colorHex;
        _appliedSize = size;
        _appliedOffset = offset;
        _appliedOpacity = opacity;
        return true;
    }

    public void Detach()
    {
        DestroyText(ref _magazineText);
        DestroyText(ref _reserveText);
        ResetAppliedStyle();
    }

    private bool TryGetAmmo(out string magazine, out string reserve, out Color color)
    {
        magazine = string.Empty;
        reserve = string.Empty;
        color = ParseColor(ColorHex.Value, Color.white);

        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var player) || !Alive(player))
                return false;

            var holder = player!.FPItemHolder;
            if (!Alive(holder))
                return false;

            var item = holder!.WieldedItem;
            if (!Alive(item))
                return false;

            var clip = Math.Max(0, item!.GetCurrentClip());
            var mag = Math.Max(0, item.GetMaxClip());
            if (mag <= 0)
                return false;

            magazine = clip + " / " + mag;
            reserve = ReserveAmmo(item).ToString();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"Scope ammo update failed: {ex.Message}");
            return false;
        }
    }

    private static int ReserveAmmo(ItemEquippable item)
    {
        var ammoType = item.AmmoType;
        if (ammoType is AmmoType.None or AmmoType.CurrentConsumable or AmmoType.ResourcePackRel)
            return 0;

        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack) || !Alive(backpack!.AmmoStorage))
            return 0;

        var ammo = backpack.AmmoStorage!.GetInventorySlotAmmo(ammoType);
        if (!Alive(ammo))
            return 0;

        if (ammo!.BulletsInPack > 0)
            return ammo.BulletsInPack;

        if (ammo.CostOfBullet > 0.0001f)
            return Mathf.Max(0, Mathf.FloorToInt(ammo.AmmoInPack / ammo.CostOfBullet));

        return 0;
    }

    private void Hide()
    {
        ResetAppliedStyle();
        HideText(_magazineText);
        HideText(_reserveText);
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
        }

        text.rectTransform.anchoredPosition = anchoredPosition;
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
        _appliedSize = -1;
        _appliedOffset = -1f;
        _appliedOpacity = -1f;
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
        if (Alive(text))
            UnityEngine.Object.Destroy(text!.gameObject);

        text = null;
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
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return fallback;

        if (!text.StartsWith("#", StringComparison.Ordinal))
            text = "#" + text;

        return ColorUtility.TryParseHtmlString(text, out var color) ? color : fallback;
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
