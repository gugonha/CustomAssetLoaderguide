using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Playables;

namespace CustomAssetLoader.Patches.Features;
public static class SpriteIdlePatches
{
    private static bool HideSpineBySetActive = true;

    private sealed class HiddenSpine
    {
        public readonly List<Renderer> Renderers = new();
        public GameObject DeactivatedObject;
    }

    private static readonly Dictionary<int, SpriteIdleEnforcer> _enforcers = new();
    private static readonly Dictionary<int, HiddenSpine> _hidden = new();

    public static void Apply(Harmony harmony)
    {
        try { ClassInjector.RegisterTypeInIl2Cpp<SpriteIdleEnforcer>(); }
        catch (Exception ex) { BundleLog.VerboseWarn($"[SpriteIdle] ClassInjector: {ex.Message}"); }

        MethodInfo changeMotion = AccessTools.Method(typeof(SD.CharacterAppearance), "ChangeMotion");
        if (changeMotion == null)
        {
            BundleLog.Error("[SpriteIdle] Target missing ChangeMotion");
            return;
        }

        harmony.Patch(
            changeMotion,
            postfix: new HarmonyMethod(typeof(SpriteIdlePatches), nameof(Postfix_ChangeMotion)));
    }

    public static void Postfix_ChangeMotion(SD.CharacterAppearance __instance, MOTION_DETAIL motiondetail)
    {
        if (__instance == null) return;

        try
        {
            int id = __instance.GetInstanceID();
            bool isIdle = motiondetail == MOTION_DETAIL.Idle;
            bool managed = _enforcers.TryGetValue(id, out var existing) && existing != null && existing.Active;

            if (isIdle || motiondetail.ToString() == "Default")
            {
                BundleLog.Verbose(
                    $"[SpriteIdle] frame {Time.frameCount} '{__instance.name}' ChangeMotion {motiondetail} " +
                    $"(managed before: {managed})");
            }

            if (!isIdle)
            {
                if (managed)
                {
                    existing.Active = false;
                    Release(id);
                    BundleLog.Verbose(
                        $"[SpriteIdle] frame {Time.frameCount} '{__instance.name}' left Idle -> {motiondetail}, released.");
                }
                return;
            }

            var idle = FindMotion(__instance, MOTION_DETAIL.Idle);
            bool hasSprite = GetSpriteRenderer(__instance) != null;
            bool spriteIdle = idle != null && !idle.isSpineAnim && hasSprite;

            BundleLog.Verbose(
                $"[SpriteIdle] '{__instance.name}' Idle: motionFound={idle != null} " +
                $"isSpineAnim={(idle != null ? idle.isSpineAnim.ToString() : "n/a")} " +
                $"hasSpriteRenderer={hasSprite} -> sprite idle={spriteIdle}");

            if (!spriteIdle)
            {
                if (existing != null)
                {
                    existing.Active = false;
                    Release(id);
                }
                return;
            }

            var enforcer = GetOrAttach(__instance, id);
            enforcer.Active = true;
            enforcer.CorrectionsLogged = 0;
            Enforce(enforcer, "ChangeMotion(Idle)");
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"[SpriteIdle] Postfix_ChangeMotion failed: {ex}");
        }
    }

    internal static void Enforce(SpriteIdleEnforcer e, string source)
    {
        var appearance = e.Appearance;
        if (appearance == null) return;

        var sprite = GetSpriteRenderer(appearance);
        if (sprite == null) return;

        int id = appearance.GetInstanceID();

        bool wasActive = sprite.gameObject.activeSelf;
        bool wasEnabled = sprite.enabled;

        if (!wasActive) sprite.gameObject.SetActive(true);
        if (!wasEnabled) sprite.enabled = true;

        int hiddenNow = HideSpine(appearance, id);

        // LOOPING
        var idle = FindMotion(appearance, MOTION_DETAIL.Idle);
        var director = appearance._playableDirector;
        if (director != null && idle != null && idle.timelineAssets != null && idle.timelineAssets.Count > 0)
        {
            var timeline = idle.timelineAssets[0];
            if (timeline != null)
            {
                if (director.playableAsset != timeline)
                {
                    director.playableAsset = timeline;
                    director.Play();
                }

                if (director.state == PlayState.Playing)
                {
                    // If the timeline is at 98% or more of its duration, snap back to 0 (the naenae method)
                    if (director.duration > 0 && director.time >= director.duration * 0.98f)
                    {
                        director.time = 0;
                        director.Play();
                    }
                }
                else
                {
                    director.Play();
                }
            }
        }

        bool corrected = !wasActive || !wasEnabled || hiddenNow > 0;
        if (corrected && source == "LateUpdate" && e.CorrectionsLogged < 6)
        {
            e.CorrectionsLogged++;
            BundleLog.Verbose(
                $"[SpriteIdle] frame {Time.frameCount} '{appearance.name}': the game changed state during Idle " +
                $"(sprite active={wasActive} enabled={wasEnabled}, spine renderers re-shown={hiddenNow}); re-applied.");
        }
    }

    private static int HideSpine(SD.CharacterAppearance appearance, int id)
    {
        Transform spine = appearance._currentSpineSkin != null
            ? appearance._currentSpineSkin.transform
            : null;
        if (spine == null) return 0;

        if (!_hidden.TryGetValue(id, out var hidden))
        {
            if (_hidden.Count > 512) _hidden.Clear();
            hidden = new HiddenSpine();
            _hidden[id] = hidden;
        }

        if (HideSpineBySetActive)
        {
            if (!spine.gameObject.activeSelf) return 0;
            spine.gameObject.SetActive(false);
            hidden.DeactivatedObject = spine.gameObject;
            return 1;
        }

        if (!spine.gameObject.activeInHierarchy) return 0;

        int count = 0;
        foreach (var r in spine.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !r.enabled) continue;
            if (r.TryCast<SpriteRenderer>() != null) continue;

            r.enabled = false;
            hidden.Renderers.Add(r);
            count++;
        }
        return count;
    }

    private static void Release(int id)
    {
        if (!_hidden.TryGetValue(id, out var hidden)) return;

        foreach (var r in hidden.Renderers)
        {
            if (r != null) r.enabled = true;
        }

        if (hidden.DeactivatedObject != null) hidden.DeactivatedObject.SetActive(true);

        _hidden.Remove(id);
    }

    private static SpriteIdleEnforcer GetOrAttach(SD.CharacterAppearance appearance, int id)
    {
        if (_enforcers.TryGetValue(id, out var cached) && cached != null)
        {
            cached.Appearance = appearance;
            return cached;
        }

        var enforcer = appearance.gameObject.GetComponent<SpriteIdleEnforcer>();
        if (enforcer == null) enforcer = appearance.gameObject.AddComponent<SpriteIdleEnforcer>();
        enforcer.Appearance = appearance;

        if (_enforcers.Count > 512) _enforcers.Clear();
        _enforcers[id] = enforcer;
        return enforcer;
    }

    private static SD.CharacterMotion FindMotion(SD.CharacterAppearance appearance, MOTION_DETAIL detail)
    {
        try
        {
            var motionList = appearance.motionList;
            if (motionList == null) return null;

            for (int i = 0; i < motionList.Count; i++)
            {
                var element = motionList[i];
                if (element == null) continue;
                if (element._motiondetail == detail) return element;
            }
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"[SpriteIdle] FindMotion failed: {ex.Message}");
        }

        return null;
    }

    private static SpriteRenderer GetSpriteRenderer(SD.CharacterAppearance appearance)
    {
        var source = appearance.sprenderer_charactermotion;
        if (source == null) return null;

        var sprite = source.transform.GetComponent<SpriteRenderer>();
        if (sprite == null) sprite = source.transform.GetComponentInChildren<SpriteRenderer>(true);
        return sprite;
    }
}

public class SpriteIdleEnforcer : MonoBehaviour
{
    public SpriteIdleEnforcer(IntPtr ptr) : base(ptr) { }

    public SD.CharacterAppearance Appearance;
    public bool Active;
    public int CorrectionsLogged;

    private void LateUpdate()
    {
        if (!Active || Appearance == null) return;
        SpriteIdlePatches.Enforce(this, "LateUpdate");
    }
}