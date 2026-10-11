using System;
using System.Text;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// Remaining class-tool ammo, as a percent of its maximum, at the visor center.
/// Key 4 is the turret, C-foam launcher, mine deployer, or bio tracker.
/// The name stays off the reticle; the player already knows which tool is out.
/// </summary>
internal sealed class ToolHintOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<string> TurretColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FoamColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MineColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FoamMineColorHex { get; private set; } = null!;
    public static ConfigEntry<string> BioTrackerColorHex { get; private set; } = null!;

    private const float TmpPointsPerHudUnit = 10f;
    private const float ColorSaturationBoost = 1.3f;

    private enum ToolHintKind
    {
        Turret,
        Foam,
        Mine,
        FoamMine,
        BioTracker
    }

    private TextMeshPro? _capacityText;
    private string? _appliedCapacity;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private bool _errorLogged;

    public static void Bind(ConfigFile config)
    {
        const string section = "7 - Tools Hints";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "Show remaining tool capacity, from 0 to 100 percent of its maximum, at the crosshair center while holding a class tool.");

        Size = config.Bind(
            section,
            "Size",
            18,
            new ConfigDescription(
                "Tool capacity hint height in HUD units.",
                new AcceptableValueRange<int>(2, 48)));

        TurretColorHex = BindColor(config, section, "Turret color", "TURRET", "ffb15a");
        FoamColorHex = BindColor(config, section, "Foam color", "FOAM", "8dffb0");
        MineColorHex = BindColor(config, section, "Mine color", "MINE", "f8df81");
        FoamMineColorHex = BindColor(config, section, "Foam mine color", "CMINE", "cff8a3");
        BioTrackerColorHex = BindColor(config, section, "Bio tracker color", "BIO", "6ecbff");
    }

    public bool Update(Transform? parent, int layer, bool inHudContext)
    {
        if (!Enabled.Value || !inHudContext || !TryGetHeldTool(out var capacity, out var color, out var colorHex))
        {
            Hide();
            return false;
        }

        if (!Alive(parent))
            return false;

        if (!Alive(_capacityText))
            _capacityText = Create(parent!, layer, "Tool Capacity Hint");
        if (!Alive(_capacityText))
            return false;

        var size = Mathf.Clamp(Size.Value, 2, 48);
        ApplyLabel(
            _capacityText!,
            capacity,
            color,
            size,
            ref _appliedCapacity,
            updateLayout: capacity != _appliedCapacity
                || colorHex != _appliedColorHex
                || size != _appliedSize);

        _appliedColorHex = colorHex;
        _appliedSize = size;
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

    private static ConfigEntry<string> BindColor(ConfigFile config, string section, string key, string naming, string defaultHex)
    {
        return config.Bind(
            section,
            key,
            defaultHex,
            $"{naming} tool capacity color as RRGGBB or RRGGBBAA. Example: FF3355.");
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

        text.rectTransform.anchoredPosition = Vector2.zero;
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
            rect.pivot = new Vector2(0.5f, 0.5f);
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

            LogOnce($"Tool hint create failed: {ex.Message}");
            return null;
        }
    }

    private bool TryGetHeldTool(out string capacity, out Color color, out string colorHex)
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
            if (!Alive(item) || !TryGetToolStyle(item!, out color, out colorHex))
                return false;

            if (!TryGetCapacity(item!, out capacity))
                return false;

            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"Tool hint update failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetToolStyle(ItemEquippable item, out Color color, out string colorHex)
    {
        if (!TryClassify(item, out var kind))
        {
            color = Color.white;
            colorHex = string.Empty;
            return false;
        }

        colorHex = kind switch
        {
            ToolHintKind.Turret => TurretColorHex.Value,
            ToolHintKind.Foam => FoamColorHex.Value,
            ToolHintKind.Mine => MineColorHex.Value,
            ToolHintKind.FoamMine => FoamMineColorHex.Value,
            _ => BioTrackerColorHex.Value
        };
        color = ParseColor(colorHex, Color.white);
        return true;
    }

    /// <summary>
    /// Key 4 is <see cref="InventorySlot.GearClass"/>. Consumable mines stay on the item hint.
    /// </summary>
    private static bool TryClassify(ItemEquippable item, out ToolHintKind kind)
    {
        kind = default;
        var block = Alive(item.ItemDataBlock) ? item.ItemDataBlock : null;
        if (block != null && block!.inventorySlot != InventorySlot.GearClass)
            return false;

        if (Alive(item.TryCast<SentryGunFirstPerson>()))
        {
            kind = ToolHintKind.Turret;
            return true;
        }

        if (Alive(item.TryCast<GlueGun>()))
        {
            kind = ToolHintKind.Foam;
            return true;
        }

        var deployer = item.TryCast<MineDeployerFirstPerson>();
        if (Alive(deployer) && !deployer!.m_isConsumable)
        {
            var text = Identity(item);
            kind = Has(text, "glue") || Has(text, "foam")
                ? ToolHintKind.FoamMine
                : ToolHintKind.Mine;
            return true;
        }

        if (Alive(item.TryCast<EnemyScanner>()))
        {
            kind = ToolHintKind.BioTracker;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Turret, foam launcher, and mine deployer share the class-tool pool.
    /// The bio tracker is marked infinite, so it has no capacity number.
    /// </summary>
    private static bool TryGetCapacity(ItemEquippable item, out string capacity)
    {
        capacity = string.Empty;
        var block = Alive(item.ItemDataBlock) ? item.ItemDataBlock : null;
        if (block != null && block!.GUIShowAmmoInfinite)
            return false;

        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack) || !Alive(backpack!.AmmoStorage))
            return false;

        var ammo = backpack.AmmoStorage!.ClassAmmo;
        if (!Alive(ammo))
            ammo = backpack.AmmoStorage.GetInventorySlotAmmo(AmmoType.Class);
        if (!Alive(ammo))
            return false;

        var relative = ammo!.AmmoMaxCap > 0.0001f
            ? ammo.AmmoInPack / ammo.AmmoMaxCap
            : ammo.RelInPack;
        var percent = Mathf.Clamp(Mathf.RoundToInt(relative * 100f), 0, 100);
        capacity = percent + "%";
        return true;
    }

    private static string Identity(ItemEquippable item)
    {
        var text = new StringBuilder();
        var block = Alive(item.ItemDataBlock) ? item.ItemDataBlock : null;
        if (block != null)
        {
            Append(text, block!.name);
            Append(text, block.publicName);
            Append(text, block.terminalItemShortName);
            AppendPrefabs(text, block.FirstPersonPrefabs);
        }

        Append(text, item.PublicName);
        Append(text, item.ArchetypeName);
        Append(text, item.name);
        return text.ToString();
    }

    private static void AppendPrefabs(StringBuilder text, Il2CppSystem.Collections.Generic.List<string>? prefabs)
    {
        if (!Alive(prefabs))
            return;

        var count = prefabs!.Count;
        for (var i = 0; i < count && i < 4; i++)
            Append(text, prefabs[i]);
    }

    private static void Append(StringBuilder text, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (text.Length > 0)
            text.Append(' ');

        text.Append(value.ToLowerInvariant());
    }

    private static bool Has(string text, string token)
    {
        return text.Contains(token, StringComparison.Ordinal);
    }

    private void ResetAppliedStyle()
    {
        _appliedCapacity = null;
        _appliedColorHex = null;
        _appliedSize = -1;
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
