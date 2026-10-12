using System;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// "EMPTY" under the other visor hints while the wielded gun's magazine has no rounds.
/// The gleam is the same text color the other hints use, pulsed per letter when the mesh allows it.
/// </summary>
internal sealed class EmptyMagOverlay
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<float> ShimmerIntensity { get; private set; } = null!;

    private const string Word = "EMPTY";
    private const float TmpPointsPerHudUnit = 10f;
    private const float BelowHintGap = 6f;
    private const float MinimumDrop = 24f;
    private const float ShimmerSpeed = 6f;
    private const float ShimmerCharacterLag = 0.7f;

    private TextMeshPro? _label;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private bool _shimmerFailed;
    private bool _errorLogged;
    private bool _hidden = true;
    private bool _wordReady;
    private bool _meshReady;
    private float _appliedTop = float.NaN;
    private Color _parsedColor;
    private string? _parsedColorHex;

    public static void Bind(ConfigFile config)
    {
        const string section = "9 - Empty Mag Hint";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "Show EMPTY below the other hints when the wielded gun's magazine is empty.");

        Size = config.Bind(
            section,
            "Size",
            18,
            new ConfigDescription(
                "EMPTY text height in HUD units.",
                new AcceptableValueRange<int>(2, 48)));

        ColorHex = config.Bind(
            section,
            "Color",
            "ff2a2a",
            "EMPTY text color as RRGGBB or RRGGBBAA. Example: FF3355.");

        ShimmerIntensity = config.Bind(
            section,
            "Shimmer intensity",
            0.6f,
            new ConfigDescription(
                "How strongly EMPTY gleams. 0 keeps a steady color, 1 is the strongest shimmer.",
                new AcceptableValueRange<float>(0f, 1f)));
    }

    public bool NeedsPresentation(bool aiming)
    {
        if (!Enabled.Value || !aiming || !MagazineIsEmpty())
        {
            Hide();
            return false;
        }

        return true;
    }

    public bool Update(Transform? parent, int layer, float contentFloor)
    {
        if (!Alive(parent))
            return false;

        if (!Alive(_label))
            _label = Create(parent!, layer);
        if (!Alive(_label))
            return false;

        var size = Mathf.Clamp(Size.Value, 2, 48);
        var intensity = Mathf.Clamp01(ShimmerIntensity.Value);
        var colorHex = ColorHex.Value;
        if (colorHex != _parsedColorHex)
        {
            _parsedColorHex = colorHex;
            _parsedColor = ParseColor(colorHex, new Color(1f, 0.165f, 0.165f, 1f));
        }

        var color = _parsedColor;
        var styleChanged = colorHex != _appliedColorHex || size != _appliedSize;
        var text = _label!;
        if (!text.gameObject.activeSelf)
            text.gameObject.SetActive(true);

        if (styleChanged || !_wordReady)
        {
            text.text = Word;
            text.fontSize = size * TmpPointsPerHudUnit;
            text.rectTransform.sizeDelta = new Vector2(size * 8f, size * 2.5f);
            text.color = color;
            text.ForceMeshUpdate();
            _wordReady = true;
            _meshReady = true;
        }

        ApplyShimmer(text, color, intensity);
        var top = Mathf.Min(contentFloor, -MinimumDrop) - BelowHintGap;
        if (float.IsNaN(_appliedTop) || !Mathf.Approximately(_appliedTop, top))
        {
            text.rectTransform.anchoredPosition = new Vector2(0f, top);
            _appliedTop = top;
        }

        _appliedColorHex = colorHex;
        _appliedSize = size;
        _hidden = false;
        return true;
    }

    public void Detach()
    {
        DestroyText(ref _label);
        ResetAppliedStyle();
        _hidden = true;
    }

    /// <summary>
    /// Letter colors are painted onto the mesh from the last build.
    /// The mesh is rebuilt when the word or its style changes. Later frames only recolor vertices.
    /// A failed paint keeps the whole word on one pulse, which is the same color path the other hints already use.
    /// </summary>
    private void ApplyShimmer(TextMeshPro text, Color color, float intensity)
    {
        if (_shimmerFailed || intensity <= 0.0001f)
        {
            text.color = Gleam(color, intensity, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * ShimmerSpeed));
            return;
        }

        try
        {
            if (!_meshReady)
            {
                text.ForceMeshUpdate();
                _meshReady = true;
            }
            var info = text.textInfo;
            var characters = info != null ? info.characterInfo : null;
            var meshes = info != null ? info.meshInfo : null;
            var count = info != null ? info.characterCount : 0;
            if (characters == null || meshes == null || count <= 0)
            {
                text.color = Gleam(color, intensity, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * ShimmerSpeed));
                return;
            }

            var time = Time.unscaledTime * ShimmerSpeed;
            for (var i = 0; i < count; i++)
            {
                var character = characters[i];
                if (!character.isVisible)
                    continue;

                var wave = 0.5f + 0.5f * Mathf.Sin(time - i * ShimmerCharacterLag);
                var vertexColor = (Color32)Gleam(color, intensity, wave);
                var colors = meshes[character.materialReferenceIndex].colors32;
                var vertex = character.vertexIndex;
                if (colors == null || vertex < 0 || vertex + 3 >= colors.Length)
                    continue;

                colors[vertex] = vertexColor;
                colors[vertex + 1] = vertexColor;
                colors[vertex + 2] = vertexColor;
                colors[vertex + 3] = vertexColor;
            }

            text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
        catch (Exception ex)
        {
            _shimmerFailed = true;
            text.color = Gleam(color, intensity, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * ShimmerSpeed));
            LogOnce($"Empty mag shimmer fell back to a solid pulse: {ex.Message}");
        }
    }

    private static Color Gleam(Color color, float intensity, float wave)
    {
        var bright = Color.Lerp(color, Color.white, 0.62f);
        var gleam = Color.Lerp(color, bright, wave * intensity);
        gleam.a = color.a * Mathf.Lerp(1f, 0.35f + 0.65f * wave, intensity);
        return gleam;
    }

    private TextMeshPro? Create(Transform parent, int layer)
    {
        GameObject? go = null;
        try
        {
            var source = HudText();
            if (!Alive(source) || source!.font == null)
                return null;

            go = new GameObject("Empty Mag Hint");
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
            text.alignment = TextAlignmentOptions.Top;
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
                renderer.sortingOrder = sourceRenderer.sortingOrder + 5;
                renderer.renderingLayerMask = sourceRenderer.renderingLayerMask;
            }

            go.SetActive(false);
            return text;
        }
        catch (Exception ex)
        {
            if (go != null)
                UnityEngine.Object.Destroy(go);

            LogOnce($"Empty mag hint create failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The inventory widget is the clip number on the player HUD. The weapon fields cover a gun
    /// whose widget is not showing a magazine count.
    /// </summary>
    private bool MagazineIsEmpty()
    {
        try
        {
            if (!WieldedFrame.TryGetBulletWeapon(out var gun) || !Alive(gun))
                return false;

            var clip = gun!.GetCurrentClip();
            // Rounds still in the weapon, so the magazine the HUD shows is not empty either.
            if (clip > 0 && gun.m_clip > 0)
                return false;

            if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
                return false;

            if (DisplayedMagazineIsEmpty(item!))
                return true;

            var max = gun!.GetMaxClip();
            if (max <= 0)
                max = gun.ClipSize;
            if (max <= 0)
                return false;

            return clip <= 0 && gun.m_clip <= 0;
        }
        catch (Exception ex)
        {
            LogOnce($"Empty mag hint update failed: {ex.Message}");
            return false;
        }
    }

    private static bool DisplayedMagazineIsEmpty(ItemEquippable item)
    {
        var block = Alive(item.ItemDataBlock) ? item.ItemDataBlock : null;
        if (block == null)
            return false;

        var slot = block!.inventorySlot;
        if (slot is not (InventorySlot.GearStandard or InventorySlot.GearSpecial))
            return false;

        var playerGui = GuiManager.PlayerLayer;
        if (!Alive(playerGui))
            return false;

        var inventory = playerGui!.Inventory;
        if (!Alive(inventory))
            return false;

        var slots = inventory!.m_inventorySlots;
        if (!Alive(slots) || !slots!.ContainsKey(slot))
            return false;

        var widget = slots[slot];
        if (!Alive(widget) || widget!.ShowAmmoInfinite || !widget.ShowAmmoClip)
            return false;

        return widget.m_ammoClipAbsVal <= 0;
    }

    private void Hide()
    {
        if (_hidden)
            return;

        _hidden = true;
        ResetAppliedStyle();
        if (!Alive(_label))
            return;

        _label!.text = string.Empty;
        _label.color = new Color(1f, 1f, 1f, 0f);
        if (_label.gameObject.activeSelf)
            _label.gameObject.SetActive(false);
    }

    private void ResetAppliedStyle()
    {
        _appliedColorHex = null;
        _appliedSize = -1;
        _wordReady = false;
        _meshReady = false;
        _appliedTop = float.NaN;
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
        return HudColor.Parse(raw, fallback);
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
