using System;
using System.Collections.Generic;
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
    TwoDots,
    Arcs,
    Circle,
    Lines
}

/// <summary>
/// Draws a configurable crosshair shape at the visor center while aiming.
///
/// The player HUD is Cell UI. <see cref="PlayerGuiLayer.m_movementRoot"/> is the group
/// the game shifts for visor sway; the PlayerLayer canvas above it stays put.
/// The shape is a HUD sprite under that movement root, placed at the canvas center,
/// then registered with <see cref="UI_Core.UIPassHUD"/> so the visor blit
/// (pixel, skew, opacity) includes it.
///
/// Recoil that does not rotate the camera is <see cref="FPS_RecoilSystem.CurrentVSPos"/>,
/// a viewport point around (0.5, 0.5). That offset is added in canvas space on top of
/// the sway rest position. Shotguns stay centered: their pellets follow the camera.
///
/// The crosshair has three parts from the center outward: Center, Outline, and Hint.
///
/// A melee charge draws a configurable shape, then shrinks it
/// to the crosshair's outer edge as <see cref="MWS_ChargeUp.m_elapsed"/> reaches full damage.
///
/// Shown only when all of these are true:
/// - <see cref="GameStateManager.CurrentStateName"/> is <see cref="eGameStateName.InLevel"/>
/// - <see cref="FocusStateManager.CurrentState"/> is <see cref="eFocusState.FPS"/>
/// - the player is not aiming through a magnified scope
/// </summary>
internal class CrosshairOverlay : MonoBehaviour
{
    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<bool> ShowWeapons { get; private set; } = null!;
    public static ConfigEntry<bool> ShowTools { get; private set; } = null!;
    public static ConfigEntry<bool> ShowPacks { get; private set; } = null!;
    public static ConfigEntry<bool> ShowItems { get; private set; } = null!;
    public static ConfigEntry<bool> HideWhenScoped { get; private set; } = null!;
    public static ConfigEntry<bool> FollowRecoil { get; private set; } = null!;
    public static ConfigEntry<string> CenterShape { get; private set; } = null!;
    public static ConfigEntry<string> CenterColorHex { get; private set; } = null!;
    public static ConfigEntry<int> CenterSize { get; private set; } = null!;
    public static ConfigEntry<float> CenterThickness { get; private set; } = null!;
    public static ConfigEntry<float> CenterOpacity { get; private set; } = null!;
    public static ConfigEntry<string> AutoAttackBlinkColorHex { get; private set; } = null!;
    public static ConfigEntry<float> AutoAttackBlinkThicknessIncrease { get; private set; } = null!;
    public static ConfigEntry<string> OutlineShape { get; private set; } = null!;
    public static ConfigEntry<string> OutlineColorHex { get; private set; } = null!;
    public static ConfigEntry<int> OutlineSize { get; private set; } = null!;
    public static ConfigEntry<float> OutlineThickness { get; private set; } = null!;
    public static ConfigEntry<float> OutlineOpacity { get; private set; } = null!;
    public static ConfigEntry<float> OutlineSpacing { get; private set; } = null!;
    public static ConfigEntry<bool> HintEnabled { get; private set; } = null!;
    public static ConfigEntry<bool> HintShowAlways { get; private set; } = null!;
    public static ConfigEntry<string> HintShape { get; private set; } = null!;
    public static ConfigEntry<string> HintColorHex { get; private set; } = null!;
    public static ConfigEntry<string> HintAutoAttackColorHex { get; private set; } = null!;
    public static ConfigEntry<float> HintAutoAttackThicknessIncrease { get; private set; } = null!;
    public static ConfigEntry<int> HintSize { get; private set; } = null!;
    public static ConfigEntry<float> HintThickness { get; private set; } = null!;
    public static ConfigEntry<float> HintOpacity { get; private set; } = null!;

    /// <summary>
    /// The crosshair layer's vertical mapping does not match view space.
    /// AccurateCrosshair uses this same correction so the reticle meets the recoil.
    /// </summary>
    private const float RecoilVerticalScale = 0.78f;

    private const float ScopeFovMargin = 2f;
    private const float MinimumCenterShapeSize = 9f;
    private const float MinimumOutlineShapeSize = 12f;
    private const float HintReadyPadding = 2f;
    private const float HeldItemCenterClearance = 12f;

    internal static CrosshairOverlay? Current { get; private set; }

