using System;
using System.Reflection;
using CustomAssetLoader.Managers.SFX;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine.Playables;

namespace CustomAssetLoader.Patches.Features;

/// <summary>
/// Attaches a SoundCueTicker to the master PlayableDirector whenever the character
/// changes motion, mirroring the ChangeMotion hook from LEAGUE-OF-NINE/motions.
///
/// Why not PlayableDirector.Play: the game does not route its motion changes through
/// it. It drives the director by setting the playable asset and stepping the graph
/// itself. ChangeMotion is the game's own dispatch point — the same one Motions.cs
/// hooks — so cues are attached there.
///
/// PlayableDirector.Play(PlayableAsset) and Play() are also hooked, defensively, for
/// directors the game might play directly (dashboard effects, screen borders). The
/// ticker attach is idempotent, so overlapping hooks are harmless.
/// </summary>
public static class MotionsSFXPatches
{
    private static bool _applied;

    public static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        try { ClassInjector.RegisterTypeInIl2Cpp<SoundCueTicker>(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[Sfx] ClassInjector for SoundCueTicker: {ex.Message}"); }

        PatchChangeMotion(harmony);
        PatchPlay(harmony, nameof(PlayableDirector.Play), Type.EmptyTypes);
        PatchPlay(harmony, nameof(PlayableDirector.Play), new[] { typeof(PlayableAsset) });
        PatchStop(harmony);

        Plugin.Log.LogInfo("[Sfx] MotionSfxPatches applied.");
    }

    // ---- CharacterAppearance.ChangeMotion -------------------------------------

    private static void PatchChangeMotion(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(SD.CharacterAppearance), "ChangeMotion");
        if (method == null)
        {
            Plugin.Log.LogWarning("[Sfx] SD.CharacterAppearance.ChangeMotion not found.");
            return;
        }

        harmony.Patch(method, postfix: new HarmonyMethod(
            typeof(MotionsSFXPatches), nameof(Postfix_ChangeMotion)));

        Plugin.Log.LogInfo("[Sfx] patched SD.CharacterAppearance.ChangeMotion.");
    }

    /// <summary>
    /// Parameter names must match the game's spelling — Harmony binds by name.
    /// Motions.cs uses 'motiondetail' lowercase, keep it.
    /// </summary>
    public static void Postfix_ChangeMotion(
        SD.CharacterAppearance __instance,
        MOTION_DETAIL motiondetail,
        int index)
    {
        if (__instance == null) return;
        // in Postfix_ChangeMotion, at the top
        Plugin.Log.LogInfo($"[Sfx] ChangeMotion postfix: {motiondetail} idx={index}");
        try
        {
            var director = __instance._playableDirector;
            if (director == null)
            {
                Plugin.Log.LogInfo("[Sfx] ChangeMotion: _playableDirector is null.");
                return;
            }

            // The game may not have assigned playableAsset yet at postfix time. Log
            // the current value and, if it's null, wait one frame by deferring to a
            // ticker that re-checks in its Update.
            var asset = director.playableAsset;
            var name = asset != null ? asset.name : "null";

            Plugin.Log.LogInfo(
                $"[Sfx] ChangeMotion fired: detail={motiondetail} index={index} asset={name}");

            if (asset == null) return;

            if (!SoundCueCache.TryGet(asset, out var cues) || cues.Count == 0)
            {
                Plugin.Log.LogInfo($"[Sfx]   no registered cues for '{name}'.");
                return;
            }

            AttachTicker(director, cues);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Sfx] Postfix_ChangeMotion: {ex}");
        }
    }

    // ---- PlayableDirector.Play (fallback for other directors) -----------------

    private static void PatchPlay(Harmony harmony, string methodName, Type[] args)
    {
        var method = AccessTools.Method(typeof(PlayableDirector), methodName, args);
        if (method == null) return;

        harmony.Patch(method, postfix: new HarmonyMethod(
            typeof(MotionsSFXPatches), nameof(Postfix_Play)));
    }

    public static void Postfix_Play(PlayableDirector __instance)
    {
        if (__instance == null) return;
        // in Postfix_Play, at the top
        Plugin.Log.LogInfo($"[Sfx] Play postfix: {__instance.playableAsset?.name ?? "null"}");
        try
        {
            var asset = __instance.playableAsset;
            if (asset == null) return;

            if (!SoundCueCache.TryGet(asset, out var cues) || cues.Count == 0) return;

            Plugin.Log.LogInfo($"[Sfx] Play fired on '{asset.name}' ({cues.Count} cue(s)).");
            AttachTicker(__instance, cues);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Sfx] Postfix_Play: {ex}");
        }
    }

    // ---- PlayableDirector.Stop ------------------------------------------------

    private static void PatchStop(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(PlayableDirector), nameof(PlayableDirector.Stop));
        if (method == null) return;

        harmony.Patch(method, postfix: new HarmonyMethod(
            typeof(MotionsSFXPatches), nameof(Postfix_Stop)));
    }

    public static void Postfix_Stop(PlayableDirector __instance)
    {
        if (__instance == null) return;
        try
        {
            var ticker = __instance.gameObject.GetComponent<SoundCueTicker>();
            if (ticker != null) ticker.StopAllCues();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Sfx] Postfix_Stop: {ex}");
        }
    }

    // ---- Shared -------------------------------------------------------------

    private static void AttachTicker(
        PlayableDirector director,
        System.Collections.Generic.List<SoundCue> sourceCues)
    {
        // Fresh copy: two attaches in one frame (ChangeMotion + Play both firing)
        // would otherwise share Triggered flags and double-fire.
        var cues = new System.Collections.Generic.List<SoundCue>(sourceCues);

        var ticker = director.gameObject.GetComponent<SoundCueTicker>();
        if (ticker == null)
        {
            ticker = director.gameObject.AddComponent<SoundCueTicker>();
            ticker.KeepAlive = false;
        }

        ticker.Director = director;
        ticker.SoundCues = cues;
        ticker.ResetTriggers();

        Plugin.Log.LogInfo(
            $"[Sfx] ticker attached to '{director.gameObject.name}' ({cues.Count} cue(s)).");
    }
}
