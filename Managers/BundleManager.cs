using Addressable;
using BepInEx;
using CustomAssetLoader.Managers.SFX;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace CustomAssetLoader.Managers;

public static class BundleManager
{
    private const string LocatorId = "CustomAssetLoader.ModBundles";

    private static readonly Dictionary<string, AssetBundle> LoadedBundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BundleOverrideEntry> OverrideMap = new();
    private static readonly Dictionary<string, string> OverrideBundleDirectory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BundleOverrideEntry> KeyMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IResourceLocation> LocationByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BattleEffectList> InjectedBattleEffectLists = new();

    private static bool _reloadRequested;
    private static bool _instanceTagRegistered;
    private static bool _addressablesRegistered;
    /// <summary>
    /// Guards the whole ready-gate critical section (pending reload + catalog register + warmup).
    /// Nested calls from Addressables APIs that re-enter <c>IsInitializedAddressable</c> must no-op.
    /// </summary>
    private static bool _insideReadyGateCriticalSection;
    private static ModAssetProvider _provider;
    private static ResourceLocationMap _locator;

    public static int OverrideCount => OverrideMap.Count;

    /// <summary>
    /// Request a full manifest/bundle reload, then register the Addressables catalog when ready.
    /// Used at plugin load (pre-<c>LoadUserDataAndSetScene</c> window) and from that method's postfix
    /// (first load + Lethe hot reload).
    /// </summary>
    public static void RequestReload()
    {
        BundleLog.ReloadConfig();
        _reloadRequested = true;
        BundleLog.Verbose("Bundle reload requested.");
        // Kick processing without reading IsInitializedAddressable (avoids getter re-entry games).
        TryProcessReloadAndRegister();
    }

    /// <summary>
    /// Called from <c>get_IsInitializedAddressable</c> when the native flag is already true.
    /// Must not re-enter that getter (critical-section guard; never reads the property here).
    /// </summary>
    public static void EnsureAddressablesRegisteredFromReadyGate()
    {
        TryProcessReloadAndRegister();
    }

    private static void TryProcessReloadAndRegister()
    {
        if (_insideReadyGateCriticalSection)
        {
            return;
        }

        _insideReadyGateCriticalSection = true;
        try
        {
            if (_reloadRequested)
            {
                PerformFullReload();
                _reloadRequested = false;
            }

            if (OverrideMap.Count == 0)
            {
                return;
            }

            AddressableManager manager = AddressableManager.Instance;
            if (manager == null || manager._hashKeys == null)
            {
                return;
            }

            if (_addressablesRegistered && IsCatalogHealthy(manager))
            {
                return;
            }

            RegisterWithAddressablesCore(manager);
        }
        finally
        {
            _insideReadyGateCriticalSection = false;
        }
    }

    /// <summary>
    /// Unload previous mod bundles, clear maps/locator state, rescan manifests, preload bundles.
    /// Does not register Addressables; caller does that when the manager is ready.
    /// </summary>
    private static void PerformFullReload()
    {
        EnsureInstanceTagRegistered();
        RemoveInjectedBattleEffectLists();
        UnloadLoadedModBundles();
        RemoveManagedLocator();
        ShaderRemapper.ResetSessionState();

        OverrideMap.Clear();
        OverrideBundleDirectory.Clear();
        KeyMap.Clear();
        LocationByKey.Clear();
        _addressablesRegistered = false;
        _nativePipelineWarmedUp = false;

        string modsRoot = Path.Combine(Paths.PluginPath, MODS_ROOT);
        if (!Directory.Exists(modsRoot))
        {
            BundleLog.Verbose($"Mods root not found: {modsRoot}");
            return;
        }

        int manifestCount = 0;
        foreach (string modDir in Directory.GetDirectories(modsRoot))
        {
            string bundleDir = Path.Combine(modDir, BUNDLE_DIRECTORY);
            string manifestPath = Path.Combine(bundleDir, BUNDLE_MANIFEST_NAME);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            manifestCount++;
            LoadManifestFrom(bundleDir, manifestPath);
        }

        if (manifestCount == 0)
        {
            BundleLog.Verbose($"No bundle manifests found under {modsRoot}");
        }
        else if (OverrideMap.Count > 0)
        {
            BundleLog.Startup(
                $"Loaded {OverrideMap.Count} bundle override(s) from {manifestCount} manifest(s).");
            PreloadOverrideBundles();
        }

        InjectBattleEffectListsIfManagerReady();
    }

