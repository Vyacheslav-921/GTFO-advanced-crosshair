using System;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// Remaining pack capacity at the visor center.
/// </summary>
internal sealed class PackHintOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;
    public static ConfigEntry<string> ToolColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MedColorHex { get; private set; } = null!;
    public static ConfigEntry<string> AmmoColorHex { get; private set; } = null!;
    public static ConfigEntry<string> DisinfColorHex { get; private set; } = null!;

    private const float TmpPointsPerHudUnit = 10f;
    private const float ColorSaturationBoost = 1.1f;

    private TextMeshPro? _capacityText;
    private string? _appliedCapacity;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private float _appliedOpacity = -1f;
    private bool _errorLogged;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "5 - Packs Hints",
            "Enabled",
            true,
            "Show remaining pack capacity at the crosshair center while holding a resource pack.");

        Size = config.Bind(
            "5 - Packs Hints",
            "Size",
            18,
            new ConfigDescription(
                "Pack capacity hint height in HUD units.",
                new AcceptableValueRange<int>(2, 48)));

        Opacity = config.Bind(
            "5 - Packs Hints",
            "Opacity",
            0.7f,
            new ConfigDescription(
                "Pack hint text opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        ToolColorHex = config.Bind(
            "5 - Packs Hints",
            "Tool color",
            "6eccff",
            "TOOL pack capacity color as RRGGBB or RRGGBBAA. Example: FF3355.");

        MedColorHex = config.Bind(
            "5 - Packs Hints",
            "Medpack color",
            "ff7893",
            "HP pack capacity color as RRGGBB or RRGGBBAA. Example: FF3355.");

        AmmoColorHex = config.Bind(
            "5 - Packs Hints",
            "Ammo color",
            "e0d489",
            "AMMO pack capacity color as RRGGBB or RRGGBBAA. Example: FF3355.");

        DisinfColorHex = config.Bind(
            "5 - Packs Hints",
            "Disinf color",
            "87a0e1",
            "DISINF pack capacity color as RRGGBB or RRGGBBAA. Example: FF3355.");
    }

    public bool Update(Transform? parent, int layer, bool inHudContext)
    {
        if (!Enabled.Value || !inHudContext || !TryGetHeldPack(out var capacity, out var color, out var colorHex))
        {
            Hide();
            return false;
        }

        if (!Alive(parent))
            return false;

        if (!Alive(_capacityText))
            _capacityText = Create(parent!, layer, "Pack Capacity Hint", new Vector2(0.5f, 0.5f), TextAlignmentOptions.Center);
        if (!Alive(_capacityText))
            return false;

        var size = Mathf.Clamp(Size.Value, 2, 48);
        var opacity = Mathf.Clamp01(Opacity.Value);
        color.a *= opacity;
        var styleChanged = colorHex != _appliedColorHex
            || size != _appliedSize
            || !Mathf.Approximately(opacity, _appliedOpacity);
        ApplyLabel(
            _capacityText!,
            capacity,
            color,
            size,
            Vector2.zero,
            ref _appliedCapacity,
            updateLayout: capacity != _appliedCapacity || styleChanged);

        _appliedColorHex = colorHex;
        _appliedSize = size;
        _appliedOpacity = opacity;
        return true;
    }

    public bool TryGetContentBottom(out float bottom)
    {
        return TryGetTextBottom(_capacityText, out bottom);
    }

    public void Detach()
    {
        DestroyText(ref _capacityText);
        ResetAppliedStyle();
    }

    private void Hide()
    {
        ResetAppliedStyle();
        HideText(_capacityText);
    }

    private static bool TryGetTextBottom(TextMeshPro? text, out float bottom)
    {
        bottom = 0f;
        if (!Alive(text) || !text!.gameObject.activeSelf)
            return false;

        var rect = text.rectTransform;
        bottom = rect.anchoredPosition.y - rect.sizeDelta.y * rect.pivot.y;
        return true;
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

    private TextMeshPro? Create(
        Transform parent,
        int layer,
        string name,
        Vector2 pivot,
        TextAlignmentOptions alignment)
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
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(160f, 40f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            var text = go.AddComponent<TextMeshPro>();
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
            text.alignment = alignment;
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

            LogOnce($"Pack hint create failed: {ex.Message}");
            return null;
        }
    }

    private bool TryGetHeldPack(out string capacity, out Color color, out string colorHex)
    {
        capacity = string.Empty;
        color = Color.white;
        colorHex = string.Empty;

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

            var pack = item!.TryCast<ResourcePackFirstPerson>();
            if (!Alive(pack) || !TryGetPackStyle(pack!.m_packType, out color, out colorHex))
                return false;

            capacity = RemainingUses(pack!).ToString();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"Pack hint update failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetPackStyle(
        eResourceContainerSpawnType packType,
        out Color color,
        out string colorHex)
    {
        switch (packType)
        {
            case eResourceContainerSpawnType.AmmoTool:
                colorHex = ToolColorHex.Value;
                color = ParseColor(colorHex, new Color(0.431f, 0.800f, 1f, 1f));
                return true;
            case eResourceContainerSpawnType.Health:
                colorHex = MedColorHex.Value;
                color = ParseColor(colorHex, new Color(1f, 0.470588f, 0.576471f, 1f));
                return true;
            case eResourceContainerSpawnType.AmmoWeapon:
                colorHex = AmmoColorHex.Value;
                color = ParseColor(colorHex, new Color(0.878f, 0.831f, 0.537f, 1f));
                return true;
            case eResourceContainerSpawnType.Disinfection:
                colorHex = DisinfColorHex.Value;
                color = ParseColor(colorHex, new Color(0.529f, 0.627f, 0.882f, 1f));
                return true;
            default:
                color = Color.white;
                colorHex = string.Empty;
                return false;
        }
    }

    private static int RemainingUses(ResourcePackFirstPerson pack)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack) || !Alive(backpack!.AmmoStorage) || !Alive(backpack.AmmoStorage!.ResourcePackAmmo))
            return 0;

        var ammo = backpack.AmmoStorage.ResourcePackAmmo!;
        if (pack.m_packType == eResourceContainerSpawnType.AmmoTool)
            return ToolPackUses(ammo);

        if (ammo.BulletsInPack > 0)
            return ammo.BulletsInPack;

        if (ammo.CostOfBullet > 0.0001f)
            return Mathf.Max(0, Mathf.RoundToInt(ammo.AmmoInPack / ammo.CostOfBullet));

        return ammo.AmmoInPack > 0.0001f ? 1 : 0;
    }

    /// <summary>
    /// A tool refill stores a fraction of class-tool ammo. That fraction is not the charge count.
    /// Charges are the bullet total only while the slot's maximum stays in pack-use range.
    /// </summary>
    private static int ToolPackUses(InventorySlotAmmo ammo)
    {
        if (ammo.CostOfBullet > 0.0001f && ammo.AmmoMaxCap > 0.0001f)
        {
            var maxUses = Mathf.FloorToInt((ammo.AmmoMaxCap / ammo.CostOfBullet) + 0.001f);
            if (maxUses > 0 && maxUses <= 20)
            {
                var uses = Mathf.FloorToInt((ammo.AmmoInPack / ammo.CostOfBullet) + 0.001f);
                return Mathf.Clamp(uses, 0, maxUses);
            }
        }

        if (ammo.BulletsMaxCap > 0 && ammo.BulletsMaxCap <= 20)
            return Mathf.Clamp(ammo.BulletsInPack, 0, ammo.BulletsMaxCap);

        return ammo.AmmoInPack > 0.0001f || ammo.RelInPack > 0.0001f ? 1 : 0;
    }

    private void ResetAppliedStyle()
    {
        _appliedCapacity = null;
        _appliedColorHex = null;
        _appliedSize = -1;
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

        var parsed = ColorUtility.TryParseHtmlString(text, out var color) ? color : fallback;
        return BoostSaturation(parsed);
    }

    private static Color BoostSaturation(Color color)
    {
        Color.RGBToHSV(color, out var hue, out var saturation, out var value);
        saturation = Mathf.Clamp01(saturation * ColorSaturationBoost);
        var boosted = Color.HSVToRGB(hue, saturation, value);
        boosted.a = color.a;
        return boosted;
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
