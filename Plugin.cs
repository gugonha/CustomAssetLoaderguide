using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CustomAssetLoader.Patches.Features;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace CustomAssetLoader;

[BepInPlugin(GUID, PLUGIN_NAME, VERSION)]
[BepInDependency("Lethe", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public static new ManualLogSource Log { get; private set; } = null!;

    public override void Load()
    {
        Log = base.Log;
        BundleLog.Bind(Config);

        var harmony = new Harmony(GUID);

        BundlePatches.Apply(harmony);
        BattleEffectListPatches.Apply(harmony);
        CustomBundleMotionPatches.Apply(harmony);
        BankPatches.Apply(harmony);
        SpriteIdlePatches.Apply(harmony);

        // SFX pipeline — FMOD Core playback, no banks.
        try { MotionsSFXPatches.Apply(harmony); }
        catch (System.Exception ex) { Log.LogError($"[Sfx] init failed, audio tracks disabled: {ex}"); }

        BundleLog.Loaded($"{PLUGIN_NAME} v{VERSION} loaded.");

    }
}
