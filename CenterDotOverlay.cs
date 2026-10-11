using System;
using BepInEx.Configuration;
using FirstPersonItem;
using GameData;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using UnityEngine;

namespace V921;

internal enum ReticleShape
{
    Dot,
    TwoDots,
    Arcs,
    Circle,
    Lines
}

/// <summary>
/// Draws a small circle at the visor center while aiming.
///
/// The player HUD is Cell UI. <see cref="PlayerGuiLayer.m_movementRoot"/> is the group
/// the game shifts for visor sway; the PlayerLayer canvas above it stays put.
/// The dot is a HUD sprite under that movement root, placed at the canvas center,
/// then registered with <see cref="UI_Core.UIPassHUD"/> so the visor blit
/// (pixel, skew, opacity) includes it.
///
/// Recoil that does not rotate the camera is <see cref="FPS_RecoilSystem.CurrentVSPos"/>,
/// a viewport point around (0.5, 0.5). That offset is added in canvas space on top of
/// the sway rest position. Shotguns stay centered: their pellets follow the camera.
///
/// A same-colored room swallows the fill. A black ring and a configurable outer ring
/// sit behind it, so an edge still reads against that light.
///
/// A melee charge draws a configurable red shape at its configured diameter, then shrinks it
/// to the crosshair's outer edge as <see cref="MWS_ChargeUp.m_elapsed"/> reaches full damage.
///
/// Shown only when all of these are true:
/// - <see cref="GameStateManager.CurrentStateName"/> is <see cref="eGameStateName.InLevel"/>
/// - <see cref="FocusStateManager.CurrentState"/> is <see cref="eFocusState.FPS"/>
/// - the player is not aiming through a magnified scope
/// </summary>
internal class CenterDotOverlay : MonoBehaviour
{
    private const int TextureSize = 16;

    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<bool> HideWhenScoped { get; private set; } = null!;
    public static ConfigEntry<bool> FollowRecoil { get; private set; } = null!;
    public static ConfigEntry<string> Shape { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;
    public static ConfigEntry<string> OutlineShape { get; private set; } = null!;
    public static ConfigEntry<string> OutlineColorHex { get; private set; } = null!;
    public static ConfigEntry<int> OutlineSize { get; private set; } = null!;
    public static ConfigEntry<float> OutlineOpacity { get; private set; } = null!;
    public static ConfigEntry<float> OutlineSpacing { get; private set; } = null!;
    public static ConfigEntry<bool> MeleeChargeFeedback { get; private set; } = null!;
    public static ConfigEntry<bool> HintShowAlways { get; private set; } = null!;
    public static ConfigEntry<string> MeleeChargeShape { get; private set; } = null!;
    public static ConfigEntry<string> MeleeChargeHintColorHex { get; private set; } = null!;
    public static ConfigEntry<int> MeleeChargeRingSize { get; private set; } = null!;
    public static ConfigEntry<float> MeleeChargeRingOpacity { get; private set; } = null!;

    /// <summary>
    /// The crosshair layer's vertical mapping does not match view space.
    /// AccurateCrosshair uses this same correction so the reticle meets the recoil.
    /// </summary>
    private const float RecoilVerticalScale = 0.78f;

    private const float ScopeFovMargin = 2f;
    private const float MinimumCenterPatternSize = 9f;
    private const float MinimumOutlinePatternSize = 12f;

    internal static CenterDotOverlay? Current { get; private set; }

    private GameObject? _root;
    private RectTransform? _rect;
    private Vector2 _restAnchored;
    private IntPtr _parentPtr;
    private UI_Sprite? _halo;
    private UI_Sprite? _outline;
    private UI_Sprite? _dot;
    private UI_Sprite? _chargeRing;
    private Sprite? _solidDotSprite;
    private Sprite? _crosshairDotsSprite;
    private Sprite? _crosshairArcsSprite;
    private Sprite? _crosshairLinesSprite;
    private Sprite? _crosshairCircleSprite;
    private Sprite? _chargeDotsSprite;
    private Sprite? _chargeSemicirclesSprite;
    private Sprite? _chargeLinesSprite;
    private Sprite? _chargeCircleSprite;
    private float _nextCreateAttempt;
    private string? _appliedHex;
    private int _appliedSize = -1;
    private float _appliedOpacity = -1f;
    private string? _appliedOutlineHex;
    private int _appliedOutlineSize = -1;
    private float _appliedOutlineOpacity = -1f;
    private float _appliedOutlineSpacing = -1f;
    private ReticleShape? _appliedShape;
    private ReticleShape? _appliedOutlineShape;
    private bool _stateErrorLogged;
    private bool _scopeErrorLogged;
    private bool _recoilErrorLogged;
    private float _hipLookFov;
    private bool _hasHipLookFov;
    private bool _chargeCueActive;
    private IntPtr _chargeStatePtr;
    private bool _chargeErrorLogged;

    public CenterDotOverlay(IntPtr ptr) : base(ptr)
    {
    }

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Center",
            "Enabled",
            true,
            "Draw the crosshair while aiming in a level.");