    private GameObject? _root;
    private RectTransform? _rect;
    private Vector2 _restAnchored;
    private IntPtr _parentPtr;
    private UI_Sprite? _centerShape;
    private UI_Sprite? _outlineShape;
    private UI_Sprite? _hintShape;
    private readonly PackHintOverlay _packHints = new();
    private readonly ItemHintOverlay _itemHints = new();
    private readonly ToolHintOverlay _toolHints = new();
    private readonly ScopeAmmoOverlay _scopeAmmo = new();
    private Sprite? _centerDotsSprite;
    private Sprite? _centerArcsSprite;
    private Sprite? _centerLinesSprite;
    private Sprite? _centerCircleSprite;
    private Sprite? _outlineDotsSprite;
    private Sprite? _outlineArcsSprite;
    private Sprite? _outlineLinesSprite;
    private Sprite? _outlineCircleSprite;
    private Sprite? _hintDotsSprite;
    private Sprite? _hintArcsSprite;
    private Sprite? _hintLinesSprite;
    private Sprite? _hintCircleSprite;
    private readonly Dictionary<int, Sprite?[]> _hintShapeCache = new();
    private float _nextCreateAttempt;
    private string? _appliedCenterColorHex;
    private int _appliedCenterSize = -1;
    private float _appliedCenterOpacity = -1f;
    private string? _appliedOutlineColorHex;
    private int _appliedOutlineSize = -1;
    private float _appliedOutlineOpacity = -1f;
    private float _appliedOutlineSpacing = -1f;
    private float _appliedCenterThickness = -1f;
    private float _appliedOutlineThickness = -1f;
    private float _appliedHintThickness = -1f;
    private Sprite? _appliedHintSprite;
    private ReticleShape? _appliedCenterShape;
    private ReticleShape? _appliedOutlineShape;
    private bool _stateErrorLogged;
    private bool _scopeErrorLogged;
    private bool _recoilErrorLogged;
    private float _hipLookFov;
    private bool _hasHipLookFov;
    private bool _hintChargeActive;
    private IntPtr _chargeStatePtr;
    private bool _chargeErrorLogged;
    private bool _crosshairShapesActive;
    private bool _autoAttackBlinkActive;
    private float _autoAttackBlinkStartedAt = -1f;
    private float _appliedBlinkAmount = -1f;
    private bool _appliedCenterClearance;
    private IntPtr _autoAttackBlinkChargePtr;
    private int _autoAttackBlinkTriggers;

