using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Timeline;

namespace CustomAssetLoader.Managers.SFX;

/// <summary>
/// Scans a freshly loaded bundle for TimelineAssets, extracts SFX cues from the
/// parallel .bytes TextAssets the author's editor script generated, registers the
/// cues, and strips the audio tracks.
///
/// Direct port of LEAGUE-OF-NINE/motions:
///   MotionData.FindBytesAsset -> FindBytesAsset below
///   CueExtractor.GetOrCacheTimeline -> the extract/strip/register loop below
///
/// No external folder. No manifest entry. The bytes ride inside the bundle, next to
/// the audio file they were copied from.
/// </summary>
public static class BundleSfxScanner
{
    public static void ScanBundle(AssetBundle bundle, string bundlePath)
    {
        if (bundle == null) return;

        TimelineAsset[] timelines;
        try { timelines = LoadTimelines(bundle); }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Sfx] LoadAllAssets failed for '{bundle.name}': {ex}");
            return;
        }

        if (timelines.Length == 0)
        {
            Plugin.Log.LogInfo($"[Sfx] '{bundle.name}' contains no TimelineAssets.");
            return;
        }

        // Closure over the loaded bundle. One lookup, no per-bundle state to clean up
        // later — the bundle's lifetime is the lookup's lifetime.
        AudioByteLookup lookup = clipName => FindBytesAsset(bundle, clipName);

        int totalCues = 0;
        foreach (var timeline in timelines)
        {
            try
            {
                var cues = TimelineCueExtractor.ExtractSoundCues(timeline, lookup, out var replaced);
                if (cues.Count > 0)
                {
                    SoundCueCache.Register(timeline, cues);
                    totalCues += cues.Count;
                    Plugin.Log.LogInfo($"[Sfx]   -> registered {cues.Count} cue(s) on timeline '{timeline.name}'.");

                    // Strip only the tracks we replaced with cues. Timelines with no
                    // matching .bytes assets, and FMOD tracks that point at events in
                    // custom .bank files, are left exactly as authored.
                    TimelineCueExtractor.StripAudioTracks(timeline, replaced);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Sfx] scan of '{timeline.name}': {ex}");
            }
        }

        Plugin.Log.LogInfo(
            $"[Sfx] scanned '{bundle.name}': {timelines.Length} timeline(s), {totalCues} cue(s).");
    }

    /// <summary>
    /// Port of MotionData.FindBytesAsset. Searches the bundle for a TextAsset whose
    /// name matches "{clipName}.bytes" (exact, or fuzzy to catch the
    /// ".wav.bytes" / ".ogg.bytes" form the editor script produces).
    /// </summary>
    private static byte[] FindBytesAsset(AssetBundle bundle, string clipName)
    {
        if (string.IsNullOrEmpty(clipName)) return null;

        string target = clipName.ToLower();

        foreach (var assetName in bundle.GetAllAssetNames())
        {
            string lower = assetName.ToLower();

            bool isExact = lower == target + ".bytes"
                        || lower.EndsWith("/" + target + ".bytes");

            bool isFuzzy = (lower.Contains("/" + target + ".") || lower.StartsWith(target + "."))
                        && lower.EndsWith(".bytes");

            if (!isExact && !isFuzzy) continue;

            var asset = bundle.LoadAsset(assetName, Il2CppType.Of<TextAsset>());
            var text = asset?.TryCast<TextAsset>();
            if (text != null)
            {
                Plugin.Log.LogInfo(
                    $"[Sfx] '{assetName}' ({text.bytes.Length} bytes) -> clip '{clipName}'.");
                return text.bytes;
            }
        }

        Plugin.Log.LogWarning($"[Sfx] no .bytes asset for clip '{clipName}'.");
        return null;
    }

    /// <summary>
    /// Returns every TimelineAsset in the bundle.
    ///
    /// Enumerates by name and loads one at a time rather than calling
    /// AssetBundle.LoadAllAssets(). LoadAllAssets internally reaches Unity's
    /// ReadOnlySpan-based path, which Il2CppInterop doesn't implement — it throws
    /// MissingMethodException on GetPinnableReference. GetAllAssetNames + LoadAsset
    /// is the documented workaround and returns the same objects.
    /// </summary>
    private static TimelineAsset[] LoadTimelines(AssetBundle bundle)
    {
        var names = bundle.GetAllAssetNames();
        if (names == null || names.Length == 0) return System.Array.Empty<TimelineAsset>();

        var seen = new HashSet<int>();
        var result = new List<TimelineAsset>();

        foreach (var name in names)
        {
            // Top-level load first: cheap, covers the common case.
            TryCollect(bundle.LoadAsset(name, Il2CppType.Of<UnityEngine.Object>()), seen, result);

            // Sub-asset sweep: covers the case where the timeline is embedded inside
            // a prefab or another asset and never appears as a top-level name.
            try
            {
                var subs = bundle.LoadAssetWithSubAssets(name, Il2CppType.Of<UnityEngine.Object>());
                if (subs != null)
                    foreach (var sub in subs)
                        TryCollect(sub, seen, result);
            }
            catch
            {
                // Some interop builds throw on LoadAssetWithSubAssets for certain asset
                // types; a failure here is not fatal, we just miss sub-assets for that name.
            }
        }

        return result.ToArray();
    }

    private static void TryCollect(UnityEngine.Object asset, HashSet<int> seen, List<TimelineAsset> into)
    {
        if (asset == null) return;

        var timeline = asset.TryCast<TimelineAsset>();
        if (timeline == null && asset.GetIl2CppType().Name == "TimelineAsset")
            timeline = asset.Cast<TimelineAsset>();

        if (timeline == null) return;
        if (!seen.Add(timeline.GetInstanceID())) return;

        Plugin.Log.LogInfo($"[Sfx]   found TimelineAsset '{timeline.name}'.");
        into.Add(timeline);
    }
}