        HideWhenScoped = config.Bind(
            "Center",
            "Hide when scoped",
            true,
            "Hide the crosshair while aiming through a magnified scope.");

        FollowRecoil = config.Bind(
            "Center",
            "Follow recoil",
            true,
            "Move the crosshair center with weapon recoil that does not follow the camera. Shotguns stay centered.");

        Shape = config.Bind(
            "Center",
            "Shape",
            "Dot",
            new ConfigDescription(
                "Center shape.",
                new AcceptableValueList<string>("Dot", "Two dots", "Arcs", "Circle", "Lines")));

        ColorHex = config.Bind(
            "Center",
            "Color",
            "5eb18e",
            "Dot color as RRGGBB or RRGGBBAA. Example: FF3355.");

        Size = config.Bind(
            "Center",
            "Size",
            3,
            new ConfigDescription(
                "Colored dot diameter in HUD units. A dark ring and the outline are drawn behind it.",
                new AcceptableValueRange<int>(1, 32)));

        Opacity = config.Bind(
            "Center",
            "Opacity",
            0.9f,
            new ConfigDescription(
                "Dot opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        OutlineShape = config.Bind(
            "Outline",
            "Shape",
            "Dot",
            new ConfigDescription(
                "Outline shape.",
                new AcceptableValueList<string>("Dot", "Two dots", "Arcs", "Circle", "Lines")));

        OutlineColorHex = config.Bind(
            "Outline",
            "Color",
            "b00a22",
            "Outer ring color as RRGGBB or RRGGBBAA. Example: FF3355.");

        OutlineSize = config.Bind(
            "Outline",
            "Size",
            9,
            new ConfigDescription(
                "Outer ring diameter in HUD units. Larger than the dot size, so the ring stays visible around the fill.",
                new AcceptableValueRange<int>(1, 64)));

        OutlineOpacity = config.Bind(
            "Outline",
            "Opacity",
            0.8f,
            new ConfigDescription(
                "Outer ring opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        OutlineSpacing = config.Bind(
            "Outline",
            "Spacing",
            3f,
            new ConfigDescription(
                "Radial distance from the center shape. Applies to every outline shape except Dot.",
                new AcceptableValueRange<float>(0f, 32f)));

        MeleeChargeFeedback = config.Bind(
            "Hint",
            "Enabled",
            true,
            "Show the shrinking melee charge hint.");

        HintShowAlways = config.Bind(
            "Hint",
            "Show always",
            false,
            "Keep the hint visible while no melee attack is charging.");

        MeleeChargeShape = config.Bind(
            "Hint",
            "Shape",
            "Circle",
            new ConfigDescription(
                "Melee charge hint shape.",
                new AcceptableValueList<string>("Two dots", "Arcs", "Circle", "Lines")));

        MeleeChargeHintColorHex = config.Bind(
            "Hint",
            "Color",
            "e5c369",
            "Melee charge hint color as RRGGBB or RRGGBBAA. Example: FF3355.");

        MeleeChargeRingSize = config.Bind(
            "Hint",
            "Size",
            24,
            new ConfigDescription(
                "Starting diameter of the melee charge hint, in HUD units.",
                new AcceptableValueRange<int>(8, 64)));

        MeleeChargeRingOpacity = config.Bind(
            "Hint",
            "Opacity",
            0.9f,
            new ConfigDescription(
                "Hint opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));
    }

    private void Update()
    {
        Transform? parent;
        try
        {
            parent = MainUiGroup();
        }
        catch (Exception ex)
        {
            if (!_stateErrorLogged)
            {
                _stateErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Center dot HUD lookup failed: {ex.Message}");
            }

            return;
        }

        if (!Alive(parent))
        {
            DestroyRoot();
            return;
        }

        if (!AttachedTo(parent!))
        {
            DestroyRoot();
            TryCreate(parent!);
            if (_root == null)
                return;
        }

        var show = ShouldShow();
        if (show)
            ApplyStyle();

        if (_root!.activeSelf != show)
        {
            _root.SetActive(show);
            RefreshHudPass();
        }

        if (show)
        {
            ApplyRecoil();
            ApplyMeleeChargeCue();
        }
        else if (_chargeCueActive)
        {
            EndChargeCue();
        }
    }

    private void OnEnable()
    {
        Current = this;
    }

    private void LateUpdate()
    {
        if (_root != null && _root.activeSelf)
            ApplyRecoil();
    }

    /// <summary>
    /// Called at the end of <see cref="FPS_RecoilSystem.FPS_Update"/> so the dot
    /// uses the recoil point from this frame.
    /// </summary>
    internal void ApplyRecoil()
    {
        if (!Alive(_rect))
            return;

        if (!FollowRecoil.Value || !TryGetRecoilCanvasDelta(out var canvasDelta))
        {
            _rect!.anchoredPosition = _restAnchored;
            return;
        }

        var canvas = CellCanvasRect();
        var parent = _rect!.parent;
        if (!Alive(canvas) || !Alive(parent))
        {
            _rect.anchoredPosition = _restAnchored;
            return;
        }

        var worldDelta = canvas!.TransformVector(new Vector3(canvasDelta.x, canvasDelta.y, 0f));
        var localDelta = parent!.InverseTransformVector(worldDelta);
        _rect.anchoredPosition = _restAnchored + new Vector2(localDelta.x, localDelta.y);
    }

    private void OnDestroy()
    {
        if (Current == this)
            Current = null;

        DestroyRoot();
    }

    /// <summary>
    /// Canvas-space offset from the visor center to <see cref="FPS_RecoilSystem.CurrentVSPos"/>.
    /// </summary>
    private bool TryGetRecoilCanvasDelta(out Vector2 canvasDelta)
    {
        canvasDelta = Vector2.zero;

        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var player) || !Alive(player))
                return false;

            if (WieldingShotgun(player!))
                return false;

            var camera = player!.FPSCamera;
            if (!Alive(camera))
                return false;

            var recoil = camera!.RecoilSystem;
            if (!Alive(recoil))
                return false;

            var view = recoil!.CurrentVSPos;
            if (view.x <= 0.001f && view.y <= 0.001f)
                return false;

            var centered = view - new Vector2(0.5f, 0.5f);
            centered.y *= RecoilVerticalScale;

            var canvas = CellCanvasRect();
            if (!Alive(canvas))
                return false;

            var size = canvas!.rect.size;
            canvasDelta = new Vector2(centered.x * size.x, centered.y * size.y);
            return true;
        }
        catch (Exception ex)
        {
            if (!_recoilErrorLogged)
            {
                _recoilErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Center dot recoil follow failed: {ex.Message}");
            }

            return false;
        }
    }