    private static void UnloadLoadedModBundles()
    {
        CustomAssetLoader.Managers.SFX.SoundCueCache.Clear();
        foreach (KeyValuePair<string, AssetBundle> pair in LoadedBundles)
        {
            AssetBundle bundle = pair.Value;
            if (bundle == null)
            {
                continue;
            }

            try
            {
                bundle.Unload(false);
            }
            catch (Exception ex)
            {
                BundleLog.VerboseWarn($"AssetBundle.Unload failed for {pair.Key}: {ex.Message}");
            }
        }

        LoadedBundles.Clear();
    }

    private static void RemoveManagedLocator()
    {
        if (_locator == null)
        {
            return;
        }

        try
        {
            Addressables.RemoveResourceLocator(_locator.Cast<IResourceLocator>());
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"RemoveResourceLocator on reload skipped: {ex.Message}");
        }

        _locator = null;
    }

    private static void LoadManifestFrom(string bundleDir, string manifestPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<AssetManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest?.overrides == null || manifest.overrides.Count == 0)
            {
                return;
            }

            foreach (var entry in manifest.overrides)
            {
                if (!TryValidateEntry(entry, out string error))
                {
                    BundleLog.Warn($"Skipping invalid bundle override in {manifestPath}: {error}");
                    continue;
                }

                string key = entry.GetKey();
                OverrideMap[key] = entry;
                OverrideBundleDirectory[key] = bundleDir;
            }
        }
        catch (Exception ex)
        {
            BundleLog.Error($"Failed to load bundle manifest {manifestPath}: {ex}");
        }
    }

    public static bool IsModAddressableKey(string addressableKey)
    {
        if (string.IsNullOrEmpty(addressableKey))
        {
            return false;
        }

        if (KeyMap.ContainsKey(addressableKey))
        {
            return true;
        }

        foreach (BundleOverrideEntry entry in OverrideMap.Values)
        {
            if (IsBattleEffectListEntry(entry)
                || string.IsNullOrEmpty(entry.resourceId)
                || addressableKey.IndexOf(entry.resourceId, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    public static bool IsReadyModAddressableKey(string addressableKey)
    {
        if (string.IsNullOrEmpty(addressableKey) || !KeyMap.ContainsKey(addressableKey))
        {
            return false;
        }

        AddressableManager manager = AddressableManager.Instance;
        if (manager?._hashKeys == null || !manager._hashKeys.Contains(addressableKey))
        {
            return false;
        }

        var locationDic = manager._locationDicByKey;
        if (locationDic != null && locationDic.ContainsKey(addressableKey))
        {
            locationDic.Remove(addressableKey);
            BundleLog.Verbose($"[CustomAssetLoader] cleared stale cached location: {addressableKey}");
        }

        return true;
    }

    public static bool TryGetModAddressableKey(Il2CppSystem.Object key, out string addressableKey)
    {
        addressableKey = null;
        if (key == null)
        {
            return false;
        }

        EnsureAddressablesRegisteredFromReadyGate();

        IResourceLocation asLoc = key.TryCast<IResourceLocation>();
        if (asLoc != null)
        {
            addressableKey = asLoc.PrimaryKey;
            if (string.IsNullOrEmpty(addressableKey))
            {
                addressableKey = asLoc.InternalId;
            }
        }
        else
        {
            addressableKey = key.ToString();
        }

        if (string.IsNullOrEmpty(addressableKey))
        {
            return false;
        }

        if (KeyMap.ContainsKey(addressableKey))
        {
            return true;
        }

        foreach (KeyValuePair<string, BundleOverrideEntry> pair in KeyMap)
        {
            if (!string.IsNullOrEmpty(pair.Value.resourceId)
                && addressableKey.IndexOf(pair.Value.resourceId, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                addressableKey = pair.Key;
                return true;
            }
        }

        return false;
    }

    public static bool TryResolveModLocation(Il2CppSystem.Object key, out IResourceLocation location)
    {
        location = null;
        if (!TryGetModAddressableKey(key, out string addressableKey))
        {
            return false;
        }

        return LocationByKey.TryGetValue(addressableKey, out location);
    }

    public static bool TryInstantiateOverride(string label, string resourceId, Transform parent, out GameObject instance)
    {
        EnsureAddressablesRegisteredFromReadyGate();
        return TryLoadGameObject(label, resourceId, parent, out instance);
    }

    public static bool TryInstantiateByAddressableKey(string addressableKey, Transform parent, out GameObject instance)
    {
        instance = null;
        EnsureAddressablesRegisteredFromReadyGate();
        if (string.IsNullOrEmpty(addressableKey)
            || !KeyMap.TryGetValue(addressableKey, out BundleOverrideEntry entry))
        {
            return false;
        }

        bool ok = TryLoadGameObject(entry.label, entry.resourceId, parent, out instance);
        BundleLog.Verbose(
            $"[CustomAssetLoader] TryLoadGameObject key={addressableKey} " +
            $"bundle={entry.bundle} path={entry.assetPath} ok={ok} " +
            $"go={(instance != null ? instance.name : "null")}");

        return ok;
    }

    private static void RegisterWithAddressablesCore(AddressableManager manager)
    {
        if (OverrideMap.Count == 0)
        {
            return;
        }

        try
        {
            EnsureProviderRegistered();
            if (_provider == null)
            {
                BundleLog.Error("ModAssetProvider registration failed.");
                return;
            }

            IResourceLocator previousLocator = _locator?.Cast<IResourceLocator>();
            _locator = new ResourceLocationMap(LocatorId, OverrideMap.Count);
            LocationByKey.Clear();

            var hashKeys = manager._hashKeys;
            var locationDic = manager._locationDicByKey;
            int registered = 0;

            foreach (BundleOverrideEntry entry in OverrideMap.Values)
            {
                if (!TryBuildAddressableKey(entry, out string addressableKey, out Il2CppSystem.Type resourceType))
                {
                    continue;
                }

                var location = new ResourceLocationBase(
                    addressableKey,
                    addressableKey,
                    _provider.ProviderId,
                    resourceType,
                    new Il2CppReferenceArray<IResourceLocation>(0));
                location.PrimaryKey = addressableKey;
                IResourceLocation locationIface = location.Cast<IResourceLocation>();

                _locator.Add(addressableKey, locationIface);
                LocationByKey[addressableKey] = locationIface;
                hashKeys?.Add(addressableKey);

                if (locationDic != null && locationDic.ContainsKey(addressableKey))
                {
                    locationDic.Remove(addressableKey);
                }

                KeyMap[addressableKey] = entry;
                registered++;
            }

            if (previousLocator != null)
            {
                try
                {
                    Addressables.RemoveResourceLocator(previousLocator);
                }
                catch (Exception ex)
                {
                    BundleLog.VerboseWarn($"RemoveResourceLocator skipped: {ex.Message}");
                }
            }

            AddLocatorWithPriority(_locator.Cast<IResourceLocator>());

            EnsureProviderRegistered();
            if (_provider == null)
            {
                _addressablesRegistered = false;
                BundleLog.Error("ModAssetProvider missing after catalog register.");
                return;
            }

            _addressablesRegistered = true;
            BundleLog.Startup($"Mod Addressables catalog ready: {registered} key(s).");

            // Warm up the native sprite pipeline once at registration time.
            // This is NOT optional diagnostics — skipping it causes a race where the
            // game's _locationDicByKey can cache official locations before our
            // AssetExists hook clears them, so the first in-game load misses our provider.
            WarmupNativeSpritePipeline();
        }
        catch (Exception ex)
        {
            _addressablesRegistered = false;
            BundleLog.Error($"Failed to register mod Addressables catalog: {ex}");
        }
    }

    private static bool _nativePipelineWarmedUp;

    /// <summary>
    /// One-shot warm-up: resolve each mod Sprite key through the real Addressables
    /// pipeline (locator → provider → Provide). Logging is optional; the load itself
    /// must always run so provider resolution and locator order are established before
    /// gameplay can cache official IResourceLocations in _locationDicByKey.
    /// </summary>
    private static void WarmupNativeSpritePipeline()
    {
        if (_nativePipelineWarmedUp)
        {
            return;
        }

        _nativePipelineWarmedUp = true;

        LogLocatorOrder();
        ClearStaleLocationsForAllModKeys();

        foreach (KeyValuePair<string, BundleOverrideEntry> pair in KeyMap)
        {
            if (!pair.Value.assetType.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var handle = Addressables.LoadAssetAsync<Sprite>((Il2CppSystem.Object)pair.Key);
                Sprite sprite = handle.WaitForCompletion();
                BundleLog.Verbose(
                    $"[SpriteWarmup] key={pair.Key} status={handle.Status} " +
                    $"sprite={(sprite != null ? sprite.name : "null")}");
            }
            catch (Exception ex)
            {
                BundleLog.Error($"[SpriteWarmup] key={pair.Key} failed: {ex.Message}");
            }
        }
    }

    private static void ClearStaleLocationsForAllModKeys()
    {
        AddressableManager manager = AddressableManager.Instance;
        var locationDic = manager?._locationDicByKey;
        if (locationDic == null)
        {
            return;
        }

        foreach (string key in KeyMap.Keys)
        {
            if (locationDic.ContainsKey(key))
            {
                locationDic.Remove(key);
                BundleLog.Verbose($"[CustomAssetLoader] cleared stale cached location: {key}");
            }
        }
    }

    private static void LogLocatorOrder()
    {
        if (!BundleLog.VerboseLog)
        {
            return;
        }

        try
        {
            var locatorInfos = Addressables.m_AddressablesInstance?.m_ResourceLocators;
            if (locatorInfos == null)
            {
                BundleLog.VerboseWarn("[SpriteWarmup] m_ResourceLocators is null");
                return;
            }

            for (int i = 0; i < locatorInfos.Count; i++)
            {
                IResourceLocator locator = locatorInfos[i]?.Locator;
                BundleLog.Verbose($"[SpriteWarmup] locator[{i}] id={locator?.LocatorId ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"[SpriteWarmup] locator dump failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers our locator at index 0 of AddressablesImpl.m_ResourceLocators.
    /// LoadAssetAsync iterates locators in list order and the first one that resolves
    /// the key wins, so front insertion lets mod entries override official catalog keys.
    /// </summary>
    private static void AddLocatorWithPriority(IResourceLocator locator)
    {
        try
        {
            var impl = Addressables.m_AddressablesInstance;
            var locatorInfos = impl?.m_ResourceLocators;
            if (locatorInfos != null)
            {
                locatorInfos.Insert(0, new ResourceLocatorInfo(locator, null, null));
                return;
            }
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"Locator front-insert failed, appending instead: {ex.Message}");
        }

        Addressables.AddResourceLocator(locator, null, null);
    }

    private static bool IsCatalogHealthy(AddressableManager manager)
    {
        if (KeyMap.Count == 0 || manager._hashKeys == null)
        {
            return false;
        }

        var locationDic = manager._locationDicByKey;
        foreach (string key in KeyMap.Keys)
        {
            if (!manager._hashKeys.Contains(key))
            {
                return false;
            }

            if (locationDic != null && locationDic.ContainsKey(key))
            {
                return false;
            }
        }

        return IsLocatorInstalled();
    }

    private static bool IsLocatorInstalled()
    {
        if (_locator == null)
        {
            return false;
        }

        try
        {
            var locatorInfos = Addressables.m_AddressablesInstance?.m_ResourceLocators;
            if (locatorInfos == null)
            {
                return false;
            }

            for (int i = 0; i < locatorInfos.Count; i++)
            {
                IResourceLocator locator = locatorInfos[i]?.Locator;
                if (locator != null && locator.LocatorId == LocatorId)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void PreloadOverrideBundles()
    {
        // Key by full path so two mods with the same bundle file name both preload.
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, BundleOverrideEntry> pair in OverrideMap)
        {
            if (string.IsNullOrEmpty(pair.Value.bundle)
                || !OverrideBundleDirectory.TryGetValue(pair.Key, out string bundleDir))
            {
                continue;
            }

            string path = Path.Combine(bundleDir, pair.Value.bundle);
            if (!uniquePaths.Add(path))
            {
                continue;
            }

            if (TryLoadBundle(bundleDir, pair.Value.bundle, out _))
            {
                BundleLog.Verbose($"Preloaded mod AssetBundle: {path}");
            }
        }
    }

    public static bool HasResourceOverride(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        foreach (var registered in OverrideMap.Values)
        {
            if (string.Equals(registered.resourceId, resourceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryLoadUnityObject(string locationKey, Il2CppSystem.Type resourceType, out UnityEngine.Object asset)
    {
        asset = null;

        if (string.IsNullOrEmpty(locationKey) || !KeyMap.TryGetValue(locationKey, out BundleOverrideEntry entry))
        {
            return false;
        }

        if (!TryLoadBundleForEntry(entry, out AssetBundle bundle))
        {
            return false;
        }

        Il2CppSystem.Type loadType = resourceType ?? ResolveResourceType(entry);
        asset = LoadObjectAsset(bundle, entry.assetPath, loadType);
        if (asset == null)
        {
            BundleLog.Error(
                $"Failed to load asset. bundle={entry.bundle}, path={entry.assetPath}, type={loadType?.Name}");
            return false;
        }

        GameObject asGo = asset.TryCast<GameObject>();
        if (asGo != null)
        {
            ShaderRemapper.RemapPrefabAsset(asGo);
        }

        return true;
    }

    public static bool TryLoadSpriteOverride(string label, string resourceId, out Sprite sprite)
    {
        sprite = null;

        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        string mapKey = $"{label}\0{resourceId}";
        if (!OverrideMap.TryGetValue(mapKey, out BundleOverrideEntry entry))
        {
            return false;
        }

        return TryLoadSpriteFromEntry(entry, mapKey, out sprite);
    }

    public static bool TryLoadSpriteByAddressableKey(string addressableKey, out Sprite sprite)
    {
        sprite = null;
        EnsureAddressablesRegisteredFromReadyGate();

        if (!string.IsNullOrEmpty(addressableKey)
            && TryGetModAddressableKey(addressableKey, out string resolvedKey)
            && KeyMap.TryGetValue(resolvedKey, out BundleOverrideEntry keyedEntry))
        {
            foreach (KeyValuePair<string, BundleOverrideEntry> pair in OverrideMap)
            {
                if (pair.Value == keyedEntry
                    && TryLoadSpriteFromEntry(pair.Value, pair.Key, out sprite))
                {
                    return true;
                }
            }
        }

        foreach (KeyValuePair<string, BundleOverrideEntry> pair in OverrideMap)
        {
            BundleOverrideEntry entry = pair.Value;
            if (!entry.assetType.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(entry.resourceId)
                && addressableKey.IndexOf(entry.resourceId, StringComparison.OrdinalIgnoreCase) >= 0
                && TryLoadSpriteFromEntry(entry, pair.Key, out sprite))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetModAddressableKey(string key, out string addressableKey) =>
        TryGetModAddressableKey((Il2CppSystem.Object)key, out addressableKey);

    private static bool TryLoadSpriteFromEntry(
        BundleOverrideEntry entry,
        string mapKey,
        out Sprite sprite)
    {
        sprite = null;
        if (!entry.assetType.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!OverrideBundleDirectory.TryGetValue(mapKey, out string bundleDir)
            || !TryLoadBundle(bundleDir, entry.bundle, out AssetBundle bundle))
        {
            return false;
        }

        sprite = LoadObjectAsset(bundle, entry.assetPath, Il2CppType.Of<Sprite>())
            ?.TryCast<Sprite>();
        return sprite != null;
    }

    /// <summary>
    /// Loads a GameObject prefab asset from a mod bundle manifest entry without instantiating.
    /// </summary>
    public static bool TryLoadGameObjectAsset(string label, string resourceId, out GameObject prefab)
    {
        prefab = null;

        if (!TryResolveGameObjectEntry(label, resourceId, out BundleOverrideEntry entry)
            || !TryLoadBundleForEntry(entry, out AssetBundle bundle))
        {
            return false;
        }

        prefab = LoadObjectAsset(bundle, entry.assetPath, Il2CppType.Of<GameObject>())
            ?.TryCast<GameObject>();
        if (prefab == null)
        {
            BundleLog.Error(
                $"Failed to load GameObject asset. bundle={entry.bundle}, path={entry.assetPath}");
            return false;
        }

        ShaderRemapper.RemapPrefabAsset(prefab);
        return true;
    }

    public static bool TryLoadGameObject(string label, string resourceId, Transform parent, out GameObject instance)
    {
        instance = null;
        if (!TryLoadGameObjectAsset(label, resourceId, out GameObject prefab))
        {
            return false;
        }

        instance = parent != null
            ? UnityEngine.Object.Instantiate(prefab, parent)
            : UnityEngine.Object.Instantiate(prefab);
        if (instance != null)
        {
            // Prefab remap may not stick on all instance material slots; re-run on live GO.
            ShaderRemapper.RemapPrefabAsset(instance);

            EnsureInstanceTagRegistered();
            instance.AddComponent<BundleInstanceTag>();
            BundleLog.Verbose(
                $"[CustomAssetLoader] Tagged bundle instance " +
                $"label={label} resourceId={resourceId} go={instance.name} " +
                $"instanceId={instance.GetInstanceID()}");
        }
        return instance != null;
    }

    private static bool TryResolveGameObjectEntry(
        string label,
        string resourceId,
        out BundleOverrideEntry entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(label)
            && OverrideMap.TryGetValue($"{label}\0{resourceId}", out entry))
        {
            return entry.assetType.Equals("GameObject", StringComparison.OrdinalIgnoreCase);
        }

        foreach (BundleOverrideEntry registered in OverrideMap.Values)
        {
            if (!string.Equals(registered.resourceId, resourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry = registered;
            break;
        }

        return entry != null
            && entry.assetType.Equals("GameObject", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureInstanceTagRegistered()
    {
        if (_instanceTagRegistered)
        {
            return;
        }

        ClassInjector.RegisterTypeInIl2Cpp<BundleInstanceTag>();
        _instanceTagRegistered = true;
    }

    private static void EnsureProviderRegistered()
    {
        _provider ??= new ModAssetProvider();
        try
        {
            var providers = Addressables.ResourceManager?.m_ResourceProviders;
            if (providers == null)
            {
                _provider = null;
                return;
            }

            for (int i = 0; i < providers.Count; i++)
            {
                IResourceProvider existing = providers[i];
                if (existing != null
                    && string.Equals(existing.ProviderId, _provider.ProviderId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            providers.Add(_provider.Cast<IResourceProvider>());
        }
        catch (Exception ex)
        {
            BundleLog.Error($"Failed to add ModAssetProvider: {ex}");
            _provider = null;
        }
    }

    private static bool TryBuildAddressableKey(
        BundleOverrideEntry entry,
        out string addressableKey,
        out Il2CppSystem.Type resourceType)
    {
        addressableKey = null;
        resourceType = null;
        if (IsBattleEffectListEntry(entry))
        {
            return false;
        }

        resourceType = ResolveResourceType(entry);
        try
        {
            addressableKey = ResourceKeyBuilder.BuildKey(entry.label, entry.resourceId, resourceType);
            return !string.IsNullOrEmpty(addressableKey);
        }
        catch (Exception ex)
        {
            BundleLog.Error(
                $"ResourceKeyBuilder.BuildKey failed for {entry.label}/{entry.resourceId}: {ex}");
            return false;
        }
    }

    private static Il2CppSystem.Type ResolveResourceType(BundleOverrideEntry entry) =>
        entry.assetType.Equals("Sprite", StringComparison.OrdinalIgnoreCase)
            ? Il2CppType.Of<Sprite>()
            : Il2CppType.Of<GameObject>();

    private static bool TryLoadBundleForEntry(BundleOverrideEntry entry, out AssetBundle bundle)
    {
        string lookupKey = entry.GetKey();
        if (!OverrideBundleDirectory.TryGetValue(lookupKey, out string bundleDir))
        {
            foreach (var pair in OverrideMap)
            {
                if (pair.Value == entry)
                {
                    OverrideBundleDirectory.TryGetValue(pair.Key, out bundleDir);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(bundleDir))
        {
            bundle = null;
            return false;
        }

        return TryLoadBundle(bundleDir, entry.bundle, out bundle);
    }

    private static bool TryLoadBundle(string bundleDir, string bundleFileName, out AssetBundle bundle)
    {
        string cacheKey = Path.Combine(bundleDir, bundleFileName);
        if (LoadedBundles.TryGetValue(cacheKey, out bundle))
        {
            return true;
        }

        if (!File.Exists(cacheKey))
        {
            BundleLog.Error($"Mod bundle file not found: {cacheKey}");
            bundle = null;
            return false;
        }

        bundle = AssetBundle.LoadFromFile(cacheKey);
        if (bundle == null)
        {
            BundleLog.Error($"AssetBundle.LoadFromFile failed: {cacheKey}");
            return false;
        }
        try { CustomAssetLoader.Managers.SFX.BundleSfxScanner.ScanBundle(bundle, cacheKey); }
        catch (System.Exception ex) { BundleLog.Error($"SFX scan failed for {cacheKey}: {ex.Message}"); }
        LoadedBundles[cacheKey] = bundle;
        return true;
    }

    private static UnityEngine.Object LoadObjectAsset(AssetBundle bundle, string assetPath, Il2CppSystem.Type type)
    {
        // Unity lowercases internal bundle paths at build time; normalize so manifest
        // can use convenient mixed-case project paths without duplicate-key ambiguity.
        string normalizedPath = assetPath.ToLowerInvariant();
        UnityEngine.Object loaded = bundle.LoadAsset(normalizedPath, type);
        if (loaded != null)
        {
            return loaded;
        }

        string fileName = Path.GetFileName(normalizedPath);
        loaded = bundle.LoadAsset(fileName, type);
        if (loaded != null)
        {
            return loaded;
        }

        foreach (string name in bundle.GetAllAssetNames())
        {
            if (!name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            loaded = bundle.LoadAsset(name, type);
            if (loaded != null)
            {
                return loaded;
            }
        }

        return null;
    }

    public static bool IsBattleEffectListEntry(BundleOverrideEntry entry) =>
        entry != null
        && !string.IsNullOrEmpty(entry.assetType)
        && entry.assetType.Equals("BattleEffectList", StringComparison.OrdinalIgnoreCase);

    public static void InjectBattleEffectLists(BattleEffectManager manager)
    {
        if (manager == null)
        {
            return;
        }

        var next = manager.battleEffectLists_next;
        if (next == null)
        {
            BundleLog.Warn("[BattleEffectList] battleEffectLists_next is null; skip inject.");
            return;
        }

        foreach (KeyValuePair<string, BundleOverrideEntry> pair in OverrideMap)
        {
            if (!IsBattleEffectListEntry(pair.Value))
            {
                continue;
            }

            if (HasAliveInjectedList(pair.Key, next))
            {
                continue;
            }

            if (!TryLoadBattleEffectList(pair.Value, out BattleEffectList list) || list == null)
            {
                BundleLog.Error(
                    $"[BattleEffectList] load failed: {pair.Value.bundle} {pair.Value.assetPath}");
                continue;
            }

            RemapBattleEffectListPrefabs(list);
            next.Add(list);
            InjectedBattleEffectLists[pair.Key] = list;
            BundleLog.Startup(
                $"[BattleEffectList] injected {list.name} from {pair.Value.assetPath}");
        }
    }

    private static void InjectBattleEffectListsIfManagerReady()
    {
        try
        {
            BattleEffectManager manager = BattleEffectManager.Instance;
            if (manager != null)
            {
                InjectBattleEffectLists(manager);
            }
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"[BattleEffectList] inject skipped: {ex.Message}");
        }
    }

    private static void RemoveInjectedBattleEffectLists()
    {
        try
        {
            BattleEffectManager manager = BattleEffectManager.Instance;
            var next = manager?.battleEffectLists_next;
            if (next != null)
            {
                foreach (BattleEffectList list in InjectedBattleEffectLists.Values)
                {
                    if (list != null)
                    {
                        next.Remove(list);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BundleLog.VerboseWarn($"[BattleEffectList] remove injected skipped: {ex.Message}");
        }

        InjectedBattleEffectLists.Clear();
    }

    private static bool HasAliveInjectedList(
        string mapKey,
        Il2CppSystem.Collections.Generic.List<BattleEffectList> next)
    {
        if (!InjectedBattleEffectLists.TryGetValue(mapKey, out BattleEffectList tracked)
            || tracked == null)
        {
            return false;
        }

        for (int i = 0; i < next.Count; i++)
        {
            if (next[i] == tracked)
            {
                return true;
            }
        }

        next.Add(tracked);
        BundleLog.Verbose($"[BattleEffectList] re-added {tracked.name} after list drop.");
        return true;
    }

    private static bool TryLoadBattleEffectList(BundleOverrideEntry entry, out BattleEffectList list)
    {
        list = null;
        if (!TryLoadBundleForEntry(entry, out AssetBundle bundle))
        {
            return false;
        }

        UnityEngine.Object asset = LoadObjectAsset(
            bundle,
            entry.assetPath,
            Il2CppType.Of<BattleEffectList>());
        list = asset?.TryCast<BattleEffectList>();
        return list != null;
    }

    private static void RemapBattleEffectListPrefabs(BattleEffectList list)
    {
        if (list == null)
        {
            return;
        }

        RemapEffectLabelObjs(list.Effects_Label);
        if (list.Effects_Ability == null)
        {
            return;
        }

        for (int i = 0; i < list.Effects_Ability.Count; i++)
        {
            Effect_Ability ability = list.Effects_Ability[i];
            if (ability != null && ability.effectObj != null)
            {
                ShaderRemapper.RemapPrefabAsset(ability.effectObj);
            }
        }
    }

    private static void RemapEffectLabelObjs(Il2CppSystem.Collections.Generic.List<Effect_Label> labels)
    {
        if (labels == null)
        {
            return;
        }

        for (int i = 0; i < labels.Count; i++)
        {
            Effect_Label label = labels[i];
            if (label != null && label.effectObj != null)
            {
                ShaderRemapper.RemapPrefabAsset(label.effectObj);
            }
        }
    }

    private static bool TryValidateEntry(BundleOverrideEntry entry, out string error)
    {
        if (entry == null)
        {
            error = "entry is null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.label)
            || string.IsNullOrWhiteSpace(entry.resourceId)
            || string.IsNullOrWhiteSpace(entry.bundle)
            || string.IsNullOrWhiteSpace(entry.assetPath))
        {
            error = "label/resourceId/bundle/assetPath required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.assetType))
        {
            entry.assetType = "GameObject";
        }

        if (!entry.assetType.Equals("GameObject", StringComparison.OrdinalIgnoreCase)
            && !entry.assetType.Equals("Sprite", StringComparison.OrdinalIgnoreCase)
            && !IsBattleEffectListEntry(entry))
        {
            error = $"unsupported assetType={entry.assetType}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
