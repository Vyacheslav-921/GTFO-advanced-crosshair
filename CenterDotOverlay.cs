using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.InteropTypes;
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
/// Shown only when both are true:
/// - <see cref="GameStateManager.CurrentStateName"/> is <see cref="eGameStateName.InLevel"/>
/// - <see cref="FocusStateManager.CurrentState"/> is <see cref="eFocusState.FPS"/>
/// </summary>
internal class CenterDotOverlay : MonoBehaviour
{
    private const int TextureSize = 16;

    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<string> ColorHex { get; private set; } = null!;
    public static ConfigEntry<int> Size { get; private set; } = null!;
    public static ConfigEntry<float> Opacity { get; private set; } = null!;

    private GameObject? _root;
    private IntPtr _parentPtr;
    private UI_Sprite? _outline;
    private UI_Sprite? _dot;
    private float _nextCreateAttempt;
    private string? _appliedHex;
    private int _appliedSize = -1;
    private float _appliedOpacity = -1f;
    private bool _stateErrorLogged;

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

        ColorHex = config.Bind(
            "Center dot",
            "Color",
            "00E5FF",
            "Dot color as RRGGBB or RRGGBBAA. Example: FF3355.");

        Size = config.Bind(
            "Center dot",
            "Size",
            4,
            new ConfigDescription(
                "Colored dot diameter in HUD units. A 2px dark ring is drawn behind it.",
                new AcceptableValueRange<int>(1, 32)));

        Opacity = config.Bind(
            "Center dot",
            "Opacity",
            1f,
            new ConfigDescription(
                "Dot opacity. 0 is invisible, 1 is solid.",
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
    }

    private void OnDestroy()
    {
        DestroyRoot();
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

            return FocusStateManager.CurrentState == eFocusState.FPS;
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

            _outline = CreateSprite(rect, sprite, "Outline", layer, reference);
            _dot = CreateSprite(rect, sprite, "Dot", layer, reference);
            if (reference != null && _dot.m_sprite != null && _outline.m_sprite != null)
            {
                _outline.m_sprite.sortingOrder = reference.sortingOrder + 10;
                _dot.m_sprite.sortingOrder = reference.sortingOrder + 11;
            }

            _root = root;
            _parentPtr = parent.Pointer;
            _appliedHex = null;
            _appliedSize = -1;
            _appliedOpacity = -1f;
            ApplyStyle();
            _root.SetActive(false);
            Plugin.Instance.Log.LogInfo($"Center aim dot attached to HUD group '{parent.name}'.");
        }
        catch (Exception ex)
        {
            _root = null;
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
        _outline = null;
        _dot = null;
        _parentPtr = IntPtr.Zero;
        _appliedHex = null;
        _appliedSize = -1;
        _appliedOpacity = -1f;
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
        tex.filterMode = FilterMode.Bilinear;
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
        if (_dot == null || _outline == null)
            return;

        var hex = ColorHex.Value;
        var size = Mathf.Clamp(Size.Value, 1, 32);
        var opacity = Mathf.Clamp01(Opacity.Value);
        if (hex == _appliedHex && size == _appliedSize && Mathf.Approximately(opacity, _appliedOpacity))
            return;

        _appliedHex = hex;
        _appliedSize = size;
        _appliedOpacity = opacity;

        var color = ParseColor(hex);
        color.a *= opacity;
        SetSprite(_dot, color, size);
        SetSprite(_outline, new Color(0f, 0f, 0f, 0.9f * opacity), size + 2);
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

    private static Color ParseColor(string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return DefaultColor();

        if (!text.StartsWith("#", StringComparison.Ordinal))
            text = "#" + text;

        return ColorUtility.TryParseHtmlString(text, out var color) ? color : DefaultColor();
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