    private static bool WieldingShotgun(PlayerAgent player)
    {
        var holder = player.FPItemHolder;
        if (!Alive(holder))
            return false;

        var item = holder!.WieldedItem;
        if (!Alive(item))
            return false;

        return item!.TryCast<Shotgun>() != null;
    }

    /// <summary>
    /// Group the game moves for visor sway. The PlayerLayer canvas that contains it does not move.
    /// </summary>
    private static Transform? MainUiGroup()
    {
        var player = GuiManager.PlayerLayer;
        if (Alive(player))
        {
            var movement = player!.m_movementRoot;
            if (Alive(movement))
            {
                var transform = movement!.transform;
                if (Alive(transform))
                    return transform;
            }
        }

        var canvas = CellCanvasRect();
        if (Alive(canvas))
            return canvas;

        var gui = GuiManager.Current;
        if (Alive(gui))
        {
            var root = gui!.m_CellUIHUDRoot;
            if (Alive(root))
                return root;
        }

        return null;
    }

    private static RectTransform? CellCanvasRect()
    {
        var player = GuiManager.PlayerLayer;
        if (!Alive(player))
            return null;

        var layer = player!.GuiLayerBase;
        if (!Alive(layer))
            return null;

        var canvas = layer!.m_cellUICanvas;
        if (!Alive(canvas))
            return null;

        var rect = canvas!.m_rectTransform;
        return Alive(rect) ? rect : null;
    }

    private static SpriteRenderer? HudSprite()
    {
        var player = GuiManager.PlayerLayer;
        if (!Alive(player) || !Alive(player!.m_playerStatus))
            return null;

        var health = player.m_playerStatus!.m_health1;
        return Alive(health) ? health : null;
    }

    private bool AttachedTo(Transform parent)
    {
        return _root != null && _root.Pointer != IntPtr.Zero && _parentPtr == parent.Pointer;
    }

    private bool ShouldShow()
    {
        if (!Enabled.Value)
            return false;

        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel)
                return false;

            if (FocusStateManager.CurrentState != eFocusState.FPS)
                return false;

