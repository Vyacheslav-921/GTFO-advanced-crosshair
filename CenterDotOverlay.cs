using System;
using BepInEx.Configuration;
using FirstPersonItem;
using GameData;
using Gear;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using UnityEngine;

namespace V921;

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
/// A melee charge blinks the dot, faster until <see cref="MWS_ChargeUp.m_maxDamageTime"/>.
/// For the last 200ms before <see cref="MeleeAnimationSetDataBlock.AutoAttackTime"/>,
/// when that charge swings on its own, the colors invert and brighten.
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
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;
    public static ConfigEntry<string> OutlineColorHex { get; private set; } = null!;
    public static ConfigEntry<int> OutlineSize { get; private set; } = null!;
    public static ConfigEntry<float> OutlineOpacity { get; private set; } = null!;
    public static ConfigEntry<bool> MeleeChargeFeedback { get; private set; } = null!;
    public static ConfigEntry<float> MeleeHintOpacity { get; private set; } = null!;

    /// <summary>
    /// The crosshair layer's vertical mapping does not match view space.
    /// AccurateCrosshair uses this same correction so the reticle meets the recoil.
    /// </summary>
    private const float RecoilVerticalScale = 0.78f;

    private const float ScopeFovMargin = 2f;

    /// <summary>
    /// Half-period of the charge blink at the start and at full charge.
    /// The gap shrinks with <see cref="MWS_ChargeUp.m_elapsed"/> / <see cref="MWS_ChargeUp.m_maxDamageTime"/>,
    /// the same progress the vanilla charge crosshair uses.
    /// </summary>
    private const float ChargeBlinkIntervalStart = 0.18f;

    private const float ChargeBlinkIntervalFull = 0.04f;

    /// <summary>
    /// Lead before <see cref="MeleeAnimationSetDataBlock.AutoAttackTime"/>, when a held charge swings by itself.
    /// </summary>
    private const float AutoStrikeWarningLead = 0.8f;

    internal static CenterDotOverlay? Current { get; private set; }

    private GameObject? _root;
    private RectTransform? _rect;
    private Vector2 _restAnchored;
    private IntPtr _parentPtr;
    private UI_Sprite? _halo;
    private UI_Sprite? _outline;
    private UI_Sprite? _dot;
    private float _nextCreateAttempt;
    private string? _appliedHex;
    private int _appliedSize = -1;
    private float _appliedOpacity = -1f;
    private string? _appliedOutlineHex;
    private int _appliedOutlineSize = -1;
    private float _appliedOutlineOpacity = -1f;
    private bool _stateErrorLogged;
    private bool _scopeErrorLogged;
    private bool _recoilErrorLogged;
    private float _hipLookFov;
    private bool _hasHipLookFov;
    private bool _chargeCueActive;
    private bool _chargeBlinkOn = true;
    private float _chargeBlinkTimer;
    private IntPtr _chargeStatePtr;
    private bool _chargeErrorLogged;

    public CenterDotOverlay(IntPtr ptr) : base(ptr)
    {
    }

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Center dot",
            "Enabled",
            true,
            "Draw a dot at the visor center while aiming in a level.");

        HideWhenScoped = config.Bind(
            "Center dot",
            "Hide when scoped",
            true,
            "Hide the dot while aiming through a magnified scope.");

        FollowRecoil = config.Bind(
            "Center dot",
            "Follow recoil",
            true,
            "Move the dot with weapon recoil that does not follow the camera. Shotguns stay on the visor center.");

        ColorHex = config.Bind(
            "Center dot",
            "Color",
            "5eb18e",
            "Dot color as RRGGBB or RRGGBBAA. Example: FF3355.");

        Size = config.Bind(
            "Center dot",
            "Size",
            3,
            new ConfigDescription(
                "Colored dot diameter in HUD units. A dark ring and the outline are drawn behind it.",
                new AcceptableValueRange<int>(1, 32)));

        Opacity = config.Bind(
            "Center dot",
            "Opacity",
            0.9f,
            new ConfigDescription(
                "Dot opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        OutlineColorHex = config.Bind(
            "Center dot",
            "Outline color",
            "b00a22",
            "Outer ring color as RRGGBB or RRGGBBAA. Example: FF3355.");

        OutlineSize = config.Bind(
            "Center dot",
            "Outline size",
            9,
            new ConfigDescription(
                "Outer ring diameter in HUD units. Larger than the dot size, so the ring stays visible around the fill.",
                new AcceptableValueRange<int>(1, 64)));

        OutlineOpacity = config.Bind(
            "Center dot",
            "Outline opacity",
            0.8f,
            new ConfigDescription(
                "Outer ring opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        MeleeChargeFeedback = config.Bind(
            "Center dot",
            "Melee charge feedback",
            true,
            "While a melee charge is building, blink the dot faster until it is full. For the last 200ms before that charged attack swings on its own, invert the colors and brighten them.");

        MeleeHintOpacity = config.Bind(
            "Center dot",
            "Melee hint opacity",
            0.1f,
            new ConfigDescription(
                "Opacity of the dot, ring, and outline during the dim half of a melee charge blink. 0 hides them, 1 keeps them solid.",
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
            if (reference != null && _dot.m_sprite != null && _outline.m_sprite != null && _halo.m_sprite != null)
            {
                _halo.m_sprite.sortingOrder = reference.sortingOrder + 10;
                _outline.m_sprite.sortingOrder = reference.sortingOrder + 11;
                _dot.m_sprite.sortingOrder = reference.sortingOrder + 12;
            }

            _root = root;
            _parentPtr = parent.Pointer;
            _appliedHex = null;
            _appliedSize = -1;
            _appliedOpacity = -1f;
            _appliedOutlineHex = null;
            _appliedOutlineSize = -1;
            _appliedOutlineOpacity = -1f;
            ApplyStyle();
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
        _parentPtr = IntPtr.Zero;
        _appliedHex = null;
        _appliedSize = -1;
        _appliedOpacity = -1f;
        _appliedOutlineHex = null;
        _appliedOutlineSize = -1;
        _appliedOutlineOpacity = -1f;
        _chargeCueActive = false;
        _chargeStatePtr = IntPtr.Zero;
        _chargeBlinkOn = true;
        _chargeBlinkTimer = 0f;
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
            pass.GatherRenderingData();
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

    private void ApplyStyle()
    {
        if (_dot == null || _outline == null || _halo == null)
            return;

        var hex = ColorHex.Value;
        var size = Mathf.Clamp(Size.Value, 1, 32);
        var opacity = Mathf.Clamp01(Opacity.Value);
        var outlineHex = OutlineColorHex.Value;
        var outlineSize = Mathf.Clamp(OutlineSize.Value, 1, 64);
        var outlineOpacity = Mathf.Clamp01(OutlineOpacity.Value);
        if (hex == _appliedHex
            && size == _appliedSize
            && Mathf.Approximately(opacity, _appliedOpacity)
            && outlineHex == _appliedOutlineHex
            && outlineSize == _appliedOutlineSize
            && Mathf.Approximately(outlineOpacity, _appliedOutlineOpacity))
            return;

        _appliedHex = hex;
        _appliedSize = size;
        _appliedOpacity = opacity;
        _appliedOutlineHex = outlineHex;
        _appliedOutlineSize = outlineSize;
        _appliedOutlineOpacity = outlineOpacity;

        var color = ParseColor(hex);
        color.a *= opacity;
        var outline = ParseColor(outlineHex, new Color(1f, 0.2f, 0.333f, 1f));
        outline.a *= outlineOpacity;
        SetSprite(_halo, outline, outlineSize);
        SetSprite(_outline, new Color(0f, 0f, 0f, opacity), size + 3);
        SetSprite(_dot, color, size);
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
    /// The auto swing is <see cref="MeleeAnimationSetDataBlock.AutoAttackTime"/> on that same clock.
    /// </summary>
    private void ApplyMeleeChargeCue()
    {
        if (!MeleeChargeFeedback.Value || !TryGetMeleeCharge(out var progress, out var releaseWarning, out var statePtr))
        {
            if (_chargeCueActive)
                EndChargeCue();
            return;
        }

        if (!_chargeCueActive || statePtr != _chargeStatePtr)
        {
            _chargeCueActive = true;
            _chargeStatePtr = statePtr;
            _chargeBlinkOn = true;
            _chargeBlinkTimer = 0f;
        }

        ReadConfiguredColors(out var halo, out var ring, out var dot);
        if (releaseWarning)
        {
            PaintSprites(InvertAndBrighten(halo), InvertAndBrighten(ring), InvertAndBrighten(dot));
            return;
        }

        if (progress >= 1f)
        {
            PaintSprites(halo, ring, dot);
            return;
        }

        var interval = Mathf.Lerp(ChargeBlinkIntervalStart, ChargeBlinkIntervalFull, progress);
        _chargeBlinkTimer += Time.deltaTime;
        if (_chargeBlinkTimer >= interval)
        {
            _chargeBlinkTimer = 0f;
            _chargeBlinkOn = !_chargeBlinkOn;
        }

        if (!_chargeBlinkOn)
        {
            var hintOpacity = Mathf.Clamp01(MeleeHintOpacity.Value);
            halo.a = hintOpacity;
            ring.a = hintOpacity;
            dot.a = hintOpacity;
        }

        PaintSprites(halo, ring, dot);
    }

    private void EndChargeCue()
    {
        _chargeCueActive = false;
        _chargeStatePtr = IntPtr.Zero;
        _chargeBlinkOn = true;
        _chargeBlinkTimer = 0f;
        _appliedHex = null;
        ApplyStyle();
    }

    private bool TryGetMeleeCharge(out float progress, out bool releaseWarning, out IntPtr statePtr)
    {
        progress = 0f;
        releaseWarning = false;
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
                releaseWarning = progress >= 1f
                    && strikeAt > 0f
                    && elapsed >= strikeAt - AutoStrikeWarningLead;
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

    /// <summary>
    /// Flip the hue, then lift it toward white so the strike warning reads brighter than the resting dot.
    /// </summary>
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
