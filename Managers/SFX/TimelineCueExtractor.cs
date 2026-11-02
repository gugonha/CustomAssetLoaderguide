using System;
using System.Collections.Generic;
using Il2CppSystem;
using UnityEngine.Timeline;

namespace CustomAssetLoader.Managers.SFX;

/// <summary>
/// Resolves a Timeline clip's displayName to raw audio bytes. CustomAssetLoader has no
/// per-appearance asset store, so callers supply this. A closure capturing an
/// appearanceID or bundle is the idiomatic use.
///
/// Return null to signal "no audio for this clip" — the extractor skips it silently,
/// which is the desired behaviour for clips whose displayName overlaps with a VFX
/// or animation clip.
/// </summary>
public delegate byte[] AudioByteLookup(string clipName);

/// <summary>
/// Extracts SoundCues from a Timeline and strips the audio tracks once they're read.
///
/// Ported from LEAGUE-OF-NINE/motions (Motions/CueExtractor.cs). Two behaviours are
/// preserved exactly:
///
///   1. Extraction iterates timeline.flattenedTracks -> track.clips, lowercases
///      clip.displayName, and asks the lookup for bytes. A null result skips the clip.
///
///   2. Stripping removes only Unity AudioTracks that were converted to cues. FMOD
///      tracks are never touched: they play events from custom .bank files through
///      the game's own RuntimeManager, which is the supported bank workflow.
///
/// Both operations mutate the timeline. Clone before calling if the original is
/// shared or addressable-cached; see <see cref="CloneExtractStripRegister"/> for the
/// whole pipeline in one call.
/// </summary>
public static class TimelineCueExtractor
{
    /// <summary>
    /// Walks every flattened track in <paramref name="timeline"/> and returns one cue
    /// per clip whose displayName resolves to audio bytes. Does not mutate the timeline.
    /// </summary>
    public static List<SoundCue> ExtractSoundCues(TimelineAsset timeline, AudioByteLookup lookup)
        => ExtractSoundCues(timeline, lookup, out _);

    /// <summary>
    /// Same as above, but also reports the instance IDs of the tracks that actually
    /// produced at least one cue. StripAudioTracks uses this so that only tracks we
    /// replaced get removed; untouched FMOD tracks (custom bank events) survive.
    /// </summary>
    public static List<SoundCue> ExtractSoundCues(
        TimelineAsset timeline, AudioByteLookup lookup, out HashSet<int> tracksWithCues)
    {
        var cues = new List<SoundCue>();
        tracksWithCues = new HashSet<int>();
        if (timeline == null || lookup == null) return cues;

        foreach (var track in timeline.flattenedTracks)
        {
            // Only Unity AudioTracks are ever converted. FMOD event tracks (e.g.
            // FMODCustomTrack) trigger events from custom .bank files and must be
            // left to the game, even if a clip name happens to match an audio file.
            if (!IsUnityAudioTrack(track)) continue;

            foreach (var clip in track.clips)
            {
                string clipName = clip.displayName?.ToLower();
                if (string.IsNullOrEmpty(clipName)) continue;

                var bytes = lookup(clipName);
                if (bytes == null) continue;

                Plugin.Log.LogInfo(
                    $"[Sfx] cue: {clipName} | {bytes.Length} bytes | " +
                    $"start={clip.start:F3}s clipIn={clip.clipIn:F3}s duration={clip.duration:F3}s");

                tracksWithCues.Add(track.GetInstanceID());
                cues.Add(new SoundCue
                {
                    StartTime = (float)clip.start,
                    ClipIn = (float)clip.clipIn,
                    Duration = (float)clip.duration,
                    WavData = bytes,
                    Triggered = false
                });
            }
        }

        return cues;
    }

    /// <summary>
    /// True for Unity's built-in AudioTrack. FMOD tracks (any type whose name contains
    /// "FMOD", or whose clips carry FMOD assets) are explicitly NOT audio tracks here.
    /// </summary>
    private static bool IsUnityAudioTrack(TrackAsset track)
    {
        var typeName = track.GetIl2CppType().Name;
        if (typeName.Contains("FMOD")) return false;
        return typeName.Contains("AudioTrack");
    }

    /// <summary>
    /// Removes the Unity AudioTracks listed in <paramref name="onlyTracks"/> (those whose
    /// clips were converted to cues). FMOD tracks are never removed. Mutates the
    /// timeline. Safe to call multiple times.
    /// </summary>
    public static void StripAudioTracks(TimelineAsset timeline, HashSet<int> onlyTracks)
    {
        if (timeline == null || onlyTracks == null || onlyTracks.Count == 0) return;

        var tracksToRemove = new List<TrackAsset>();

        foreach (var track in timeline.flattenedTracks)
        {
            if (!onlyTracks.Contains(track.GetInstanceID())) continue;
            if (!IsUnityAudioTrack(track)) continue; // belt and braces
            tracksToRemove.Add(track);
        }

        foreach (var track in tracksToRemove)
        {
            timeline.DeleteTrack(track);
            Plugin.Log.LogInfo($"[Sfx] stripped audio track: {track.name}");
        }
    }

    /// <summary>
    /// End-to-end helper: clones the source timeline, extracts cues into an out
    /// parameter, strips audio tracks from the clone, registers the cues against the
    /// clone, and returns the clone.
    ///
    /// Mirrors CueExtractor.GetOrCacheTimeline from the motions project. The clone is
    /// required because StripAudioTracks mutates the asset, and addressable-cached
    /// Timelines are shared across every play — stripping the original would break
    /// vanilla playback for anything else using that asset.
    /// </summary>
    public static TimelineAsset CloneExtractStripRegister(
        TimelineAsset source,
        AudioByteLookup lookup,
        out List<SoundCue> cues)
    {
        cues = null;
        if (source == null) return null;

        var clone = UnityEngine.Object.Instantiate(source);

        cues = ExtractSoundCues(clone, lookup, out var replaced);
        StripAudioTracks(clone, replaced);

        if (cues.Count > 0)
            SoundCueCache.Register(clone, cues);

        return clone;
    }
}
