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
    Dots,
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
/// Up to five small circles sit to the right of Outline. A magazine under five rounds
/// shows one circle per round, drawn a little larger. Spent circles stay visible at a
/// lower opacity and fill back in from the bottom over a reload.
/// The stack is hidden on a full magazine and fades toward those opacities as rounds are spent.
///
/// A melee charge draws a configurable shape, then shrinks it
/// to the crosshair's outer edge as <see cref="MWS_ChargeUp.m_elapsed"/> reaches full damage.
///
/// Shown only when all of these are true:
/// - <see cref="GameStateManager.CurrentStateName"/> is <see cref="eGameStateName.InLevel"/>
/// - <see cref="FocusStateManager.CurrentState"/> is <see cref="eFocusState.FPS"/>
/// - the player is not aiming through a magnified scope
///
/// While running, jumping, falling, reloading, downed, or out of ammo, Center and Outline
/// draw at half of their configured opacity. Center becomes white and Outline becomes pale green.
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
    public static ConfigEntry<bool> MeleeHintEnabled { get; private set; } = null!;
    public static ConfigEntry<bool> MeleeHintShowAlways { get; private set; } = null!;
    public static ConfigEntry<string> MeleeHintShape { get; private set; } = null!;
    public static ConfigEntry<string> MeleeHintColorHex { get; private set; } = null!;
    public static ConfigEntry<string> MeleeHintAutoAttackColorHex { get; private set; } = null!;
    public static ConfigEntry<float> MeleeHintAutoAttackThicknessIncrease { get; private set; } = null!;
    public static ConfigEntry<int> MeleeHintSize { get; private set; } = null!;
    public static ConfigEntry<float> MeleeHintThickness { get; private set; } = null!;
    public static ConfigEntry<float> MeleeHintOpacity { get; private set; } = null!;
    public static ConfigEntry<bool> MagCounterEnabled { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterSize { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterOpacity { get; private set; } = null!;
    public static ConfigEntry<float> MagCounterSpentOpacity { get; private set; } = null!;

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
    private const int MagPipCount = 5;
    private const float MagPipGapRatio = 1.2f / 3.2f;
    private const float MagPipMargin = 4f;
    private const float MagPipSmallMagScaleStep = 0.12f;
    private const float MagPipThickness = 16f;
    private const string BlockedCenterColorHex = "ffffff";
    private const string BlockedOutlineColorHex = "5eb18e";
    private const float BlockedOpacityScale = 0.5f;

    internal static CrosshairOverlay? Current { get; private set; }

    private GameObject? _root;
    private RectTransform? _rect;
    private Vector2 _restAnchored;
    private IntPtr _parentPtr;
    private UI_Sprite? _centerShape;
    private UI_Sprite? _outlineShape;
    private UI_Sprite? _hintShape;
    private readonly UI_Sprite?[] _magPips = new UI_Sprite?[MagPipCount];
    private Sprite? _magPipSprite;
    private float _visibleOutlineSize;
    private float _magDisplayed = -1f;
    private bool _magReloadActive;
    private float _magReloadDuration;
    private IntPtr _magItemPtr;
    private float _appliedMagDisplayed = -1f;
    private float _appliedMagAnchorX = float.NaN;
    private int _appliedMagPipCount = -1;
    private float _appliedMagPipSize = float.NaN;
    private float _appliedMagOpacity = -1f;
    private float _appliedMagSpentOpacity = -1f;
    private float _appliedMagFade = -1f;
    private bool _magFadeHidden;
    private Color _appliedMagColor;
    private readonly PackHintOverlay _packHints = new();
    private readonly ItemHintOverlay _itemHints = new();
    private readonly ToolHintOverlay _toolHints = new();
    private readonly ScopeAmmoOverlay _scopeAmmo = new();
    private readonly MagHintOverlay _magHint = new();
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
    private readonly Dictionary<int, Sprite?[]> _shapeCache = new();
    private readonly List<float> _prewarmThicknesses = new();
    private int _prewarmIndex;
    private static Color32[]? _pixels;
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
    private float _hintLayoutDiameter;
    private IntPtr _chargeStatePtr;
    private bool _chargeErrorLogged;
    private bool _crosshairShapesActive;
    private bool _autoAttackBlinkActive;
    private float _autoAttackBlinkStartedAt = -1f;
    private float _appliedBlinkAmount = -1f;
    private bool _appliedCenterClearance;
    private bool _appliedUsageBlocked;
    private bool _usageBlockErrorLogged;
    private IntPtr _autoAttackBlinkChargePtr;
    private int _autoAttackBlinkTriggers;
    private bool _charging;
    private float _chargeProgress;
    private float _chargeWarning;
    private IntPtr _frameChargePtr;
    private int _recoilIntegratedFrame = -1;
    private Vector2 _appliedAnchored;
    private bool _hasAppliedAnchored;
    private bool _hintVisible = true;
    private int _steadyHintKey = int.MinValue;
    private string? _steadyHintHex;
    private float _steadyHintOpacity = -1f;
    private FPS_RecoilSystem? _recoil;
    private IntPtr _recoilPlayer;
    private Vector2 _lastRecoilView;
    private Vector2 _lastRecoilDelta;
    private bool _hasRecoilView;

    public CrosshairOverlay(IntPtr ptr) : base(ptr)
    {
    }

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "1 - General",
            "Crosshair enabled",
            true,
            "Crosshair enabled.");

        ShowWeapons = config.Bind(
            "Show for:",
            "Weapons",
            true,
            "Show the crosshair while holding a primary weapon, special weapon, or melee weapon.");

        ShowTools = config.Bind(
            "Show for:",
            "Tools",
            false,
            "Show the crosshair while holding any tool.");

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
            "Hide the crosshair while aiming through a weapon scope.");

        FollowRecoil = config.Bind(
            "2 - Center",
            "Follow recoil",
            true,
            "Move the crosshair center with weapon recoil that does not follow the camera.");

        CenterShape = config.Bind(
            "2 - Center",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Center shape.",
                new AcceptableValueList<string>("Dots", "Arcs", "Circle", "Lines")));

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
            2.8f,
            new ConfigDescription(
                "Center shape thickness. For Dots, this controls dot size.",
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
            "Color Outline blinks just before an automatic melee attack, as RRGGBB or RRGGBBAA.");

        AutoAttackBlinkThicknessIncrease = config.Bind(
            "2 - Center",
            "Auto attack blink thickness increase",
            50f,
            new ConfigDescription(
                "Percentage by which Center and Outline thickness grows during the auto-attack blink.",
                new AcceptableValueRange<float>(0f, 200f)));

        OutlineShape = config.Bind(
            "3 - Outline",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Outline shape.",
                new AcceptableValueList<string>("Dots", "Arcs", "Circle", "Lines")));

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
            3.4f,
            new ConfigDescription(
                "Outline shape thickness. For Dots, this controls dot size.",
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

        MeleeHintEnabled = config.Bind(
            "4 - Hint",
            "Enabled",
            true,
            "Show the shrinking melee charge hint.");

        MeleeHintShowAlways = config.Bind(
            "4 - Hint",
            "Show always",
            false,
            "Keep the hint visible while no melee attack is charging.");

        MeleeHintShape = config.Bind(
            "4 - Hint",
            "Shape",
            "Arcs",
            new ConfigDescription(
                "Melee charge hint shape.",
                new AcceptableValueList<string>("Dots", "Arcs", "Circle", "Lines")));

        MeleeHintColorHex = config.Bind(
            "4 - Hint",
            "Color",
            "ffffff",
            "Melee charge hint color as RRGGBB or RRGGBBAA. Example: FF3355.");

        MeleeHintAutoAttackColorHex = config.Bind(
            "4 - Hint",
            "Auto attack color",
            "cb2769",
            "Hint color as an automatic melee attack approaches, as RRGGBB or RRGGBBAA.");

        MeleeHintAutoAttackThicknessIncrease = config.Bind(
            "4 - Hint",
            "Auto attack thickness increase",
            110f,
            new ConfigDescription(
                "Percentage by which hint thickness grows as an automatic melee attack approaches.",
                new AcceptableValueRange<float>(0f, 200f)));

        MeleeHintSize = config.Bind(
            "4 - Hint",
            "Size",
            78,
            new ConfigDescription(
                "Starting diameter of the melee charge hint, in HUD units.",
                new AcceptableValueRange<int>(8, 64)));

        MeleeHintThickness = config.Bind(
            "4 - Hint",
            "Thickness",
            1.25f,
            new ConfigDescription(
                "Hint shape thickness. For Dots, this controls dot size.",
                new AcceptableValueRange<float>(0.5f, 16f)));

        MeleeHintOpacity = config.Bind(
            "4 - Hint",
            "Opacity",
            0.8f,
            new ConfigDescription(
                "Hint opacity. 0 is invisible, 1 is solid.",
                new AcceptableValueRange<float>(0f, 1f)));

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

    private void Update()
    {
        AdvancePrewarm();

        if (!InFpsFocus())
        {
            HideRoot();
            return;
        }

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

        var hasState = TryGetGameState(out var state);
        var inLevel = hasState && state == eGameStateName.InLevel;
        var scoped = inLevel && AimingThroughScope();
        var inHudContext = inLevel && (!HideWhenScoped.Value || !scoped);
        var showCrosshair = false;
        if (inHudContext && Enabled.Value)
        {
            try
            {
                showCrosshair = WieldedCategoryIsShown();
            }
            catch (Exception ex)
            {
                if (!_stateErrorLogged)
                {
                    _stateErrorLogged = true;
                    Plugin.Instance.Log.LogWarning($"Crosshair visibility check failed: {ex.Message}");
                }
            }
        }

        var hintParent = _rect;
        var hintLayer = _rect != null ? _rect.gameObject.layer : 0;
        var showPackHint = _packHints.Update(hintParent, hintLayer, inHudContext);
        var showItemHint = _itemHints.Update(hintParent, hintLayer, inHudContext);
        var showToolHint = _toolHints.Update(hintParent, hintLayer, inHudContext);
        var showScopeAmmo = _scopeAmmo.Update(hintParent, hintLayer, scoped);
        if (showCrosshair)
        {
            _charging = ReadMeleeCharge(out _chargeProgress, out _chargeWarning, out _frameChargePtr);
            if (!_crosshairShapesActive)
                InvalidateAppliedStyle();
            ApplyStyle();
        }
        else
            _charging = false;

        SetCrosshairShapesActive(showCrosshair);
        var hasMagFill = TryGetMagDisplayed(out var magDisplayed, out var magCapacity);
        ApplyMagIndicator(showCrosshair, hasMagFill, magDisplayed, magCapacity);

        if (showCrosshair)
            ApplyHint();
        else if (_hintChargeActive)
            EndHintCharge();

        var aiming = hasState && state is eGameStateName.InLevel or eGameStateName.Lobby;
        var showMagHint = _magHint.NeedsPresentation(aiming)
            && _magHint.Update(hintParent, hintLayer, HintFloor());
        var showRoot = showCrosshair || showPackHint || showItemHint || showToolHint || showScopeAmmo || showMagHint;

        if (_root!.activeSelf != showRoot)
        {
            _root.SetActive(showRoot);
            RefreshHudPass();
        }
    }

    private void OnEnable()
    {
        Current = this;
    }

    /// <summary>
    /// Used when the recoil system did not integrate this frame.
    /// <see cref="ApplyRecoilAfterIntegration"/> already placed the crosshair on the usual path.
    /// </summary>
    private void LateUpdate()
    {
        if (_recoilIntegratedFrame == Time.frameCount)
            return;

        if (RootActive())
            ApplyRecoil();
    }

    /// <summary>
    /// Starts the same-time blink the vanilla reticle uses before a charged auto-attack.
    /// </summary>
    internal void StartAutoAttackBlink()
    {
        ReadMeleeCharge(out _, out _, out var statePtr);
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
        if (!_charging)
        {
            if (_autoAttackBlinkActive || _autoAttackBlinkTriggers != 0 || _autoAttackBlinkChargePtr != IntPtr.Zero)
            {
                _autoAttackBlinkActive = false;
                _autoAttackBlinkStartedAt = -1f;
                _autoAttackBlinkChargePtr = IntPtr.Zero;
                _autoAttackBlinkTriggers = 0;
            }

            return 0f;
        }

        if (_frameChargePtr != _autoAttackBlinkChargePtr)
        {
            _autoAttackBlinkChargePtr = _frameChargePtr;
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
    /// uses the recoil point from this frame. LateUpdate does not repeat that write.
    /// </summary>
    internal void ApplyRecoilAfterIntegration()
    {
        if (!RootActive())
            return;

        _recoilIntegratedFrame = Time.frameCount;
        ApplyRecoil();
    }

    private void ApplyRecoil()
    {
        if (!Alive(_rect))
            return;

        if (!FollowRecoil.Value || !TryGetRecoilCanvasDelta(out var canvasDelta))
        {
            SetAnchored(_restAnchored);
            return;
        }

        var canvas = CellCanvasRect();
        var parent = _rect!.parent;
        if (!Alive(canvas) || !Alive(parent))
        {
            SetAnchored(_restAnchored);
            return;
        }

        var worldDelta = canvas!.TransformVector(new Vector3(canvasDelta.x, canvasDelta.y, 0f));
        var localDelta = parent!.InverseTransformVector(worldDelta);
        SetAnchored(_restAnchored + new Vector2(localDelta.x, localDelta.y));
    }

    private void SetAnchored(Vector2 position)
    {
        if (_hasAppliedAnchored && (position - _appliedAnchored).sqrMagnitude < 0.0004f)
            return;

        _rect!.anchoredPosition = position;
        _appliedAnchored = position;
        _hasAppliedAnchored = true;
    }

    private bool RootActive()
    {
        return _root != null && _root.activeSelf;
    }

    private void HideRoot()
    {
        if (!RootActive())
            return;

        _root!.SetActive(false);
        RefreshHudPass();
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
            var recoil = RecoilSystem();
            if (!Alive(recoil))
                return false;

            var view = recoil!.CurrentVSPos;
            if (view.x <= 0.001f && view.y <= 0.001f)
                return false;

            var centered = view - new Vector2(0.5f, 0.5f);
            centered.y *= RecoilVerticalScale;
            if (centered.sqrMagnitude < 0.0000001f)
                return false;

            if (WieldedFrame.IsShotgun())
                return false;

            if (_hasRecoilView && (view - _lastRecoilView).sqrMagnitude < 0.00000001f)
            {
                canvasDelta = _lastRecoilDelta;
                return true;
            }

            var canvas = CellCanvasRect();
            if (!Alive(canvas))
                return false;

            var size = canvas!.rect.size;
            canvasDelta = new Vector2(centered.x * size.x, centered.y * size.y);
            _lastRecoilView = view;
            _lastRecoilDelta = canvasDelta;
            _hasRecoilView = true;
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

    private FPS_RecoilSystem? RecoilSystem()
    {
        if (!WieldedFrame.TryGetPlayer(out var player) || !Alive(player))
            return null;

        if (_recoilPlayer == player!.Pointer && Alive(_recoil))
            return _recoil;

        _recoilPlayer = player.Pointer;
        _hasRecoilView = false;
        var camera = player.FPSCamera;
        _recoil = Alive(camera) ? camera!.RecoilSystem : null;
        return Alive(_recoil) ? _recoil : null;
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

    private bool InFpsFocus()
    {
        try
        {
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

    /// <summary>
    /// The shooting range stays in the lobby state while the player is aiming.
    /// </summary>
    private bool TryGetGameState(out eGameStateName state)
    {
        try
        {
            state = GameStateManager.CurrentStateName;
            return true;
        }
        catch (Exception ex)
        {
            state = default;
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
            if (!WieldedFrame.TryGetPlayer(out var player) || !Alive(player))
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

            if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
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
            var hintThickness = Mathf.Clamp(MeleeHintThickness.Value, 0.5f, 16f);
            LoadShapeSet(
                centerThickness,
                out _centerDotsSprite,
                out _centerArcsSprite,
                out _centerCircleSprite,
                out _centerLinesSprite);
            LoadShapeSet(
                outlineThickness,
                out _outlineDotsSprite,
                out _outlineArcsSprite,
                out _outlineCircleSprite,
                out _outlineLinesSprite);
            ApplyHintThickness(hintThickness);
            LoadShapeSet(MagPipThickness, out _, out _, out _magPipSprite, out _);
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
            _appliedAnchored = _restAnchored;
            _hasAppliedAnchored = true;

            _centerShape = CreateSprite(rect, sprite, "Center Shape", layer, reference);
            _outlineShape = CreateSprite(rect, sprite, "Outline Shape", layer, reference);
            _hintShape = CreateSprite(rect, hintSprite, "Hint Shape", layer, reference);
            if (_magPipSprite != null)
            {
                for (var i = 0; i < MagPipCount; i++)
                {
                    var pip = CreateSprite(rect, _magPipSprite, "Mag Pip " + (i + 1), layer, reference);
                    pip.gameObject.SetActive(false);
                    _magPips[i] = pip;
                }
            }

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
            SchedulePrewarm();
            Plugin.Instance.Log.LogInfo($"Crosshair attached to HUD group '{parent.name}'.");
        }
        catch (Exception ex)
        {
            _root = null;
            _rect = null;
            _outlineShape = null;
            _centerShape = null;
            _hintShape = null;
            ClearMagPips();
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

            DestroyShapeCache();
            Plugin.Instance.Log.LogWarning($"Crosshair creation failed: {ex.Message}");
        }
    }

    private void DestroyRoot()
    {
        if (_root != null && _root.Pointer != IntPtr.Zero)
            Destroy(_root);

        DestroyShapeCache();
        _prewarmThicknesses.Clear();
        _prewarmIndex = 0;

        _root = null;
        _rect = null;
        _restAnchored = Vector2.zero;
        _outlineShape = null;
        _centerShape = null;
        _hintShape = null;
        ClearMagPips();
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
        _appliedUsageBlocked = false;
        _autoAttackBlinkChargePtr = IntPtr.Zero;
        _autoAttackBlinkTriggers = 0;
        _hintLayoutDiameter = 0f;
        _hasAppliedAnchored = false;
        _appliedAnchored = Vector2.zero;
        _hintVisible = true;
        _steadyHintKey = int.MinValue;
        _steadyHintHex = null;
        _steadyHintOpacity = -1f;
        _charging = false;
        _frameChargePtr = IntPtr.Zero;
        _recoil = null;
        _recoilPlayer = IntPtr.Zero;
        _hasRecoilView = false;
        _packHints.Detach();
        _itemHints.Detach();
        _toolHints.Detach();
        _scopeAmmo.Detach();
        _magHint.Detach();
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
        _appliedUsageBlocked = false;
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

    private void LoadShapeSet(
        float thickness,
        out Sprite? dots,
        out Sprite? arcs,
        out Sprite? circle,
        out Sprite? lines)
    {
        thickness = NormalizedShapeThickness(thickness);
        var key = Mathf.RoundToInt(thickness * 4f);
        if (!_shapeCache.TryGetValue(key, out var sprites) || sprites == null)
        {
            CreateShapeSet(thickness, out dots, out arcs, out circle, out lines);
            _shapeCache[key] = new[] { dots, arcs, circle, lines };
            return;
        }

        dots = sprites[0];
        arcs = sprites[1];
        circle = sprites[2];
        lines = sprites[3];
    }

    /// <summary>
    /// Builds the thicknesses a blink or melee warning will ask for, one set per later update,
    /// so the first charge does not upload those textures in the same frames as the animation.
    /// </summary>
    private void SchedulePrewarm()
    {
        _prewarmThicknesses.Clear();
        _prewarmIndex = 0;
        AddPrewarmRange(CenterThickness.Value, AutoAttackBlinkThicknessIncrease.Value);
        AddPrewarmRange(OutlineThickness.Value, AutoAttackBlinkThicknessIncrease.Value);
        AddPrewarmRange(MeleeHintThickness.Value, MeleeHintAutoAttackThicknessIncrease.Value);
    }

    private void AddPrewarmRange(float baseThickness, float increasePercent)
    {
        var increase = Mathf.Clamp(increasePercent, 0f, 200f) * 0.01f;
        var start = NormalizedShapeThickness(baseThickness);
        var end = NormalizedShapeThickness(baseThickness * (1f + increase));
        if (end < start)
            (start, end) = (end, start);

        var steps = Mathf.RoundToInt((end - start) * 4f);
        for (var i = 0; i <= steps; i++)
        {
            var thickness = start + i * 0.25f;
            var listed = false;
            for (var n = 0; n < _prewarmThicknesses.Count; n++)
            {
                if (Mathf.Approximately(_prewarmThicknesses[n], thickness))
                {
                    listed = true;
                    break;
                }
            }

            if (!listed)
                _prewarmThicknesses.Add(thickness);
        }
    }

    private void AdvancePrewarm()
    {
        var built = 0;
        while (_prewarmIndex < _prewarmThicknesses.Count && built < 1)
        {
            var thickness = _prewarmThicknesses[_prewarmIndex];
            _prewarmIndex++;
            var key = Mathf.RoundToInt(NormalizedShapeThickness(thickness) * 4f);
            if (_shapeCache.ContainsKey(key))
                continue;

            try
            {
                LoadShapeSet(thickness, out _, out _, out _, out _);
                built++;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogWarning($"Crosshair shape prewarm failed: {ex.Message}");
                built++;
            }
        }
    }

    private void DestroyShapeCache()
    {
        foreach (var sprites in _shapeCache.Values)
        {
            if (sprites == null)
                continue;

            for (var i = 0; i < sprites.Length; i++)
                DestroySprite(sprites[i]);
        }

        _shapeCache.Clear();
    }

    private static void DestroySprite(Sprite? sprite)
    {
        if (sprite == null)
            return;

        var texture = sprite.texture;
        UnityEngine.Object.Destroy(sprite);
        if (texture != null)
            UnityEngine.Object.Destroy(texture);
    }

    private static void CreateShapeSet(
        float thickness,
        out Sprite? dots,
        out Sprite? arcs,
        out Sprite? circle,
        out Sprite? lines)
    {
        dots = ShapeSprite(ReticleShape.Dots, thickness);
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
        var pixels = PixelBuffer();
        var center = (textureSize - 1) * 0.5f;
        var radius = textureSize * 0.5f - resolutionScale;
        var dotOffset = radius - dotRadius;

        for (var y = 0; y < textureSize; y++)
        {
            var row = y * textureSize;
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
                    case ReticleShape.Dots:
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

                pixels[row + x] = new Color32(255, 255, 255, AlphaByte(alpha));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false);
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

    private static Color32[] PixelBuffer()
    {
        const int length = 64 * 64;
        if (_pixels == null || _pixels.Length != length)
            _pixels = new Color32[length];
        return _pixels;
    }

    private static byte AlphaByte(float alpha)
    {
        var value = Mathf.RoundToInt(alpha * 255f);
        if (value < 0)
            return 0;
        if (value > 255)
            return 255;
        return (byte)value;
    }

    /// <summary>
    /// Magazine pips sit just to the right of Outline, top circle first.
    /// A clip shorter than <see cref="MagPipCount"/> draws one pip per round.
    /// Fired rounds leave their pip in place at the configured spent opacity.
    /// A reload fills the stack back toward the clip the gun already holds
    /// while the reload animation is still playing.
    /// <see cref="MagHintOverlay.OpacityFactor"/> hides a full magazine and fades the
    /// stack up to the configured opacities as rounds are spent.
    /// </summary>
    private void ApplyMagIndicator(bool showCrosshair, bool hasMag, float displayed, int capacity)
    {
        if (!showCrosshair
            || !MagCounterEnabled.Value
            || _magPipSprite == null
            || !hasMag)
        {
            _magFadeHidden = false;
            HideMagPips();
            return;
        }

        var outlineRenderer = _outlineShape?.m_sprite;
        if (outlineRenderer == null)
        {
            _magFadeHidden = false;
            HideMagPips();
            return;
        }

        var fade = MagHintOverlay.OpacityFactor(
            displayed * capacity,
            capacity,
            MagHintOverlay.FullOpacityAt.Value);
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
        var opacity = Mathf.Clamp01(MagCounterOpacity.Value);
        var spentOpacity = Mathf.Clamp01(MagCounterSpentOpacity.Value);
        var pipCount = capacity < MagPipCount ? capacity : MagPipCount;
        var pipSize = configuredSize * (pipCount < MagPipCount
            ? 1f + (MagPipCount - pipCount) * MagPipSmallMagScaleStep
            : 1f);
        var color = outlineRenderer.color;
        var anchorX = _visibleOutlineSize * 0.5f + MagPipMargin + pipSize * 0.5f;
        var layoutChanged = float.IsNaN(_appliedMagAnchorX) || !Mathf.Approximately(_appliedMagAnchorX, anchorX);
        var colorChanged = color != _appliedMagColor;
        var ratioChanged = !Mathf.Approximately(_appliedMagDisplayed, displayed);
        var countChanged = pipCount != _appliedMagPipCount;
        var sizeChanged = float.IsNaN(_appliedMagPipSize) || !Mathf.Approximately(_appliedMagPipSize, pipSize);
        var opacityChanged = !Mathf.Approximately(_appliedMagOpacity, opacity)
            || !Mathf.Approximately(_appliedMagSpentOpacity, spentOpacity);
        var fadeChanged = !Mathf.Approximately(_appliedMagFade, fade);
        if (!layoutChanged && !colorChanged && !ratioChanged && !countChanged && !sizeChanged && !opacityChanged && !fadeChanged)
            return;

        _appliedMagAnchorX = anchorX;
        _appliedMagColor = color;
        _appliedMagDisplayed = displayed;
        _appliedMagPipCount = pipCount;
        _appliedMagPipSize = pipSize;
        _appliedMagOpacity = opacity;
        _appliedMagSpentOpacity = spentOpacity;
        _appliedMagFade = fade;

        var gap = pipSize * MagPipGapRatio;
        var span = pipCount * pipSize + (pipCount - 1) * gap;
        var top = span * 0.5f - pipSize * 0.5f;
        var step = pipSize + gap;
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
            SetSprite(pip, pipColor, pipSize);
        }
    }

    private bool TryGetMagDisplayed(out float displayed, out int capacity)
    {
        displayed = 0f;
        capacity = 0;
        if (!WieldedFrame.TryGetBulletWeapon(out var gun) || !Alive(gun))
        {
            _magReloadActive = false;
            _magItemPtr = IntPtr.Zero;
            return false;
        }

        if (!WieldedFrame.TryGetItem(out var item) || !Alive(item))
        {
            _magReloadActive = false;
            _magItemPtr = IntPtr.Zero;
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
        if (ptr != _magItemPtr)
        {
            _magItemPtr = ptr;
            _magReloadActive = false;
            _magDisplayed = live;
        }

        if (item.IsReloading)
        {
            if (!_magReloadActive)
            {
                _magReloadActive = true;
                var duration = item.ReloadTime;
                _magReloadDuration = duration > 0.05f ? duration : 1.2f;
                if (_magDisplayed < 0f)
                    _magDisplayed = 0f;
            }

            var step = Time.deltaTime / Mathf.Max(_magReloadDuration, 0.05f);
            _magDisplayed = Mathf.MoveTowards(_magDisplayed, live, step);
        }
        else
        {
            _magReloadActive = false;
            _magDisplayed = live;
        }

        displayed = Mathf.Clamp01(_magDisplayed);
        capacity = max;
        return true;
    }

    private void HideMagPips()
    {
        _appliedMagDisplayed = -1f;
        _appliedMagAnchorX = float.NaN;
        _appliedMagPipCount = -1;
        _appliedMagPipSize = float.NaN;
        _appliedMagOpacity = -1f;
        _appliedMagSpentOpacity = -1f;
        _appliedMagFade = -1f;
        for (var i = 0; i < MagPipCount; i++)
        {
            var pip = _magPips[i];
            if (Alive(pip) && pip!.gameObject.activeSelf)
                pip.gameObject.SetActive(false);
        }
    }

    private void ClearMagPips()
    {
        for (var i = 0; i < MagPipCount; i++)
            _magPips[i] = null;

        _magPipSprite = null;
        _visibleOutlineSize = 0f;
        _magDisplayed = -1f;
        _magReloadActive = false;
        _magItemPtr = IntPtr.Zero;
        _appliedMagDisplayed = -1f;
        _appliedMagAnchorX = float.NaN;
        _appliedMagPipCount = -1;
        _appliedMagPipSize = float.NaN;
        _appliedMagOpacity = -1f;
        _appliedMagSpentOpacity = -1f;
        _appliedMagFade = -1f;
        _magFadeHidden = false;
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
        var usageBlocked = UsageIsBlocked();
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
            && centerClearance == _appliedCenterClearance
            && usageBlocked == _appliedUsageBlocked)
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
        _appliedUsageBlocked = usageBlocked;

        if (usageBlocked)
        {
            hex = BlockedCenterColorHex;
            outlineHex = BlockedOutlineColorHex;
            opacity *= BlockedOpacityScale;
            outlineOpacity *= BlockedOpacityScale;
        }

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
        _visibleOutlineSize = visibleOutlineSize;
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

    /// <summary>
    /// The wielded item cannot be used: running, jumping or falling, reloading, downed, or no ammo.
    /// </summary>
    private bool UsageIsBlocked()
    {
        try
        {
            if (WieldedFrame.TryGetPlayer(out var player) && Alive(player))
            {
                var locomotion = player!.Locomotion;
                if (Alive(locomotion))
                {
                    var state = locomotion!.m_currentStateEnum;
                    if (locomotion.IsRunning
                        || locomotion.m_isJumping
                        || state is PlayerLocomotion.PLOC_State.Run
                            or PlayerLocomotion.PLOC_State.Jump
                            or PlayerLocomotion.PLOC_State.Fall
                            or PlayerLocomotion.PLOC_State.Downed)
                        return true;
                }
            }

            if (WieldedFrame.TryGetItem(out var item) && Alive(item) && item!.IsReloading)
                return true;

            return _magHint.MagazineIsEmpty() || ToolHintOverlay.WieldedToolAmmoIsEmpty();
        }
        catch (Exception ex)
        {
            if (!_usageBlockErrorLogged)
            {
                _usageBlockErrorLogged = true;
                Plugin.Instance.Log.LogWarning($"Crosshair usage state check failed: {ex.Message}");
            }

            return false;
        }
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
        return WieldedFrame.TryGetSlot(out slot);
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
            LoadShapeSet(
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
            LoadShapeSet(
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
        if (Mathf.Approximately(thickness, _appliedHintThickness) && _hintDotsSprite != null)
            return;

        LoadShapeSet(
            thickness,
            out _hintDotsSprite,
            out _hintArcsSprite,
            out _hintCircleSprite,
            out _hintLinesSprite);
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
            ReticleShape.Dots => _centerDotsSprite,
            ReticleShape.Arcs => _centerArcsSprite,
            ReticleShape.Lines => _centerLinesSprite,
            _ => _centerCircleSprite
        };
    }

    private Sprite? SelectedOutlineSprite(ReticleShape shape)
    {
        return shape switch
        {
            ReticleShape.Dots => _outlineDotsSprite,
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
        if (!MeleeHintEnabled.Value)
        {
            if (_hintChargeActive)
                EndHintCharge();
            else
                HideHint();
            return;
        }

        if (!_charging)
        {
            if (_hintChargeActive)
                EndHintCharge();

            if (MeleeHintShowAlways.Value)
                ShowHintAtOriginalSize();
            else
                HideHint();
            return;
        }

        var progress = _chargeProgress;
        var autoStrikeWarningProgress = _chargeWarning;
        var statePtr = _frameChargePtr;

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
        var baseThickness = Mathf.Clamp(MeleeHintThickness.Value, 0.5f, 16f);
        var thicknessIncrease = Mathf.Clamp(MeleeHintAutoAttackThicknessIncrease.Value, 0f, 200f) * 0.01f;
        ApplyHintThickness(baseThickness * (1f + thicknessIncrease * easedWarning));
        diameter = HintDiameterWithThicknessSpacing(
            diameter,
            baseThickness,
            _appliedHintThickness);
        ApplyHintSprite();
        if (autoStrikeWarningProgress > 0f)
        {
            var autoAttackColor = ParseColor(MeleeHintAutoAttackColorHex.Value, Color.white);
            autoAttackColor.a *= Mathf.Clamp01(MeleeHintOpacity.Value);
            color = Color.Lerp(color, autoAttackColor, easedWarning);
        }
        _hintLayoutDiameter = diameter;
        _hintVisible = true;
        _steadyHintKey = int.MinValue;
        SetSprite(_hintShape!, color, diameter);
    }

    private void ShowHintAtOriginalSize()
    {
        var thickness = Mathf.Clamp(MeleeHintThickness.Value, 0.5f, 16f);
        ApplyHintThickness(thickness);
        ApplyHintSprite();
        var diameter = HintOriginalSize(CurrentCrosshairDiameter());
        var key = Mathf.RoundToInt(NormalizedShapeThickness(thickness) * 4f) * 100000
            + Mathf.RoundToInt(diameter * 4f);
        var colorHex = MeleeHintColorHex.Value;
        var opacity = Mathf.Clamp01(MeleeHintOpacity.Value);
        if (_hintVisible
            && key == _steadyHintKey
            && colorHex == _steadyHintHex
            && Mathf.Approximately(opacity, _steadyHintOpacity))
            return;

        _steadyHintKey = key;
        _steadyHintHex = colorHex;
        _steadyHintOpacity = opacity;
        _hintLayoutDiameter = diameter;
        _hintVisible = true;
        SetSprite(_hintShape!, ConfiguredHintColor(), diameter);
    }

    private static Color ConfiguredHintColor()
    {
        var color = ParseColor(MeleeHintColorHex.Value, new Color(1f, 0.05f, 0.08f, 1f));
        color.a *= Mathf.Clamp01(MeleeHintOpacity.Value);
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

    /// <summary>
    /// Lowest HUD Y already taken by the crosshair or another hint. Positive Y is up.
    /// </summary>
    private float HintFloor()
    {
        var floor = 0f;
        if (_crosshairShapesActive)
        {
            floor = Mathf.Min(floor, -CurrentCrosshairDiameter() * 0.5f);
            if (_hintLayoutDiameter > 0f)
                floor = Mathf.Min(floor, -_hintLayoutDiameter * 0.5f);
        }

        if (_packHints.TryGetContentBottom(out var packBottom))
            floor = Mathf.Min(floor, packBottom);
        if (_itemHints.TryGetContentBottom(out var itemBottom))
            floor = Mathf.Min(floor, itemBottom);
        if (_toolHints.TryGetContentBottom(out var toolBottom))
            floor = Mathf.Min(floor, toolBottom);
        if (_scopeAmmo.TryGetContentBottom(out var scopeBottom))
            floor = Mathf.Min(floor, scopeBottom);
        return floor;
    }

    private static float HintOriginalSize(float crosshairDiameter)
    {
        return Mathf.Max(
            Mathf.Clamp(MeleeHintSize.Value, 8, 64),
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
        return ParseShape(MeleeHintShape.Value) switch
        {
            ReticleShape.Dots => _hintDotsSprite,
            ReticleShape.Arcs => _hintArcsSprite,
            ReticleShape.Lines => _hintLinesSprite,
            _ => _hintCircleSprite
        };
    }

    private static ReticleShape ParseShape(string value)
    {
        return value switch
        {
            "Dots" or "Two dots" => ReticleShape.Dots,
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

    private bool ReadMeleeCharge(out float progress, out float autoStrikeWarningProgress, out IntPtr statePtr)
    {
        progress = 0f;
        autoStrikeWarningProgress = 0f;
        statePtr = IntPtr.Zero;

        try
        {
            if (!WieldedFrame.TryGetMelee(out var melee) || !Alive(melee))
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
        if (!_hintVisible && _hintLayoutDiameter <= 0f)
            return;

        _hintLayoutDiameter = 0f;
        _hintVisible = false;
        _steadyHintKey = int.MinValue;
        _steadyHintHex = null;
        _steadyHintOpacity = -1f;
        var renderer = _hintShape?.m_sprite;
        if (renderer != null)
            renderer.color = new Color(1f, 0.05f, 0.08f, 0f);
    }

    private static Color ParseColor(string raw, Color? fallback = null)
    {
        return HudColor.Parse(raw, fallback ?? DefaultColor());
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