    public CrosshairOverlay(IntPtr ptr) : base(ptr)
    {
    }

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "1 - General",
            "Enabled",
            true,
            "Draw the crosshair while aiming in a level.");

        ShowWeapons = config.Bind(
            "Show for:",
            "Weapons",
            true,
            "Show the crosshair while holding a primary weapon, special weapon, or melee weapon.");

        ShowTools = config.Bind(
            "Show for:",
            "Tools",
            false,
            "Show the crosshair while holding a class tool or the hacking tool.");

        ShowPacks = config.Bind(
            "Show for:",
            "Packs",
            false,
            "Show the crosshair while holding a resource pack.");

        ShowItems = config.Bind(
            "Show for:",
            "Items",
            false,
            "Show the crosshair while holding consumables or carried objects.");

        HideWhenScoped = config.Bind(
            "2 - Center",
            "Hide when scoped",
            true,
            "Hide the crosshair while aiming through a magnified scope.");

        FollowRecoil = config.Bind(
            "2 - Center",
            "Follow recoil",
            true,
            "Move the crosshair center with weapon recoil that does not follow the camera. Shotguns stay centered.");

        CenterShape = config.Bind(
            "2 - Center",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Center shape.",
                new AcceptableValueList<string>("Two dots", "Arcs", "Circle", "Lines")));

        CenterColorHex = config.Bind(
            "2 - Center",
            "Color",
            "5eb18e",
            "Center color as RRGGBB or RRGGBBAA. Example: FF3355.");

        CenterSize = config.Bind(
            "2 - Center",
            "Size",
            22,
            new ConfigDescription(
                "Center shape diameter in HUD units.",
                new AcceptableValueRange<int>(1, 32)));

        CenterThickness = config.Bind(
            "2 - Center",
            "Thickness",
            2.95f,
            new ConfigDescription(
                "Center shape thickness. For Two dots, this controls dot size.",
                new AcceptableValueRange<float>(0.5f, 16f)));

        CenterOpacity = config.Bind(
            "2 - Center",
            "Opacity",
            0.7f,
            new ConfigDescription(
                "Center opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        AutoAttackBlinkColorHex = config.Bind(
            "2 - Center",
            "Auto attack blink color",
            "ff2a2a",
            "Color Outline blinks to just before an automatic melee attack, as RRGGBB or RRGGBBAA.");

        AutoAttackBlinkThicknessIncrease = config.Bind(
            "2 - Center",
            "Auto attack blink thickness increase",
            90f,
            new ConfigDescription(
                "Percentage by which Center and Outline thickness grows during the auto-attack blink.",
                new AcceptableValueRange<float>(0f, 200f)));

        OutlineShape = config.Bind(
            "3 - Outline",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Outline shape.",
                new AcceptableValueList<string>("Two dots", "Arcs", "Circle", "Lines")));

        OutlineColorHex = config.Bind(
            "3 - Outline",
            "Color",
            "b00a22",
            "Outline color as RRGGBB or RRGGBBAA. Example: FF3355.");

        OutlineSize = config.Bind(
            "3 - Outline",
            "Size",
            32,
            new ConfigDescription(
                "Outline shape diameter in HUD units.",
                new AcceptableValueRange<int>(1, 64)));

        OutlineThickness = config.Bind(
            "3 - Outline",
            "Thickness",
            3.95f,
            new ConfigDescription(
                "Outline shape thickness. For Two dots, this controls dot size.",
                new AcceptableValueRange<float>(0.5f, 16f)));

        OutlineOpacity = config.Bind(
            "3 - Outline",
            "Opacity",
            0.8f,
            new ConfigDescription(
                "Outline opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

        OutlineSpacing = config.Bind(
            "3 - Outline",
            "Spacing",
            3f,
            new ConfigDescription(
                "Radial distance from the center shape.",
                new AcceptableValueRange<float>(0f, 32f)));

        HintEnabled = config.Bind(
            "4 - Hint",
            "Enabled",
            true,
            "Show the shrinking melee charge hint.");

        HintShowAlways = config.Bind(
            "4 - Hint",
            "Show always",
            false,
            "Keep the hint visible while no melee attack is charging.");

        HintShape = config.Bind(
            "4 - Hint",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Melee charge hint shape.",
                new AcceptableValueList<string>("Two dots", "Arcs", "Circle", "Lines")));

        HintColorHex = config.Bind(
            "4 - Hint",
            "Color",
            "ffffff",
            "Melee charge hint color as RRGGBB or RRGGBBAA. Example: FF3355.");

        HintAutoAttackColorHex = config.Bind(
            "4 - Hint",
            "Auto attack color",
            "cb2769",
            "Hint color as an automatic melee attack approaches, as RRGGBB or RRGGBBAA.");

        HintAutoAttackThicknessIncrease = config.Bind(
            "4 - Hint",
            "Auto attack thickness increase",
            110f,
            new ConfigDescription(
                "Percentage by which hint thickness grows as an automatic melee attack approaches.",
                new AcceptableValueRange<float>(0f, 200f)));

        HintSize = config.Bind(
            "4 - Hint",
            "Size",
            78,
            new ConfigDescription(
                "Starting diameter of the melee charge hint, in HUD units.",
                new AcceptableValueRange<int>(8, 64)));

        HintThickness = config.Bind(
            "4 - Hint",
            "Thickness",
            1.25f,
            new ConfigDescription(
                "Hint shape thickness. For Two dots, this controls dot size.",
                new AcceptableValueRange<float>(0.5f, 16f)));

        HintOpacity = config.Bind(
            "4 - Hint",
            "Opacity",
            0.8f,
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
                Plugin.Instance.Log.LogWarning($"Crosshair HUD lookup failed: {ex.Message}");
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

        var showCrosshair = ShouldShow();
        var hintParent = _rect;
        var hintLayer = _rect != null ? _rect.gameObject.layer : 0;
        var inLevel = InLevelFps();
        var scoped = inLevel && AimingThroughScope();
        var inHudContext = inLevel && (!HideWhenScoped.Value || !scoped);
        var crosshairDiameter = CurrentCrosshairDiameter();
        var showPackHint = _packHints.Update(hintParent, hintLayer, inHudContext, crosshairDiameter);
        var showItemHint = _itemHints.Update(hintParent, hintLayer, inHudContext, crosshairDiameter);
        var showToolHint = _toolHints.Update(hintParent, hintLayer, inHudContext);
        var showScopeAmmo = _scopeAmmo.Update(hintParent, hintLayer, scoped);
        var showRoot = showCrosshair || showPackHint || showItemHint || showToolHint || showScopeAmmo;
        if (showCrosshair)
        {
            if (!_crosshairShapesActive)
                InvalidateAppliedStyle();
            ApplyStyle();
        }

        SetCrosshairShapesActive(showCrosshair);

        if (showCrosshair)
            ApplyHint();
        else if (_hintChargeActive)
            EndHintCharge();

        if (_root!.activeSelf != showRoot)
        {
            _root.SetActive(showRoot);
            RefreshHudPass();
        }

        if (showRoot)
            ApplyRecoil();
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
    /// Starts the same-time blink the vanilla reticle uses before a charged auto-attack.
    /// </summary>
    internal void StartAutoAttackBlink()
    {
        TryGetMeleeCharge(out _, out _, out var statePtr);
        if (statePtr != _autoAttackBlinkChargePtr)
        {
            _autoAttackBlinkChargePtr = statePtr;
            _autoAttackBlinkTriggers = 0;
            _autoAttackBlinkActive = false;
        }

        _autoAttackBlinkTriggers++;
        if (_autoAttackBlinkTriggers < 2)
            return;

        _autoAttackBlinkActive = true;
        _autoAttackBlinkStartedAt = Time.unscaledTime;
        _appliedBlinkAmount = -1f;
    }

    private float AutoAttackBlinkAmount()
    {
        if (!TryGetMeleeCharge(out _, out _, out var statePtr))
        {
            _autoAttackBlinkActive = false;
            _autoAttackBlinkStartedAt = -1f;
            _autoAttackBlinkChargePtr = IntPtr.Zero;
            _autoAttackBlinkTriggers = 0;
            return 0f;
        }

        if (statePtr != _autoAttackBlinkChargePtr)
        {
            _autoAttackBlinkChargePtr = statePtr;
            _autoAttackBlinkTriggers = 0;
            _autoAttackBlinkActive = false;
            return 0f;
        }

        if (!_autoAttackBlinkActive)
            return 0f;

        const float halfPulse = 0.09f;
        var elapsed = Time.unscaledTime - _autoAttackBlinkStartedAt;
        if (elapsed >= halfPulse)
            return 1f;

        return Mathf.Clamp01(elapsed / halfPulse);
    }

    /// <summary>
    /// Called at the end of <see cref="FPS_RecoilSystem.FPS_Update"/> so the center shape
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
                Plugin.Instance.Log.LogWarning($"Center shape recoil follow failed: {ex.Message}");
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
        if (!Enabled.Value || !InHudContext())
            return false;

        try
        {
            return WieldedCategoryIsShown();
        }
        catch (Exception ex)
        {
            if (!_stateErrorLogged)
            {
                _stateErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Crosshair visibility check failed: {ex.Message}");
            }

            return false;
        }
    }

    private bool InHudContext()
    {
        return InLevelFps() && (!HideWhenScoped.Value || !AimingThroughScope());
    }

    private bool InLevelFps()
    {
        try
        {
            if (GameStateManager.CurrentStateName != eGameStateName.InLevel)
                return false;

            return FocusStateManager.CurrentState == eFocusState.FPS;
        }
        catch (Exception ex)
        {
            if (!_stateErrorLogged)
            {
                _stateErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Crosshair visibility check failed: {ex.Message}");
            }

            return false;
        }
    }

    private static bool WieldedCategoryIsShown()
    {
        if (!TryGetWieldedSlot(out var slot))
            return false;

        return slot switch
        {
            InventorySlot.GearStandard or InventorySlot.GearSpecial or InventorySlot.GearMelee
                => ShowWeapons.Value,
            InventorySlot.GearClass or InventorySlot.HackingTool
                => ShowTools.Value,
            InventorySlot.ResourcePack
                => ShowPacks.Value,
            InventorySlot.Consumable or InventorySlot.ConsumableHeavy or InventorySlot.InPocket
                or InventorySlot.InLevelCarry or InventorySlot.Pickup
                => ShowItems.Value,
            _ => false
        };
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
                Plugin.Instance.Log.LogWarning($"Crosshair scope check failed: {ex.Message}");
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
            var centerThickness = Mathf.Clamp(CenterThickness.Value, 0.5f, 16f);
            var outlineThickness = Mathf.Clamp(OutlineThickness.Value, 0.5f, 16f);
            var hintThickness = Mathf.Clamp(HintThickness.Value, 0.5f, 16f);
            CreateShapeSet(
                centerThickness,
                out _centerDotsSprite,
                out _centerArcsSprite,
                out _centerCircleSprite,
                out _centerLinesSprite);
            CreateShapeSet(
                outlineThickness,
                out _outlineDotsSprite,
                out _outlineArcsSprite,
                out _outlineCircleSprite,
                out _outlineLinesSprite);
            ApplyHintThickness(hintThickness);
            var sprite = _centerCircleSprite!;
            var hintSprite = SelectedHintSprite() ?? _hintCircleSprite!;

            root = new GameObject("GTFO Crosshair");
            var rect = root.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            PlaceAtCenter(rect);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            root.layer = layer;
            _rect = rect;
            _restAnchored = rect.anchoredPosition;

            _centerShape = CreateSprite(rect, sprite, "Center Shape", layer, reference);
            _outlineShape = CreateSprite(rect, sprite, "Outline Shape", layer, reference);
            _hintShape = CreateSprite(rect, hintSprite, "Hint Shape", layer, reference);
            _appliedHintSprite = hintSprite;
            if (reference != null
                && _centerShape.m_sprite != null
                && _outlineShape.m_sprite != null
                && _hintShape.m_sprite != null)
            {
                _outlineShape.m_sprite.sortingOrder = reference.sortingOrder + 10;
                _centerShape.m_sprite.sortingOrder = reference.sortingOrder + 11;
                _hintShape.m_sprite.sortingOrder = reference.sortingOrder + 12;
            }

            _root = root;
            _parentPtr = parent.Pointer;
            _appliedCenterColorHex = null;
            _appliedCenterSize = -1;
            _appliedCenterOpacity = -1f;
            _appliedOutlineColorHex = null;
            _appliedOutlineSize = -1;
            _appliedOutlineOpacity = -1f;
            _appliedOutlineSpacing = -1f;
            _appliedCenterThickness = centerThickness;
            _appliedOutlineThickness = outlineThickness;
            _appliedCenterShape = null;
            _appliedOutlineShape = null;
            _crosshairShapesActive = true;
            ApplyStyle();
            HideHint();
            _root.SetActive(false);
            Plugin.Instance.Log.LogInfo($"Crosshair attached to HUD group '{parent.name}'.");
        }
        catch (Exception ex)
        {
            _root = null;
            _rect = null;
            _outlineShape = null;
            _centerShape = null;
            _hintShape = null;
            _centerDotsSprite = null;
            _centerArcsSprite = null;
            _centerLinesSprite = null;
            _centerCircleSprite = null;
            _outlineDotsSprite = null;
            _outlineArcsSprite = null;
            _outlineLinesSprite = null;
            _outlineCircleSprite = null;
            _hintDotsSprite = null;
            _hintArcsSprite = null;
            _hintLinesSprite = null;
            _hintCircleSprite = null;
            _appliedHintSprite = null;
            _parentPtr = IntPtr.Zero;
            if (root != null)
                Destroy(root);

            Plugin.Instance.Log.LogWarning($"Crosshair creation failed: {ex.Message}");
        }
    }

    private void DestroyRoot()
    {
        if (_root != null && _root.Pointer != IntPtr.Zero)
            Destroy(_root);

        _root = null;
        _rect = null;
        _restAnchored = Vector2.zero;
        _outlineShape = null;
        _centerShape = null;
        _hintShape = null;
        _centerDotsSprite = null;
        _centerArcsSprite = null;
        _centerLinesSprite = null;
        _centerCircleSprite = null;
        _outlineDotsSprite = null;
        _outlineArcsSprite = null;
        _outlineLinesSprite = null;
        _outlineCircleSprite = null;
        _hintDotsSprite = null;
        _hintArcsSprite = null;
        _hintLinesSprite = null;
        _hintCircleSprite = null;
        _parentPtr = IntPtr.Zero;
        _appliedCenterColorHex = null;
        _appliedCenterSize = -1;
        _appliedCenterOpacity = -1f;
        _appliedOutlineColorHex = null;
        _appliedOutlineSize = -1;
        _appliedOutlineOpacity = -1f;
        _appliedOutlineSpacing = -1f;
        _appliedCenterThickness = -1f;
        _appliedOutlineThickness = -1f;
        _appliedHintThickness = -1f;
        _appliedHintSprite = null;
        _appliedCenterShape = null;
        _appliedOutlineShape = null;
        _hintChargeActive = false;
        _chargeStatePtr = IntPtr.Zero;
        _crosshairShapesActive = false;
        _autoAttackBlinkActive = false;
        _autoAttackBlinkStartedAt = -1f;
        _appliedBlinkAmount = -1f;
        _appliedCenterClearance = false;
        _autoAttackBlinkChargePtr = IntPtr.Zero;
        _autoAttackBlinkTriggers = 0;
        _packHints.Detach();
        _itemHints.Detach();
        _toolHints.Detach();
        _scopeAmmo.Detach();
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

    private void SetCrosshairShapesActive(bool active)
    {
        if (_crosshairShapesActive == active)
            return;

        if (_centerShape != null)
            _centerShape.gameObject.SetActive(active);
        if (_outlineShape != null)
            _outlineShape.gameObject.SetActive(active);
        if (_hintShape != null)
            _hintShape.gameObject.SetActive(active);

        _crosshairShapesActive = active;
    }

    private void InvalidateAppliedStyle()
    {
        _appliedCenterColorHex = null;
        _appliedCenterSize = -1;
        _appliedCenterOpacity = -1f;
        _appliedOutlineColorHex = null;
        _appliedOutlineSize = -1;
        _appliedOutlineOpacity = -1f;
        _appliedOutlineSpacing = -1f;
        _appliedCenterShape = null;
        _appliedOutlineShape = null;
        _appliedBlinkAmount = -1f;
        _appliedCenterClearance = false;
    }

    /// <summary>
    /// Keep the shape on the visor center, stored as a local offset of the moving group.
    /// Later sway of that group carries the shape with it.
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
            Plugin.Instance.Log.LogWarning($"Crosshair HUD pass refresh failed: {ex.Message}");
        }
    }

    private static void CreateShapeSet(
        float thickness,
        out Sprite? dots,
        out Sprite? arcs,
        out Sprite? circle,
        out Sprite? lines)
    {
        dots = ShapeSprite(ReticleShape.TwoDots, thickness);
        arcs = ShapeSprite(ReticleShape.Arcs, thickness);
        circle = ShapeSprite(ReticleShape.Circle, thickness);
        lines = ShapeSprite(ReticleShape.Lines, thickness);
    }

    private static Sprite ShapeSprite(ReticleShape shape, float configuredThickness)
    {
        const int textureSize = 64;
        const float resolutionScale = textureSize / 32f;
        var thickness = Mathf.Clamp(configuredThickness, 0.5f, 16f) * resolutionScale;
        var dotRadius = Mathf.Clamp(thickness * 1.33f, resolutionScale, 7f * resolutionScale);
        var tex = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
        var center = (textureSize - 1) * 0.5f;
        var radius = textureSize * 0.5f - resolutionScale;
        var dotOffset = radius - dotRadius;

        for (var y = 0; y < textureSize; y++)
        {
            for (var x = 0; x < textureSize; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = Mathf.Sqrt(dx * dx + dy * dy);
                var distanceInsideEdge = radius - distance;
                var outerAlpha = Mathf.Clamp01(distanceInsideEdge + 0.5f);
                var innerAlpha = Mathf.Clamp01(thickness - distanceInsideEdge);
                var ringAlpha = outerAlpha * innerAlpha;
                float alpha;

                switch (shape)
                {
                    case ReticleShape.TwoDots:
                        var leftDistance = Mathf.Sqrt((dx + dotOffset) * (dx + dotOffset) + dy * dy);
                        var rightDistance = Mathf.Sqrt((dx - dotOffset) * (dx - dotOffset) + dy * dy);
                        alpha = Mathf.Clamp01(dotRadius + 0.5f - Mathf.Min(leftDistance, rightDistance));
                        break;
                    case ReticleShape.Arcs:
                        var thicknessProgress = Mathf.Clamp01(
                            (thickness / resolutionScale - 1.25f) / 3.25f);
                        var arcCutoff = Mathf.Lerp(0.68f, 0.52f, thicknessProgress);
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
            textureSize,
            0,
            SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private void ApplyStyle()
    {
        if (_centerShape == null || _outlineShape == null)
            return;

        var hex = CenterColorHex.Value;
        var size = Mathf.Clamp(CenterSize.Value, 1, 32);
        var opacity = Mathf.Clamp01(CenterOpacity.Value);
        var shape = ParseShape(CenterShape.Value);
        var centerThickness = Mathf.Clamp(CenterThickness.Value, 0.5f, 16f);
        var outlineHex = OutlineColorHex.Value;
        var outlineSize = Mathf.Clamp(OutlineSize.Value, 1, 64);
        var outlineOpacity = Mathf.Clamp01(OutlineOpacity.Value);
        var outlineSpacing = Mathf.Clamp(OutlineSpacing.Value, 0f, 32f);
        var outlineShape = ParseShape(OutlineShape.Value);
        var outlineThickness = Mathf.Clamp(OutlineThickness.Value, 0.5f, 16f);
        var centerClearance = HoldingPackOrItem();
        var blinkAmount = AutoAttackBlinkAmount();
        var thicknessIncrease = Mathf.Clamp(AutoAttackBlinkThicknessIncrease.Value, 0f, 200f) * 0.01f;
        centerThickness = NormalizedShapeThickness(centerThickness * (1f + thicknessIncrease * blinkAmount));
        outlineThickness = NormalizedShapeThickness(outlineThickness * (1f + thicknessIncrease * blinkAmount));
        var spritesChanged = RefreshCrosshairShapeSprites(centerThickness, outlineThickness);
        if (!spritesChanged
            && hex == _appliedCenterColorHex
            && size == _appliedCenterSize
            && Mathf.Approximately(opacity, _appliedCenterOpacity)
            && shape == _appliedCenterShape
            && outlineHex == _appliedOutlineColorHex
            && outlineSize == _appliedOutlineSize
            && Mathf.Approximately(outlineOpacity, _appliedOutlineOpacity)
            && Mathf.Approximately(outlineSpacing, _appliedOutlineSpacing)
            && outlineShape == _appliedOutlineShape
            && Mathf.Approximately(blinkAmount, _appliedBlinkAmount)
            && centerClearance == _appliedCenterClearance)
            return;

        _appliedCenterColorHex = hex;
        _appliedCenterSize = size;
        _appliedCenterOpacity = opacity;
        _appliedCenterShape = shape;
        _appliedOutlineColorHex = outlineHex;
        _appliedOutlineSize = outlineSize;
        _appliedOutlineOpacity = outlineOpacity;
        _appliedOutlineSpacing = outlineSpacing;
        _appliedOutlineShape = outlineShape;
        _appliedBlinkAmount = blinkAmount;
        _appliedCenterClearance = centerClearance;

        var color = ParseColor(hex);
        color.a *= opacity;
        var outline = ParseColor(outlineHex, new Color(1f, 0.2f, 0.333f, 1f));
        outline.a *= outlineOpacity;
        if (blinkAmount > 0f)
        {
            var blink = ParseColor(AutoAttackBlinkColorHex.Value, new Color(1f, 0.165f, 0.165f, 1f));
            blink.a = outline.a;
            outline = Color.Lerp(outline, blink, blinkAmount);
        }
        var visibleCenterSize = VisibleCenterSize(size, centerClearance);
        var visibleOutlineSize = VisibleOutlineSize(
            outlineSize,
            visibleCenterSize,
            outlineSpacing);
        SetRendererSprite(_outlineShape, SelectedOutlineSprite(outlineShape));
        SetRendererSprite(_centerShape, SelectedCenterSprite(shape));
        SetSprite(_outlineShape, outline, visibleOutlineSize);
        SetSprite(_centerShape, color, visibleCenterSize);
    }

    private static float VisibleCenterSize(float configuredSize, bool centerClearance)
    {
        var size = Mathf.Max(configuredSize, MinimumCenterShapeSize);
        if (centerClearance)
            size += HeldItemCenterClearance;
        return size;
    }

    private static bool HoldingPackOrItem()
    {
        if (!TryGetWieldedSlot(out var slot))
            return false;

        return slot is InventorySlot.ResourcePack
            or InventorySlot.Consumable
            or InventorySlot.ConsumableHeavy
            or InventorySlot.InPocket
            or InventorySlot.InLevelCarry
            or InventorySlot.Pickup;
    }

    private static bool TryGetWieldedSlot(out InventorySlot slot)
    {
        slot = InventorySlot.None;
        if (!PlayerManager.TryGetLocalPlayerAgent(out var player) || !Alive(player))
            return false;

        var holder = player!.FPItemHolder;
        if (!Alive(holder))
            return false;

        var item = holder!.WieldedItem;
        if (!Alive(item) || !Alive(item!.ItemDataBlock))
            return false;

        slot = item.ItemDataBlock!.inventorySlot;
        return true;
    }

    private static float VisibleOutlineSize(
        float configuredSize,
        float visibleCenterSize,
        float spacing)
    {
        var readableSize = Mathf.Max(configuredSize, MinimumOutlineShapeSize);
        return Mathf.Max(readableSize, visibleCenterSize + spacing * 2f);
    }

    private bool RefreshCrosshairShapeSprites(float centerThickness, float outlineThickness)
    {
        var changed = false;
        if (!Mathf.Approximately(centerThickness, _appliedCenterThickness))
        {
            CreateShapeSet(
                centerThickness,
                out _centerDotsSprite,
                out _centerArcsSprite,
                out _centerCircleSprite,
                out _centerLinesSprite);
            _appliedCenterThickness = centerThickness;
            changed = true;
        }

        if (!Mathf.Approximately(outlineThickness, _appliedOutlineThickness))
        {
            CreateShapeSet(
                outlineThickness,
                out _outlineDotsSprite,
                out _outlineArcsSprite,
                out _outlineCircleSprite,
                out _outlineLinesSprite);
            _appliedOutlineThickness = outlineThickness;
            changed = true;
        }

        return changed;
    }

    private void ApplyHintThickness(float thickness)
    {
        thickness = NormalizedShapeThickness(thickness);
        if (Mathf.Approximately(thickness, _appliedHintThickness))
            return;

        var cacheKey = Mathf.RoundToInt(thickness * 4f);
        if (_hintShapeCache.TryGetValue(cacheKey, out var sprites))
        {
            _hintDotsSprite = sprites[0];
            _hintArcsSprite = sprites[1];
            _hintCircleSprite = sprites[2];
            _hintLinesSprite = sprites[3];
        }
        else
        {
            CreateShapeSet(
                thickness,
                out _hintDotsSprite,
                out _hintArcsSprite,
                out _hintCircleSprite,
                out _hintLinesSprite);
            _hintShapeCache[cacheKey] =
                new[] { _hintDotsSprite, _hintArcsSprite, _hintCircleSprite, _hintLinesSprite };
        }

        _appliedHintThickness = thickness;
    }

    private static float NormalizedShapeThickness(float thickness)
    {
        return Mathf.Round(Mathf.Clamp(thickness, 0.5f, 16f) * 4f) * 0.25f;
    }

    /// <summary>
    /// Shape thickness is encoded into a 64-pixel texture at 2 pixels per configured unit.
    /// Growing the diameter by the corresponding inward stroke increase keeps Hint's inner
    /// edge at approximately the same distance from Outline.
    /// </summary>
    private static float HintDiameterWithThicknessSpacing(
        float diameter,
        float baseThickness,
        float currentThickness)
    {
        var thicknessIncrease = Mathf.Max(
            0f,
            NormalizedShapeThickness(currentThickness) - NormalizedShapeThickness(baseThickness));
        return diameter * (1f + thicknessIncrease / 16f);
    }

    private Sprite? SelectedCenterSprite(ReticleShape shape)
    {
        return shape switch
        {
            ReticleShape.TwoDots => _centerDotsSprite,
            ReticleShape.Arcs => _centerArcsSprite,
            ReticleShape.Lines => _centerLinesSprite,
            _ => _centerCircleSprite
        };
    }

    private Sprite? SelectedOutlineSprite(ReticleShape shape)
    {
        return shape switch
        {
            ReticleShape.TwoDots => _outlineDotsSprite,
            ReticleShape.Arcs => _outlineArcsSprite,
            ReticleShape.Lines => _outlineLinesSprite,
            _ => _outlineCircleSprite
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
    private void ApplyHint()
    {
        if (!HintEnabled.Value)
        {
            if (_hintChargeActive)
                EndHintCharge();
            else
                HideHint();
            return;
        }

        if (!TryGetMeleeCharge(out var progress, out var autoStrikeWarningProgress, out var statePtr))
        {
            if (_hintChargeActive)
                EndHintCharge();

            if (HintShowAlways.Value)
                ShowHintAtOriginalSize();
            else
                HideHint();
            return;
        }

        var chargeJustStarted = !_hintChargeActive || statePtr != _chargeStatePtr;
        if (chargeJustStarted)
        {
            _hintChargeActive = true;
            _chargeStatePtr = statePtr;
        }

        var crosshairDiameter = CurrentCrosshairDiameter();
        var targetSize = HintOriginalSize(crosshairDiameter);
        var readySize = crosshairDiameter + HintReadyPadding;
        var diameter = chargeJustStarted
            ? targetSize
            : Mathf.Lerp(targetSize, readySize, progress);
        var color = ConfiguredHintColor();
        var easedWarning = autoStrikeWarningProgress * autoStrikeWarningProgress;
        var baseThickness = Mathf.Clamp(HintThickness.Value, 0.5f, 16f);
        var thicknessIncrease = Mathf.Clamp(HintAutoAttackThicknessIncrease.Value, 0f, 200f) * 0.01f;
        ApplyHintThickness(baseThickness * (1f + thicknessIncrease * easedWarning));
        diameter = HintDiameterWithThicknessSpacing(
            diameter,
            baseThickness,
            _appliedHintThickness);
        ApplyHintSprite();
        if (autoStrikeWarningProgress > 0f)
        {
            var autoAttackColor = ParseColor(HintAutoAttackColorHex.Value, Color.white);
            autoAttackColor.a *= Mathf.Clamp01(HintOpacity.Value);
            color = Color.Lerp(color, autoAttackColor, easedWarning);
        }
        SetSprite(_hintShape!, color, diameter);
    }

    private void ShowHintAtOriginalSize()
    {
        ApplyHintThickness(Mathf.Clamp(HintThickness.Value, 0.5f, 16f));
        ApplyHintSprite();
        SetSprite(_hintShape!, ConfiguredHintColor(), HintOriginalSize(CurrentCrosshairDiameter()));
    }

    private static Color ConfiguredHintColor()
    {
        var color = ParseColor(HintColorHex.Value, new Color(1f, 0.05f, 0.08f, 1f));
        color.a *= Mathf.Clamp01(HintOpacity.Value);
        return color;
    }

    private float CurrentCrosshairDiameter()
    {
        var visibleCenterSize = VisibleCenterSize(
            Mathf.Clamp(CenterSize.Value, 1, 32),
            HoldingPackOrItem());
        var visibleOutlineSize = VisibleOutlineSize(
            Mathf.Clamp(OutlineSize.Value, 1, 64),
            visibleCenterSize,
            Mathf.Clamp(OutlineSpacing.Value, 0f, 32f));
        return Mathf.Max(visibleOutlineSize, visibleCenterSize);
    }

    private static float HintOriginalSize(float crosshairDiameter)
    {
        return Mathf.Max(
            Mathf.Clamp(HintSize.Value, 8, 64),
            crosshairDiameter + HintReadyPadding);
    }

    /// <summary>
    /// The visor blit keeps the sprite mesh from the last HUD gather. The configured
    /// shape has to be on the renderer before that gather, and a later swap has to ask for another one.
    /// </summary>
    private void ApplyHintSprite()
    {
        var sprite = SelectedHintSprite();
        var renderer = _hintShape?.m_sprite;
        if (renderer == null || sprite == null || ReferenceEquals(sprite, _appliedHintSprite))
            return;

        renderer.sprite = sprite;
        _appliedHintSprite = sprite;
        RefreshHudPass();
    }

    private Sprite? SelectedHintSprite()
    {
        return ParseShape(HintShape.Value) switch
        {
            ReticleShape.TwoDots => _hintDotsSprite,
            ReticleShape.Arcs => _hintArcsSprite,
            ReticleShape.Lines => _hintLinesSprite,
            _ => _hintCircleSprite
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
            _ => ReticleShape.Circle
        };
    }

    private void EndHintCharge()
    {
        _hintChargeActive = false;
        _chargeStatePtr = IntPtr.Zero;
        HideHint();
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
                Plugin.Instance.Log.LogWarning($"Hint melee charge update failed: {ex.Message}");
            }

            return false;
        }
    }

    private void HideHint()
    {
        var renderer = _hintShape?.m_sprite;
        if (renderer != null)
            renderer.color = new Color(1f, 0.05f, 0.08f, 0f);
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
