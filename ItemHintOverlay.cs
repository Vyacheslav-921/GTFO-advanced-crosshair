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
/// Consumable naming hint below the reticle, plus remaining uses at the visor center.
/// These are level pickups the player wields and spends, such as a glow stick or fog repeller.
/// </summary>
internal sealed class ItemHintOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Margin { get; private set; } = null!;
    public static ConfigEntry<string> GlowColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FogColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FoamColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MelterColorHex { get; private set; } = null!;
    public static ConfigEntry<string> HealthSyringeColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MeleeSyringeColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MineColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FoamMineColorHex { get; private set; } = null!;
    public static ConfigEntry<string> FlashColorHex { get; private set; } = null!;
    public static ConfigEntry<string> HeavyFogColorHex { get; private set; } = null!;

    private const float TmpPointsPerHudUnit = 10f;
    private const float NamingSizeScale = 1.2f;
    private const float CapacitySizeScale = 1.1f;
    private const float ColorSaturationBoost = 1.3f;

    private enum ItemHintKind
    {
        Glow,
        Fog,
        Foam,
        Melter,
        HealthSyringe,
        MeleeSyringe,
        Mine,
        FoamMine,
        Flash,
        HeavyFog
    }

    private TextMeshPro? _namingText;
    private TextMeshPro? _capacityText;
    private string? _appliedNaming;
    private string? _appliedCapacity;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private float _appliedMargin = -1f;
    private bool _errorLogged;

    public static void Bind(ConfigFile config)
    {
        const string section = "6 - Items Hints";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "Show item naming below the crosshair and remaining uses at its center while holding a level consumable.");

        Size = config.Bind(
            section,
            "Size",
            16,
            new ConfigDescription(
                "Item naming hint height in HUD units. Item count hint uses a smaller size.",
                new AcceptableValueRange<int>(2, 48)));

        Margin = config.Bind(
            section,
            "Margin",
            7.8f,
            new ConfigDescription(
                "Distance from the crosshair's outer edge to the item naming hint. Count hint stays at the center.",
                new AcceptableValueRange<float>(0f, 48f)));

        GlowColorHex = BindColor(config, section, "Glow color", "GLOW", "6affeb");
        FogColorHex = BindColor(config, section, "Fog color", "FOG", "93c9c1");
        FoamColorHex = BindColor(config, section, "Foam color", "FOAM", "cff8a3");
        MelterColorHex = BindColor(config, section, "Melter color", "MELT", "ffffff");
        HealthSyringeColorHex = BindColor(config, section, "Health syringe color", "HEAL", "ff7290");
        MeleeSyringeColorHex = BindColor(config, section, "Melee syringe color", "MELEE", "98a607");
        MineColorHex = BindColor(config, section, "Mine color", "MINE", "f8df81");
        FoamMineColorHex = BindColor(config, section, "Foam mine color", "CMINE", "cff8a3");
        FlashColorHex = BindColor(config, section, "Flashlight color", "FLASH", "ede78d");
        HeavyFogColorHex = BindColor(config, section, "Heavy fog color", "HFOG", "ffffff");
    }

    public bool Update(Transform? parent, int layer, bool inHudContext, float crosshairDiameter)
    {
        if (!Enabled.Value || !inHudContext || !TryGetHeldItem(out var naming, out var capacity, out var color, out var colorHex))
        {
            Hide();
            return false;
        }

        if (!Alive(parent))
            return false;

        if (!Alive(_namingText))
            _namingText = Create(parent!, layer, "Item Naming Hint", new Vector2(0.5f, 1f), TextAlignmentOptions.Top);
        if (!Alive(_capacityText))
            _capacityText = Create(parent!, layer, "Item Capacity Hint", new Vector2(0.5f, 0.5f), TextAlignmentOptions.Center);
        if (!Alive(_namingText) || !Alive(_capacityText))
            return false;

        var size = Mathf.Clamp(Size.Value, 2, 48);
        var namingSize = Mathf.Max(2, Mathf.RoundToInt(size * NamingSizeScale));
        var capacitySize = Mathf.Max(2, Mathf.RoundToInt(size * CapacitySizeScale));
        var margin = Mathf.Clamp(Margin.Value, 0f, 48f);
        ApplyLabel(
            _namingText!,
            naming,
            color,
            namingSize,
            new Vector2(0f, -(crosshairDiameter * 0.5f + margin)),
            ref _appliedNaming,
            updateLayout: naming != _appliedNaming
                || colorHex != _appliedColorHex
                || size != _appliedSize
                || !Mathf.Approximately(margin, _appliedMargin));
        ApplyLabel(
            _capacityText!,
            capacity,
            color,
            capacitySize,
            Vector2.zero,
            ref _appliedCapacity,
            updateLayout: capacity != _appliedCapacity
                || colorHex != _appliedColorHex
                || size != _appliedSize);

        _appliedColorHex = colorHex;
        _appliedSize = size;
        _appliedMargin = margin;
        return true;
    }

    public void Detach()
    {
        DestroyText(ref _namingText);
        DestroyText(ref _capacityText);
        ResetAppliedStyle();
    }

    private static ConfigEntry<string> BindColor(ConfigFile config, string section, string key, string naming, string defaultHex)
    {
        return config.Bind(
            section,
            key,
            defaultHex,
            $"{naming} item hint color as RRGGBB or RRGGBBAA. Example: FF3355.");
    }

    private void Hide()
    {
        ResetAppliedStyle();
        HideText(_namingText);
        HideText(_capacityText);
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

            LogOnce($"Item hint create failed: {ex.Message}");
            return null;
        }
    }

    private bool TryGetHeldItem(out string naming, out string capacity, out Color color, out string colorHex)
    {
        naming = string.Empty;
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
            if (!Alive(item) || !TryGetItemStyle(item!, out naming, out color, out colorHex))
                return false;

            capacity = RemainingUses(item!).ToString();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"Item hint update failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetItemStyle(
        ItemEquippable item,
        out string naming,
        out Color color,
        out string colorHex)
    {
        if (!TryClassify(item, out var kind))
        {
            naming = string.Empty;
            color = Color.white;
            colorHex = string.Empty;
            return false;
        }

        switch (kind)
        {
            case ItemHintKind.Glow:
                colorHex = GlowColorHex.Value;
                naming = "GLOW";
                break;
            case ItemHintKind.Fog:
                colorHex = FogColorHex.Value;
                naming = "FOG";
                break;
            case ItemHintKind.Foam:
                colorHex = FoamColorHex.Value;
                naming = "FOAM";
                break;
            case ItemHintKind.Melter:
                colorHex = MelterColorHex.Value;
                naming = "MELT";
                break;
            case ItemHintKind.HealthSyringe:
                colorHex = HealthSyringeColorHex.Value;
                naming = "HEAL";
                break;
            case ItemHintKind.MeleeSyringe:
                colorHex = MeleeSyringeColorHex.Value;
                naming = "MELEE";
                break;
            case ItemHintKind.Mine:
                colorHex = MineColorHex.Value;
                naming = "MINE";
                break;
            case ItemHintKind.FoamMine:
                colorHex = FoamMineColorHex.Value;
                naming = "CMINE";
                break;
            case ItemHintKind.Flash:
                colorHex = FlashColorHex.Value;
                naming = "FLASH";
                break;
            default:
                colorHex = HeavyFogColorHex.Value;
                naming = "HFOG";
                break;
        }

        color = ParseColor(colorHex, Color.white);
        return true;
    }

    /// <summary>
    /// Glow sticks and fog repellers share <see cref="ThrowingWeapon"/>. The datablock name
    /// separates them. Unique wield classes cover the other level consumables.
    /// </summary>
    private static bool TryClassify(ItemEquippable item, out ItemHintKind kind)
    {
        kind = default;
        var block = Alive(item.ItemDataBlock) ? item.ItemDataBlock : null;
        var slot = block != null ? block!.inventorySlot : InventorySlot.None;
        var consumableSlot = slot is InventorySlot.Consumable or InventorySlot.ConsumableHeavy;
        var heavy = Alive(item.TryCast<HeavyFogRepellerFirstPerson>());
        var melter = Alive(item.TryCast<LockMelterFirstPerson>());
        var syringeItem = item.TryCast<SyringeFirstPerson>();
        var syringe = Alive(syringeItem);
        var flash = Alive(item.TryCast<ConsumableFlashlight>());
        var mine = Alive(item.TryCast<MineDeployerFirstPerson>());
        var throwing = Alive(item.TryCast<ThrowingWeapon>());
        if (!consumableSlot && !heavy && !melter && !syringe && !flash && !mine && !throwing)
            return false;

        if (mine && !consumableSlot)
        {
            var deployer = item.TryCast<MineDeployerFirstPerson>();
            if (Alive(deployer) && !deployer!.m_isConsumable)
                return false;
        }

        var text = Identity(item);
        if (heavy || Has(text, "turbine") || (Has(text, "heavy") && Has(text, "fog")))
            kind = ItemHintKind.HeavyFog;
        else if (Has(text, "glow"))
            kind = ItemHintKind.Glow;
        else if (Has(text, "fog") || Has(text, "repeller"))
            kind = ItemHintKind.Fog;
        else if (melter || Has(text, "melter"))
            kind = ItemHintKind.Melter;
        else if (syringe || Has(text, "syringe") || Has(text, "iix"))
            kind = SyringeKind(syringeItem, text);
        else if (flash || Has(text, "flash"))
            kind = ItemHintKind.Flash;
        else if (mine || Has(text, "mine") || Has(text, "trip"))
            kind = Has(text, "glue") || Has(text, "foam") ? ItemHintKind.FoamMine : ItemHintKind.Mine;
        else if (Has(text, "foam") || Has(text, "glue") || Has(text, "grenade"))
            kind = ItemHintKind.Foam;
        else
            return false;

        return true;
    }

    /// <summary>
    /// <see cref="eSyringeType.Health"/> restores health. <see cref="eSyringeType.MeleeBuff"/> raises melee damage.
    /// </summary>
    private static ItemHintKind SyringeKind(SyringeFirstPerson? syringe, string text)
    {
        if (Alive(syringe))
        {
            return syringe!.m_type == eSyringeType.MeleeBuff
                ? ItemHintKind.MeleeSyringe
                : ItemHintKind.HealthSyringe;
        }

        return Has(text, "melee") ? ItemHintKind.MeleeSyringe : ItemHintKind.HealthSyringe;
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

    private static int RemainingUses(ItemEquippable item)
    {
        var ammoType = item.AmmoType;
        if (ammoType == AmmoType.None)
        {
            var clip = item.GetCurrentClip();
            return clip > 0 ? clip : 1;
        }

        var backpack = PlayerBackpackManager.LocalBackpack;
        if (!Alive(backpack) || !Alive(backpack!.AmmoStorage))
            return Math.Max(0, item.GetCurrentClip());

        var ammo = backpack.AmmoStorage!.GetInventorySlotAmmo(ammoType);
        if (!Alive(ammo))
            return Math.Max(0, item.GetCurrentClip());

        if (ammo!.BulletsInPack > 0)
            return ammo.BulletsInPack;

        if (ammo.CostOfBullet > 0.0001f)
            return Mathf.Max(0, Mathf.RoundToInt(ammo.AmmoInPack / ammo.CostOfBullet));

        return Mathf.Max(0, Mathf.RoundToInt(ammo.RelInPack * 100f));
    }

    private void ResetAppliedStyle()
    {
        _appliedNaming = null;
        _appliedCapacity = null;
        _appliedColorHex = null;
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
