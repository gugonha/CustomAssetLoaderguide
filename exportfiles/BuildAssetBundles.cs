using UnityEditor;
using System.IO;

public static class BuildAssetBundles
{
    [MenuItem("Build/AssetBundles/Windows64")]
    public static void Build()
    {
        string outDir = "Build/AssetBundles/Windows";
        Directory.CreateDirectory(outDir);

        BuildPipeline.BuildAssetBundles(
            outDir,
            BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);

        AssetDatabase.Refresh();
    }
}
