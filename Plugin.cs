using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace GtfoTestmod;

/// <summary>
/// Entry point for the BepInEx plugin.
/// BepInEx discovers this class via <see cref="BepInPluginAttribute"/> and calls <see cref="Load"/>
/// once when the game starts (IL2CPP / Unity).
///
/// Docs:
/// - BepInEx 6 plugin basics: https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/1_setup.html
/// - IL2CPP Unity plugins: https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/2_plugin_start.html
/// - Harmony patching: https://harmony.pardeike.net/articles/intro.html
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    // Unique id used by BepInEx + Harmony (must stay stable across versions).
    public const string PluginGuid = "V-921.GTFO-better-crosshair";
    public const string PluginName = "GTFO-better-crosshair"; 
    public const string PluginVersion = "1.0.0";

    /// <summary>Singleton so patches / MonoBehaviours can log through this plugin instance.</summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <summary>
    /// Called by BepInEx after the plugin DLL is loaded.
    /// This is where we register runtime components and apply Harmony patches.
    /// </summary>
    public override void Load()
    {
        // Keep a static reference for other classes (ClockOverlay, patches) to reach Log.
        Instance = this;
        Log.LogInfo($"{PluginName} is loaded!");

        // AddComponent attaches a Unity MonoBehaviour to the plugin's GameObject.
        // ClockOverlay.Update will then run every frame and bind to PUI_Watermark when ready.
        // (See ClockOverlay.cs — it does NOT Harmony-patch watermark methods.)
       // AddComponent<ClockOverlay>();
        Log.LogInfo("Clock overlay ready (awaits watermark bind).");

        // Harmony.PatchAll() scans this assembly for [HarmonyPatch] classes and applies them.
        // Currently that means Patches/RundownPagePatch.cs → CM_PageRundown_New.Setup postfix.
        // https://harmony.pardeike.net/articles/patching.html
        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();
        Log.LogInfo("Harmony patches applied.");
    }
}