            return !HideWhenScoped.Value || !AimingThroughScope();
        }
        catch (Exception ex)
        {
            if (!_stateErrorLogged)
            {
                _stateErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Center dot visibility check failed: {ex.Message}");
            }

            return false;
        }
    }

    /// <summary>
    /// True while the wielded sight magnifies the world camera.
    /// </summary>
    private bool AimingThroughScope()
    {
        try
        {
            if (!PlayerManager.TryGetLocalPlayerAgent(out var player) || !Alive(player))
                return false;

            var holder = player!.FPItemHolder;
            if (!Alive(holder))
                return false;

            var look = holder!.LookCamFov;
            var aiming = holder.m_currentState == FPItemStateName.Aim;
            if (!aiming)
            {
                RememberHipFov(look);
                return false;
            }

            if (!_hasHipLookFov)
                RememberHipFovFromAimStart(look);

            var item = holder.WieldedItem;
            if (!Alive(item))
                return false;

            return SightMagnifies(item!);
        }
        catch (Exception ex)
        {
            if (!_scopeErrorLogged)
            {
                _scopeErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Center dot scope check failed: {ex.Message}");
            }

            return false;
        }
    }

    private void RememberHipFov(LerpingPairFloat? look)
    {
        if (!Alive(look))
            return;

        var hip = look!.Target;
        if (hip <= 1f)
            return;

        _hipLookFov = hip;
        _hasHipLookFov = true;
    }

    /// <summary>
    /// Aim just started before a hip sample existed. The lerp still begins at the unscoped FOV.
    /// </summary>
    private void RememberHipFovFromAimStart(LerpingPairFloat? look)
    {
        if (!Alive(look))
            return;

        var hip = look!.Start;
        if (hip <= 1f)
            return;

        _hipLookFov = hip;
        _hasHipLookFov = true;
    }

    private bool SightMagnifies(ItemEquippable item)
    {
        var parts = item.GearPartHolder;
        if (Alive(parts))
        {
            var sight = parts!.SightData;
            if (Alive(sight))
            {
                var props = sight!.SightProperties;
                if (Alive(props))
                {
                    var step = props!.WorldFovZoom;
                    if (step >= (int)WorldCameraFov.ZoomStep1 && step <= (int)WorldCameraFov.ZoomStep3)
                        return true;
                }
            }
        }

        if (!_hasHipLookFov)
            return false;

        var zoom = item.GetWorldCameraZoomFov();
        if (zoom > 1f && zoom < _hipLookFov - ScopeFovMargin)
            return true;

        var fps = item.ItemFPSData;
        if (!Alive(fps))
            return false;

        var adsFov = fps!.LookCameraFOVZoom;
        return adsFov > 1 && adsFov < _hipLookFov - ScopeFovMargin;
    }

    private void TryCreate(Transform parent)
    {
        if (Time.unscaledTime < _nextCreateAttempt)
            return;

        _nextCreateAttempt = Time.unscaledTime + 0.5f;

        GameObject? root = null;
        try
        {
            var reference = HudSprite();
            var layer = reference != null ? reference.gameObject.layer : parent.gameObject.layer;
            var sprite = CircleSprite();
            _solidDotSprite = sprite;
            _crosshairDotsSprite = ShapeSprite(ReticleShape.TwoDots, true);
            _crosshairArcsSprite = ShapeSprite(ReticleShape.Arcs, true);
            _crosshairLinesSprite = ShapeSprite(ReticleShape.Lines, true);
            _crosshairCircleSprite = ShapeSprite(ReticleShape.Circle, true);
            _chargeDotsSprite = ShapeSprite(ReticleShape.TwoDots, false);
            _chargeSemicirclesSprite = ShapeSprite(ReticleShape.Arcs, false);
            _chargeLinesSprite = ShapeSprite(ReticleShape.Lines, false);
            _chargeCircleSprite = ShapeSprite(ReticleShape.Circle, false);

            root = new GameObject("GTFO Center Aim Dot");
            var rect = root.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            PlaceAtCenter(rect);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            root.layer = layer;
            _rect = rect;
            _restAnchored = rect.anchoredPosition;

            _halo = CreateSprite(rect, sprite, "Halo", layer, reference);
            _outline = CreateSprite(rect, sprite, "Outline", layer, reference);
            _dot = CreateSprite(rect, sprite, "Dot", layer, reference);
            _chargeRing = CreateSprite(rect, _chargeCircleSprite!, "Melee Charge Hint", layer, reference);
            if (reference != null
                && _dot.m_sprite != null
                && _outline.m_sprite != null
                && _halo.m_sprite != null
                && _chargeRing.m_sprite != null)
            {
                _halo.m_sprite.sortingOrder = reference.sortingOrder + 10;
                _outline.m_sprite.sortingOrder = reference.sortingOrder + 11;
                _dot.m_sprite.sortingOrder = reference.sortingOrder + 12;
                _chargeRing.m_sprite.sortingOrder = reference.sortingOrder + 13;
            }

            _root = root;
            _parentPtr = parent.Pointer;
            _appliedHex = null;
            _appliedSize = -1;
            _appliedOpacity = -1f;
            _appliedOutlineHex = null;
            _appliedOutlineSize = -1;
            _appliedOutlineOpacity = -1f;
            _appliedOutlineSpacing = -1f;
            _appliedShape = null;
            _appliedOutlineShape = null;
            ApplyStyle();
            HideChargeRing();
            _root.SetActive(false);
            Plugin.Instance.Log.LogInfo($"Center aim dot attached to HUD group '{parent.name}'.");
        }
        catch (Exception ex)
        {
            _root = null;
            _rect = null;
            _halo = null;
            _outline = null;
            _dot = null;
            _chargeRing = null;
            _solidDotSprite = null;
            _crosshairDotsSprite = null;
            _crosshairArcsSprite = null;
            _crosshairLinesSprite = null;
            _crosshairCircleSprite = null;
            _chargeDotsSprite = null;
            _chargeSemicirclesSprite = null;
            _chargeLinesSprite = null;
            _chargeCircleSprite = null;
            _parentPtr = IntPtr.Zero;
            if (root != null)
                Destroy(root);

            Plugin.Instance.Log.LogWarning($"Center aim dot create failed: {ex.Message}");
        }
    }

    private void DestroyRoot()
    {
        if (_root != null && _root.Pointer != IntPtr.Zero)
            Destroy(_root);

        _root = null;
        _rect = null;
        _restAnchored = Vector2.zero;
        _halo = null;
        _outline = null;
        _dot = null;
        _chargeRing = null;
        _solidDotSprite = null;
        _crosshairDotsSprite = null;
        _crosshairArcsSprite = null;
        _crosshairLinesSprite = null;
        _crosshairCircleSprite = null;
        _chargeDotsSprite = null;
        _chargeSemicirclesSprite = null;
        _chargeLinesSprite = null;
        _chargeCircleSprite = null;
        _parentPtr = IntPtr.Zero;
        _appliedHex = null;
        _appliedSize = -1;
        _appliedOpacity = -1f;
        _appliedOutlineHex = null;
        _appliedOutlineSize = -1;
        _appliedOutlineOpacity = -1f;
        _appliedOutlineSpacing = -1f;
        _appliedShape = null;
        _appliedOutlineShape = null;
        _chargeCueActive = false;
        _chargeStatePtr = IntPtr.Zero;
    }

    private static UI_Sprite CreateSprite(Transform parent, Sprite sprite, string name, int layer, SpriteRenderer? reference)
    {
        var go = new GameObject(name);
        go.layer = layer;

        var rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;

        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        if (reference != null)
        {
            renderer.sortingLayerID = reference.sortingLayerID;
            renderer.sortingOrder = reference.sortingOrder + 10;
            renderer.renderingLayerMask = reference.renderingLayerMask;
            if (reference.sharedMaterial != null)
                renderer.sharedMaterial = reference.sharedMaterial;
        }

        var ui = go.AddComponent<UI_Sprite>();
        ui.m_rect = rect;
        ui.m_sprite = renderer;
        return ui;
    }

    /// <summary>
    /// Keep the dot on the visor center, stored as a local offset of the moving group.
    /// Later sway of that group carries the dot with it.
    /// </summary>
    private static void PlaceAtCenter(RectTransform rect)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;

        var canvas = CellCanvasRect();
        if (Alive(canvas))
        {
            var center = canvas!.rect.center;
            rect.position = canvas.TransformPoint(new Vector3(center.x, center.y, 0f));
            return;
        }

        rect.anchoredPosition = Vector2.zero;
    }

    /// <summary>
    /// The visor blit gathers HUD sprites on its own. A sprite added later is drawn on top, sharp and still.
    /// </summary>
    private static void RefreshHudPass()
    {
        try
        {
            var pass = UI_Core.UIPassHUD;
            if (!Alive(pass))
                return;

            pass!.UpdateRenderingData = true;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"Center dot HUD pass refresh failed: {ex.Message}");
        }
    }

    private static Sprite CircleSprite()
    {
        var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
        var center = (TextureSize - 1) * 0.5f;
        var radius = TextureSize * 0.5f;

        for (var y = 0; y < TextureSize; y++)
        {
            for (var x = 0; x < TextureSize; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                var alpha = Mathf.Clamp01(radius - distance);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags = HideFlags.HideAndDontSave;

        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, TextureSize, TextureSize),
            new Vector2(0.5f, 0.5f),
            TextureSize);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Sprite ShapeSprite(ReticleShape shape, bool compact)
    {
        const int textureSize = 32;
        var thickness = compact ? 4.5f : 1.25f;
        var dotRadius = compact ? 6f : 2.25f;
        var tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        var center = (textureSize - 1) * 0.5f;
        var radius = textureSize * 0.5f - 1f;
        var dotOffset = radius - dotRadius;

        for (var y = 0; y < textureSize; y++)
        {
            for (var x = 0; x < textureSize; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                var ringAlpha = Mathf.Clamp01(thickness - Mathf.Abs(distance - radius));
                float alpha;

                switch (shape)
                {
                    case ReticleShape.TwoDots:
                        var leftDistance = Mathf.Sqrt((dx + dotOffset) * (dx + dotOffset) + dy * dy);
                        var rightDistance = Mathf.Sqrt((dx - dotOffset) * (dx - dotOffset) + dy * dy);
                        alpha = Mathf.Clamp01(dotRadius + 0.5f - Mathf.Min(leftDistance, rightDistance));
                        break;
                    case ReticleShape.Arcs:
                        var arcCutoff = compact ? 0.52f : 0.68f;
                        alpha = ringAlpha * Mathf.Clamp01(Mathf.Abs(dx) - radius * arcCutoff);
                        break;
                    case ReticleShape.Lines:
                        var leftLineDistance = Mathf.Abs(dx + dotOffset);
                        var rightLineDistance = Mathf.Abs(dx - dotOffset);
                        var lineAlpha = Mathf.Clamp01(
                            thickness - Mathf.Min(leftLineDistance, rightLineDistance));
                        var lineLength = Mathf.Clamp01(radius * 0.72f + 0.5f - Mathf.Abs(dy));
                        alpha = lineAlpha * lineLength;
                        break;
                    default:
                        alpha = ringAlpha;
                        break;
                }

                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags = HideFlags.HideAndDontSave;

        var sprite = Sprite.Create(
            tex,
            new Rect(0f, 0f, textureSize, textureSize),
            new Vector2(0.5f, 0.5f),
            textureSize);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private void ApplyStyle()
    {
        if (_dot == null || _outline == null || _halo == null)
            return;

        var hex = ColorHex.Value;
        var size = Mathf.Clamp(Size.Value, 1, 32);
        var opacity = Mathf.Clamp01(Opacity.Value);
        var shape = ParseShape(Shape.Value);
        var outlineHex = OutlineColorHex.Value;
        var outlineSize = Mathf.Clamp(OutlineSize.Value, 1, 64);
        var outlineOpacity = Mathf.Clamp01(OutlineOpacity.Value);
        var outlineSpacing = Mathf.Clamp(OutlineSpacing.Value, 0f, 32f);
        var outlineShape = ParseShape(OutlineShape.Value);
        if (hex == _appliedHex
            && size == _appliedSize
            && Mathf.Approximately(opacity, _appliedOpacity)
            && shape == _appliedShape
            && outlineHex == _appliedOutlineHex
            && outlineSize == _appliedOutlineSize
            && Mathf.Approximately(outlineOpacity, _appliedOutlineOpacity)
            && Mathf.Approximately(outlineSpacing, _appliedOutlineSpacing)
            && outlineShape == _appliedOutlineShape)
            return;

        _appliedHex = hex;
        _appliedSize = size;
        _appliedOpacity = opacity;
        _appliedShape = shape;
        _appliedOutlineHex = outlineHex;
        _appliedOutlineSize = outlineSize;
        _appliedOutlineOpacity = outlineOpacity;
        _appliedOutlineSpacing = outlineSpacing;
        _appliedOutlineShape = outlineShape;

        var color = ParseColor(hex);
        color.a *= opacity;
        var outline = ParseColor(outlineHex, new Color(1f, 0.2f, 0.333f, 1f));
        outline.a *= outlineOpacity;
        var visibleCenterSize = VisibleCenterSize(size, shape);
        var visibleOutlineSize = VisibleOutlineSize(
            outlineSize,
            outlineShape,
            visibleCenterSize,
            outlineSpacing);
        SetRendererSprite(_halo, SelectedCrosshairSprite(outlineShape));
        SetRendererSprite(_outline, SelectedCrosshairSprite(shape));
        SetRendererSprite(_dot, SelectedCrosshairSprite(shape));
        SetSprite(_halo, outline, visibleOutlineSize);
        SetSprite(_outline, new Color(0f, 0f, 0f, opacity), visibleCenterSize + 3f);
        SetSprite(_dot, color, visibleCenterSize);
    }

    private static float VisibleCenterSize(float configuredSize, ReticleShape shape)
    {
        return shape == ReticleShape.Dot
            ? configuredSize
            : Mathf.Max(configuredSize, MinimumCenterPatternSize);
    }

    private static float VisibleOutlineSize(
        float configuredSize,
        ReticleShape shape,
        float visibleCenterSize,
        float spacing)
    {
        if (shape == ReticleShape.Dot)
            return Mathf.Max(configuredSize, visibleCenterSize + 6f);

        var readableSize = Mathf.Max(configuredSize, MinimumOutlinePatternSize);
        return Mathf.Max(readableSize, visibleCenterSize + spacing * 2f);
    }

    private Sprite? SelectedCrosshairSprite(ReticleShape shape)
    {
        return shape switch
        {
            ReticleShape.TwoDots => _crosshairDotsSprite,
            ReticleShape.Arcs => _crosshairArcsSprite,
            ReticleShape.Circle => _crosshairCircleSprite,
            ReticleShape.Lines => _crosshairLinesSprite,
            _ => _solidDotSprite
        };
    }

    private static void SetRendererSprite(UI_Sprite? ui, Sprite? sprite)
    {
        var renderer = ui?.m_sprite;
        if (renderer != null && sprite != null)
            renderer.sprite = sprite;
    }

    private static void SetSprite(UI_Sprite ui, Color color, float size)
    {
        var rect = ui.m_rect;
        var renderer = ui.m_sprite;
        if (rect != null)
            rect.sizeDelta = new Vector2(size, size);

        if (renderer != null)
            renderer.color = color;

        ui.SetupSize();

        if (renderer != null)
            renderer.transform.localScale = new Vector3(size, size, 1f);
    }

    /// <summary>
    /// Charge progress matches <c>MWS_ChargeUp.Update</c>: elapsed / max damage time, clamped to 1.
    /// </summary>
    private void ApplyMeleeChargeCue()
    {
        if (!MeleeChargeFeedback.Value)
        {
            if (_chargeCueActive)
                EndChargeCue();
            else
                HideChargeRing();
            return;
        }

        if (!TryGetMeleeCharge(out var progress, out var autoStrikeWarningProgress, out var statePtr))
        {
            if (_chargeCueActive)
                EndChargeCue();

            if (HintShowAlways.Value)
                ShowHintAtOriginalSize();
            else
                HideChargeRing();
            return;
        }

        var chargeJustStarted = !_chargeCueActive || statePtr != _chargeStatePtr;
        if (chargeJustStarted)
        {
            _chargeCueActive = true;
            _chargeStatePtr = statePtr;
        }

        var crosshairDiameter = CurrentCrosshairDiameter();
        var targetSize = HintOriginalSize(crosshairDiameter);
        var diameter = chargeJustStarted
            ? targetSize
            : Mathf.Lerp(targetSize, crosshairDiameter, progress);
        var color = ParseColor(MeleeChargeHintColorHex.Value, new Color(1f, 0.05f, 0.08f, 1f));
        color.a *= Mathf.Clamp01(MeleeChargeRingOpacity.Value);
        var renderer = _chargeRing?.m_sprite;
        if (renderer != null)
            renderer.sprite = SelectedChargeHintSprite();
        if (autoStrikeWarningProgress > 0f)
        {
            ReadConfiguredColors(out var halo, out var ring, out var dot);
            var easedWarning = autoStrikeWarningProgress * autoStrikeWarningProgress;
            PaintSprites(
                Color.Lerp(halo, InvertAndBrighten(halo), easedWarning),
                Color.Lerp(ring, InvertAndBrighten(ring), easedWarning),
                Color.Lerp(dot, InvertAndBrighten(dot), easedWarning));
            color = Color.Lerp(color, InvertAndBrighten(color), easedWarning);
        }
        SetSprite(_chargeRing!, color, diameter);
    }

    private void ShowHintAtOriginalSize()
    {
        var renderer = _chargeRing?.m_sprite;
        if (renderer != null)
            renderer.sprite = SelectedChargeHintSprite();

        var color = ParseColor(MeleeChargeHintColorHex.Value, new Color(1f, 0.05f, 0.08f, 1f));
        color.a *= Mathf.Clamp01(MeleeChargeRingOpacity.Value);
        SetSprite(_chargeRing!, color, HintOriginalSize(CurrentCrosshairDiameter()));
    }

    private float CurrentCrosshairDiameter()
    {
        var centerShape = ParseShape(Shape.Value);
        var outlineShape = ParseShape(OutlineShape.Value);
        var visibleCenterSize = VisibleCenterSize(Mathf.Clamp(Size.Value, 1, 32), centerShape);
        var visibleOutlineSize = VisibleOutlineSize(
            Mathf.Clamp(OutlineSize.Value, 1, 64),
            outlineShape,
            visibleCenterSize,
            Mathf.Clamp(OutlineSpacing.Value, 0f, 32f));
        return Mathf.Max(visibleOutlineSize, visibleCenterSize + 3f);
    }

    private static float HintOriginalSize(float crosshairDiameter)
    {
        return Mathf.Max(
            Mathf.Clamp(MeleeChargeRingSize.Value, 8, 64),
            crosshairDiameter + 1f);
    }

    private Sprite? SelectedChargeHintSprite()
    {
        return ParseShape(MeleeChargeShape.Value) switch
        {
            ReticleShape.TwoDots => _chargeDotsSprite,
            ReticleShape.Arcs => _chargeSemicirclesSprite,
            ReticleShape.Lines => _chargeLinesSprite,
            _ => _chargeCircleSprite
        };
    }

    private static ReticleShape ParseShape(string value)
    {
        return value switch
        {
            "Two dots" => ReticleShape.TwoDots,
            "Arcs" => ReticleShape.Arcs,
            "Circle" => ReticleShape.Circle,
            "Lines" => ReticleShape.Lines,
            _ => ReticleShape.Dot
        };
    }

    private void EndChargeCue()
    {
        _chargeCueActive = false;
        _chargeStatePtr = IntPtr.Zero;
        HideChargeRing();
        _appliedHex = null;
        ApplyStyle();
    }

    private bool TryGetMeleeCharge(out float progress, out float autoStrikeWarningProgress, out IntPtr statePtr)
    {
        progress = 0f;
        autoStrikeWarningProgress = 0f;
        statePtr = IntPtr.Zero;

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

            var melee = item!.TryCast<MeleeWeaponFirstPerson>();
            if (!Alive(melee))
                return false;

            var stateName = melee!.CurrentStateName;
            if (stateName != eMeleeWeaponState.AttackChargeUpLeft
                && stateName != eMeleeWeaponState.AttackChargeUpRight)
                return false;

            var state = melee.CurrentState;
            if (!Alive(state))
                return false;

            var chargeUp = state!.TryCast<MWS_ChargeUp>();
            if (!Alive(chargeUp))
                return false;

            statePtr = chargeUp!.Pointer;
            var elapsed = chargeUp.m_elapsed;
            var maxDamage = chargeUp.m_maxDamageTime;
            progress = maxDamage > 0.0001f ? Mathf.Clamp01(elapsed / maxDamage) : 1f;

            var anim = melee.MeleeAnimationData;
            if (Alive(anim))
            {
                var strikeAt = anim!.AutoAttackTime;
                if (progress >= 1f && strikeAt > maxDamage)
                {
                    var warningDuration = strikeAt - maxDamage;
                    autoStrikeWarningProgress = Mathf.Clamp01(
                        (elapsed - maxDamage) / warningDuration);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_chargeErrorLogged)
            {
                _chargeErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Center dot melee charge cue failed: {ex.Message}");
            }

            return false;
        }
    }

    private void HideChargeRing()
    {
        var renderer = _chargeRing?.m_sprite;
        if (renderer != null)
            renderer.color = new Color(1f, 0.05f, 0.08f, 0f);
    }

    private void ReadConfiguredColors(out Color halo, out Color ring, out Color dot)
    {
        var opacity = Mathf.Clamp01(Opacity.Value);
        var color = ParseColor(ColorHex.Value);
        color.a *= opacity;
        var outline = ParseColor(OutlineColorHex.Value, new Color(1f, 0.2f, 0.333f, 1f));
        outline.a *= Mathf.Clamp01(OutlineOpacity.Value);
        halo = outline;
        ring = new Color(0f, 0f, 0f, opacity);
        dot = color;
    }

    private static Color InvertAndBrighten(Color color)
    {
        var inverted = new Color(1f - color.r, 1f - color.g, 1f - color.b, 1f);
        var bright = Color.Lerp(inverted, Color.white, 0.35f);
        bright.a = 1f;
        return bright;
    }

    private void PaintSprites(Color halo, Color ring, Color dot)
    {
        SetRendererColor(_halo, halo);
        SetRendererColor(_outline, ring);
        SetRendererColor(_dot, dot);
    }

    private static void SetRendererColor(UI_Sprite? ui, Color color)
    {
        var renderer = ui?.m_sprite;
        if (renderer != null)
            renderer.color = color;
    }

    private static Color ParseColor(string raw, Color? fallback = null)
    {
        var missing = fallback ?? DefaultColor();
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return missing;

        if (!text.StartsWith("#", StringComparison.Ordinal))
            text = "#" + text;

        return ColorUtility.TryParseHtmlString(text, out var color) ? color : missing;
    }

    private static Color DefaultColor()
    {
        return new Color(0f, 0.898f, 1f, 1f);
    }

    private static bool Alive(Il2CppObjectBase? obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }
}
