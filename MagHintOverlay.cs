using System;
using BepInEx.Configuration;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
using UnityEngine;

namespace V921;

/// <summary>
/// Magazine dots beside the crosshair, and "EMPTY" under the other visor hints.
///
/// Up to five small circles sit to the right of Outline. A magazine under five rounds
/// shows one circle per round, drawn a little larger. Spent circles stay visible at a
/// lower opacity. The stack is hidden on a full magazine and fades toward those opacities
/// as rounds are spent.
/// A reload leaves the stack as it was. The dots turn white at full opacity and grow
/// by one short-magazine size step for 80ms slightly before that reload ends, then
/// return to the configured color, opacity, and size.
///
/// "EMPTY" shows while the wielded gun's magazine has no rounds.
/// A reload that starts from an empty magazine keeps the word up until that reload finishes.
/// The dots fade on their own curve. This word does not follow that fade.
/// The gleam is the text color, pulsed per letter when the mesh allows it.
/// </summary>
internal sealed class MagHintOverlay
{
    /// <summary>
    /// Remaining rounds that always draw at the configured opacity.
    /// A full magazine stays hidden even when it holds this many rounds or fewer.
    /// </summary>
    internal const float RoundsAtFullOpacity = 10f;

    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<float> ShimmerIntensity { get; private set; } = null!;
    public static ConfigEntry<float> FullOpacityAt { get; private set; } = null!;
    public static ConfigEntry<bool> MagCounterEnabled { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterSize { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterOpacity { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterSpentOpacity { get; private set; } = null!;

    private const int MagPipCount = 5;
    private const float MagPipGapRatio = 1.2f / 3.2f;
    private const float MagPipMargin = 4f;
    private const float MagPipSmallMagScaleStep = 0.12f;
    private const float MagPipThickness = 16f;

    /// <summary>
    /// Reload-complete dots grow by one short-magazine size step, then return.
    /// </summary>
    private const float MagReloadPopScale = 1f + MagPipSmallMagScaleStep;

    /// <summary>How long the reload-complete pop lasts.</summary>
    private const float MagReloadPopSeconds = 0.08f;

    /// <summary>
    /// The pop starts this long before the reload's end, so it is not late.
    /// </summary>
    private const float MagReloadLeadSeconds = 0.15f;

    /// <summary>
    /// The clip can be stored before <see cref="ItemEquippable.IsReloading"/> is set.
    /// </summary>
    private const float MagReloadArmSeconds = 0.3f;
    private const string Word = "EMPTY";
    private const float TmpPointsPerHudUnit = 10f;
    private const float BelowHintGap = 6f;
    private const float MinimumDrop = 24f;
    private const float ShimmerSpeed = 6f;
    private const float ShimmerCharacterLag = 0.7f;

    /// <summary>
    /// Wielding copies the magazine onto the gun a moment later. Until then a full gun still reads as 0.
    /// </summary>
    private const float ClipSettleSeconds = 0.25f;

    private TextMeshPro? _label;
    private string? _appliedColorHex;
    private int _appliedSize = -1;
    private bool _shimmerFailed;
    private bool _errorLogged;
    private bool _hidden = true;
    private bool _emptyWasShown;
    private bool _holdForReload;
    private IntPtr _heldItem;
    private IntPtr _clipZeroItem;
    private float _clipZeroSince = -1f;
    private bool _wordReady;
    private bool _meshReady;
    private float _appliedTop = float.NaN;
    private Color _parsedColor;
    private string? _parsedColorHex;
    private readonly UI_Sprite?[] _magPips = new UI_Sprite?[MagPipCount];
    private Sprite? _magPipSprite;
    private float _magDisplayed = -1f;
    private bool _magReloadActive;
    private bool _magPictureHeld;
    private bool _magHasPicture;
    private float _magPopUntil;
    private float _magReloadStarted;
    private float _magReloadReleaseAt;
    private IntPtr _magItemPtr;
    private float _appliedMagDisplayed = -1f;
    private float _appliedMagAnchorX = float.NaN;
    private int _appliedMagPipCount = -1;
    private float _appliedMagPipSize = float.NaN;
    private float _appliedMagOpacity = -1f;
    private float _appliedMagSpentOpacity = -1f;
    private float _appliedMagFade = -1f;
    private bool _appliedMagPop;
    private bool _magFadeHidden;
    private Color _appliedMagColor;

    public static void Bind(ConfigFile config)
    {
        const string section = "9 - Mag Hint";

        Enabled = config.Bind(
            section,
            "Enabled",
            true,
            "Show EMPTY below the other hints while the wielded gun's magazine is empty. A reload that starts empty keeps it up until that reload finishes.");

        Size = config.Bind(
            section,
            "Size",
            18,
            new ConfigDescription(
                "Mag hint text height in HUD units.",
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
                "How strongly the mag hint gleams. 0 keeps a steady color, 1 is the strongest shimmer.",
                new AcceptableValueRange<float>(0f, 1f)));

        FullOpacityAt = config.Bind(
            section,
            "Full opacity at",
            0.7f,
            new ConfigDescription(
                "Magazine fill where the magazine dots reach Mag counter Opacity. 0.7 reaches it at 70% of the magazine and keeps it there as fewer rounds remain. A full magazine always hides the dots. The last 10 rounds always use that opacity, so a short magazine does not stay faded. EMPTY stays hidden until the magazine is empty.",
                new AcceptableValueRange<float>(0f, 1f)));
    }

    public static void BindCounter(ConfigFile config)
    {
        MagCounterEnabled = config.Bind(
            "5 - Mag counter",
            "Enabled",
            true,
            "Show the magazine dots beside the crosshair.");

        MagCounterSize = config.Bind(
            "5 - Mag counter",
            "Size",
            3.2f,
            new ConfigDescription(
                "Magazine dot diameter in HUD units. Clips shorter than five rounds scale each dot up slightly.",
                new AcceptableValueRange<float>(1f, 16f)));

        MagCounterOpacity = config.Bind(
            "5 - Mag counter",
            "Opacity",
            0.8f,
            new ConfigDescription(
                "Opacity of rounds still in the magazine once the dots have faded in. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        MagCounterSpentOpacity = config.Bind(
            "5 - Mag counter",
            "Spent opacity",
            0.18f,
            new ConfigDescription(
                "Opacity of spent rounds once the dots have faded in. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));
    }

    /// <summary>
    /// 0 while the magazine is full. 1 once the remaining rounds are at or below the larger of
    /// the configured fill and <see cref="RoundsAtFullOpacity"/>. Between those, it rises as rounds are spent.
    /// </summary>
    internal static float OpacityFactor(float roundsLeft, int capacity, float fullAtFraction)
    {
        if (capacity <= 0)
            return 0f;

        // Full stays hidden even when the magazine itself is 10 rounds or fewer.
        if (roundsLeft >= capacity - 0.001f)
            return 0f;

        var gate = Mathf.Max(capacity * Mathf.Clamp01(fullAtFraction), RoundsAtFullOpacity);
        if (roundsLeft <= gate)
            return 1f;

        var span = capacity - gate;
        if (span <= 0.001f)
            return 1f;

        return Mathf.Clamp01((capacity - roundsLeft) / span);
    }

    /// <summary>
    /// Top of the EMPTY word.
    /// </summary>
    internal static float AnchorTop(float contentFloor)
    {
        return Mathf.Min(contentFloor, -MinimumDrop) - BelowHintGap;
    }

    internal bool TryGetContentBottom(out float bottom)
    {
        bottom = 0f;
        if (_hidden || !Alive(_label) || !_label!.gameObject.activeSelf)
            return false;

        var rect = _label.rectTransform;
        bottom = rect.anchoredPosition.y - rect.sizeDelta.y * rect.pivot.y;
        return true;
    }

    public bool NeedsPresentation(bool aiming)
    {
        if (!Enabled.Value || !aiming || !EmptyShouldStay())
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
            // An inactive visor builds an empty mesh, and that mesh would stay empty after the root turns on.
            if (text.gameObject.activeInHierarchy)
            {
                text.ForceMeshUpdate();
                _wordReady = true;
                _meshReady = true;
            }
        }

        ApplyShimmer(text, color, intensity);
        var top = AnchorTop(contentFloor);
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
        ClearPips();
        ResetAppliedStyle();
        _emptyWasShown = false;
        _holdForReload = false;
        _heldItem = IntPtr.Zero;
        _clipZeroItem = IntPtr.Zero;
        _clipZeroSince = -1f;
        _hidden = true;
    }

    /// <summary>
    /// The clip count is written when a reload starts, before the animation finishes.
    /// EMPTY stays up for that reload when the magazine was empty as it began.
    /// Switching weapons clears it. A clip that has not arrived yet is not an empty magazine.
    /// </summary>
    private bool EmptyShouldStay()
    {
        if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
        {
            _emptyWasShown = false;
            _holdForReload = false;
            _heldItem = IntPtr.Zero;
            return false;
        }

        var ptr = item!.Pointer;
        if (ptr != _heldItem)
        {
            _heldItem = ptr;
            _emptyWasShown = false;
            _holdForReload = false;
        }

        var empty = MagazineIsEmpty();
        var reloading = item.IsReloading;
        if (empty)
            _emptyWasShown = true;
        if (_emptyWasShown && reloading)
            _holdForReload = true;

        if (empty || (_holdForReload && reloading))
            return true;

        _emptyWasShown = false;
        _holdForReload = false;
        return false;
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
            var painted = false;
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
                painted = true;
            }

            if (!painted)
            {
                text.color = Gleam(color, intensity, 0.5f + 0.5f * Mathf.Sin(time));
                return;
            }

            text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
        catch (Exception ex)
        {
            _shimmerFailed = true;
            text.color = Gleam(color, intensity, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * ShimmerSpeed));
            LogOnce($"Mag hint shimmer fell back to a solid pulse: {ex.Message}");
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

            go = new GameObject("Mag Hint");
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

            LogOnce($"Mag hint create failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The wielded gun's magazine has no rounds.
    /// The higher of the clip reads is the count the magazine dots use, so one stale 0 is not empty.
    /// Wielding copies that count a moment later, and until then a full gun still reads as 0.
    /// A fetched 0 is empty. A 0 that is still there after the copy gap is empty too.
    /// </summary>
    internal bool MagazineIsEmpty()
    {
        try
        {
            if (!WieldedFrame.TryGetBulletWeapon(out var gun) || !Alive(gun))
                return false;

            if (gun!.ClipIsFull())
            {
                _clipZeroSince = -1f;
                return false;
            }

            var rounds = gun.GetCurrentClip();
            if (gun.m_clip > rounds)
                rounds = gun.m_clip;
            if (rounds > 0)
            {
                _clipZeroSince = -1f;
                return false;
            }

            var max = gun.GetMaxClip();
            if (max <= 0)
                max = gun.ClipSize;
            if (max <= 0)
                return false;

            if (gun.m_hasGottenClip)
                return true;

            return UnfetchedZeroHasSettled();
        }
        catch (Exception ex)
        {
            LogOnce($"Mag hint update failed: {ex.Message}");
            return false;
        }
    }

    private bool UnfetchedZeroHasSettled()
    {
        var ptr = IntPtr.Zero;
        if (WieldedFrame.TryGetItem(out var item) && Alive(item))
            ptr = item!.Pointer;

        if (ptr != _clipZeroItem)
        {
            _clipZeroItem = ptr;
            _clipZeroSince = Time.unscaledTime;
            return false;
        }

        if (_clipZeroSince < 0f)
            _clipZeroSince = Time.unscaledTime;

        return Time.unscaledTime - _clipZeroSince >= ClipSettleSeconds;
    }

    internal void CreatePips(RectTransform parent, int layer, SpriteRenderer? reference, Func<float, Sprite?> circleSprite)
    {
        _magPipSprite = circleSprite(MagPipThickness);
        if (_magPipSprite == null)
            return;

        for (var i = 0; i < MagPipCount; i++)
        {
            var pip = CrosshairOverlay.CreateSprite(parent, _magPipSprite, "Mag Pip " + (i + 1), layer, reference);
            pip.gameObject.SetActive(false);
            _magPips[i] = pip;
        }
    }

    /// <summary>
    /// Magazine pips sit just to the right of Outline, top circle first.
    /// A clip shorter than <see cref="MagPipCount"/> draws one pip per round.
    /// Fired rounds leave their pip in place at the configured spent opacity.
    /// A reload leaves that picture in place. When it finishes, every dot is white
    /// at full opacity and one size step larger for <see cref="MagReloadPopSeconds"/>,
    /// then the stack uses the configured color, opacity, and size.
    /// <see cref="OpacityFactor"/> hides a full magazine and fades the
    /// stack up to the configured opacities as rounds are spent.
    /// </summary>
    internal void ApplyIndicator(bool showCrosshair, float outlineSize, Color? outlineColor)
    {
        var hasMag = TryGetMagDisplayed(out var displayed, out var capacity);
        if (!showCrosshair
            || !MagCounterEnabled.Value
            || _magPipSprite == null
            || !hasMag
            || outlineColor == null)
        {
            _magFadeHidden = false;
            HideMagPips();
            return;
        }

        // The reload already has a picture. Leave those sprites until it finishes.
        if (_magPictureHeld && _magHasPicture)
            return;

        var popping = Time.unscaledTime < _magPopUntil;
        var fade = popping
            ? 1f
            : OpacityFactor(
                displayed * capacity,
                capacity,
                FullOpacityAt.Value);
        if (fade <= 0.0001f)
        {
            if (!_magFadeHidden)
            {
                HideMagPips();
                _magFadeHidden = true;
            }

            return;
        }

        _magFadeHidden = false;
        var configuredSize = Mathf.Clamp(MagCounterSize.Value, 1f, 16f);
        var opacity = popping ? 1f : Mathf.Clamp01(MagCounterOpacity.Value);
        var spentOpacity = popping ? 1f : Mathf.Clamp01(MagCounterSpentOpacity.Value);
        var pipCount = capacity < MagPipCount ? capacity : MagPipCount;
        var pipScale = pipCount < MagPipCount
            ? 1f + (MagPipCount - pipCount) * MagPipSmallMagScaleStep
            : 1f;
        var layoutSize = configuredSize * pipScale;
        var pipSize = layoutSize * (popping ? MagReloadPopScale : 1f);
        var color = popping ? Color.white : outlineColor.Value;
        var anchorX = outlineSize * 0.5f + MagPipMargin + layoutSize * 0.5f;
        var layoutChanged = float.IsNaN(_appliedMagAnchorX) || !Mathf.Approximately(_appliedMagAnchorX, anchorX);
        var colorChanged = color != _appliedMagColor;
        var ratioChanged = !Mathf.Approximately(_appliedMagDisplayed, displayed);
        var countChanged = pipCount != _appliedMagPipCount;
        var sizeChanged = float.IsNaN(_appliedMagPipSize) || !Mathf.Approximately(_appliedMagPipSize, pipSize);
        var opacityChanged = !Mathf.Approximately(_appliedMagOpacity, opacity)
            || !Mathf.Approximately(_appliedMagSpentOpacity, spentOpacity);
        var fadeChanged = !Mathf.Approximately(_appliedMagFade, fade);
        var popChanged = popping != _appliedMagPop;
        if (!layoutChanged && !colorChanged && !ratioChanged && !countChanged && !sizeChanged && !opacityChanged && !fadeChanged && !popChanged)
        {
            _magHasPicture = true;
            return;
        }

        _appliedMagAnchorX = anchorX;
        _appliedMagColor = color;
        _appliedMagDisplayed = displayed;
        _appliedMagPipCount = pipCount;
        _appliedMagPipSize = pipSize;
        _appliedMagOpacity = opacity;
        _appliedMagSpentOpacity = spentOpacity;
        _appliedMagFade = fade;
        _appliedMagPop = popping;

        var gap = layoutSize * MagPipGapRatio;
        var span = pipCount * layoutSize + (pipCount - 1) * gap;
        var top = span * 0.5f - layoutSize * 0.5f;
        var step = layoutSize + gap;
        var filled = displayed * pipCount;
        for (var i = 0; i < MagPipCount; i++)
        {
            var pip = _magPips[i];
            if (!Alive(pip))
                continue;

            if (i >= pipCount)
            {
                if (pip!.gameObject.activeSelf)
                    pip.gameObject.SetActive(false);
                continue;
            }

            if (!pip!.gameObject.activeSelf)
                pip.gameObject.SetActive(true);

            var rect = pip.m_rect;
            if (rect != null)
                rect.anchoredPosition = new Vector2(anchorX, top - i * step);

            var presence = Mathf.Clamp01(filled - (pipCount - 1 - i));
            var pipColor = color;
            pipColor.a = Mathf.Lerp(spentOpacity, opacity, presence) * fade;
            CrosshairOverlay.SetSprite(pip, pipColor, pipSize);
        }

        _magHasPicture = true;
    }

    internal void ClearPips()
    {
        for (var i = 0; i < MagPipCount; i++)
            _magPips[i] = null;

        _magPipSprite = null;
        _magDisplayed = -1f;
        _magReloadActive = false;
        _magPictureHeld = false;
        _magHasPicture = false;
        _magPopUntil = 0f;
        _magReloadStarted = 0f;
        _magReloadReleaseAt = 0f;
        _magItemPtr = IntPtr.Zero;
        _appliedMagDisplayed = -1f;
        _appliedMagAnchorX = float.NaN;
        _appliedMagPipCount = -1;
        _appliedMagPipSize = float.NaN;
        _appliedMagOpacity = -1f;
        _appliedMagSpentOpacity = -1f;
        _appliedMagFade = -1f;
        _appliedMagPop = false;
        _magFadeHidden = false;
    }

    private bool TryGetMagDisplayed(out float displayed, out int capacity)
    {
        displayed = 0f;
        capacity = 0;
        if (!WieldedFrame.TryGetBulletWeapon(out var gun) || !Alive(gun))
        {
            ClearMagTrack();
            return false;
        }

        if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
        {
            ClearMagTrack();
            return false;
        }

        // Wielding writes the clip a moment later. A 0 from that gap is not an empty magazine.
        if (!gun!.m_hasGottenClip && gun.GetCurrentClip() <= 0 && gun.m_clip <= 0 && !gun.ClipIsFull())
            return false;

        var max = gun.GetMaxClip();
        if (max <= 0)
            max = gun.ClipSize;
        if (max <= 0)
            return false;

        var clipCount = gun.GetCurrentClip();
        if (gun.m_clip > clipCount)
            clipCount = gun.m_clip;
        var clip = Mathf.Clamp(clipCount, 0, max);
        var live = gun.ClipIsFull() ? 1f : clip / (float)max;
        var ptr = item!.Pointer;
        var reloading = item.IsReloading;
        if (ptr != _magItemPtr)
        {
            _magItemPtr = ptr;
            _magReloadActive = false;
            _magPictureHeld = false;
            _magHasPicture = false;
            _magPopUntil = 0f;
            _magReloadStarted = 0f;
            _magReloadReleaseAt = 0f;
            _magDisplayed = live;
        }
        else if (ReloadHold(reloading, live))
        {
            // The clip is stored when the reload starts. Keep the fill from the
            // frame before that, and release the dots slightly before the reload ends.
            if (!_magReloadActive)
            {
                _magReloadActive = true;
                _magReloadStarted = Time.time;
                _magPopUntil = 0f;
                if (_magDisplayed < 0f)
                    _magDisplayed = live;

                _magPictureHeld = _magHasPicture && !_appliedMagPop;
                _magReloadReleaseAt = _magReloadStarted + (reloading ? ReloadSpan(item, gun) : MagReloadArmSeconds);
            }
            else if (reloading)
            {
                var release = _magReloadStarted + ReloadSpan(item, gun);
                if (release > _magReloadReleaseAt)
                    _magReloadReleaseAt = release;
            }
        }
        else
        {
            if (_magReloadActive)
            {
                _magPopUntil = Time.unscaledTime + MagReloadPopSeconds;
                _magReloadActive = false;
                _magPictureHeld = false;
            }

            _magDisplayed = live;
        }

        displayed = Mathf.Clamp01(_magDisplayed);
        capacity = max;
        return true;
    }

    /// <summary>
    /// Keep the pre-reload fill until slightly before the reload ends.
    /// A dropped reload flag ends the hold immediately, so the pop is not late.
    /// </summary>
    private bool ReloadHold(bool reloading, float live)
    {
        if (_magReloadActive && Time.time >= _magReloadStarted + 20f)
            return false;

        if (_magReloadActive && Time.time >= _magReloadReleaseAt - MagReloadLeadSeconds)
            return false;

        if (reloading)
            return true;

        if (_magReloadActive)
            return false;

        return _magDisplayed >= 0f && live > _magDisplayed + 0.001f;
    }

    /// <summary>
    /// Gameplay length of this reload. The shorter of <see cref="ItemEquippable.ReloadTime"/>
    /// and the authored duration wins, so the pop leads the end instead of trailing the animation.
    /// </summary>
    private float ReloadSpan(ItemEquippable item, BulletWeapon gun)
    {
        var time = item.ReloadTime;
        try
        {
            var data = gun.m_weaponData;
            if (Alive(data))
            {
                var authored = data!.reloadDuration;
                if (authored > 0.05f)
                    time = time > 0.05f ? Mathf.Min(time, authored) : authored;
            }
        }
        catch (Exception ex)
        {
            LogOnce($"Mag reload timing fell back to ReloadTime: {ex.Message}");
        }

        if (time < 0.05f)
            time = 1.2f;
        return Mathf.Min(time, 20f);
    }

    private void ClearMagTrack()
    {
        _magReloadActive = false;
        _magPictureHeld = false;
        _magPopUntil = 0f;
        _magReloadStarted = 0f;
        _magReloadReleaseAt = 0f;
        _magItemPtr = IntPtr.Zero;
    }

    private void HideMagPips()
    {
        _magHasPicture = false;
        _appliedMagDisplayed = -1f;
        _appliedMagAnchorX = float.NaN;
        _appliedMagPipCount = -1;
        _appliedMagPipSize = float.NaN;
        _appliedMagOpacity = -1f;
        _appliedMagSpentOpacity = -1f;
        _appliedMagFade = -1f;
        _appliedMagPop = false;
        for (var i = 0; i < MagPipCount; i++)
        {
            var pip = _magPips[i];
            if (Alive(pip) && pip!.gameObject.activeSelf)
                pip.gameObject.SetActive(false);
        }
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
