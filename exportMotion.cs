using UnityEditor;
using System.IO;
using UnityEngine;

public class CreateAssetBundles
{
    [MenuItem("Assets/Build AssetBundles")]
    static void BuildAllAssetBundles()
    {
        // 1. First, process and sync all parallel audio files (.bytes)
        SyncParallelAudio();

        // 2. Prepare the directory
        string assetBundleDirectory = "Assets/AssetBundles";
        if (!Directory.Exists(assetBundleDirectory))
        {
            Directory.CreateDirectory(assetBundleDirectory);
        }

        // 3. Build for Windows 64-bit
        BuildPipeline.BuildAssetBundles(
            assetBundleDirectory,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64
        );

        Debug.Log("Asset Bundles with Parallel Audio built successfully in Assets/AssetBundles!");
    }

    static void SyncParallelAudio()
    {
        string[] audioExtensions = { "*.wav", "*.ogg", "*.mp3" };
        int count = 0;

        foreach (var ext in audioExtensions)
        {
            // Find all audio files in the project
            string[] files = Directory.GetFiles(Application.dataPath, ext, SearchOption.AllDirectories);
            foreach (string file in files)
            {
                // Convert to relative path (Assets/...)
                string relativePath = "Assets" + file.Replace(Application.dataPath, "").Replace('\\', '/');
                
                // Get the original asset's bundle info
                AssetImporter importer = AssetImporter.GetAtPath(relativePath);
                if (importer == null || string.IsNullOrEmpty(importer.assetBundleName)) continue;

                string bundleName = importer.assetBundleName;
                string bundleVariant = importer.assetBundleVariant;
                string bytesPath = relativePath + ".bytes";

                // Check if we need to update the .bytes copy (if missing or older than source)
                FileInfo sourceInfo = new FileInfo(file);
                FileInfo bytesInfo = new FileInfo(bytesPath);

                if (!bytesInfo.Exists || sourceInfo.LastWriteTime > bytesInfo.LastWriteTime)
                {
                    File.Copy(file, bytesPath, true);
                    AssetDatabase.ImportAsset(bytesPath); // Force Unity to recognize the new file
                    Debug.Log($"[Sync] Updated parallel bytes: {bytesPath}");
                }

                // Ensure the .bytes file is in the SAME AssetBundle as the original
                AssetImporter bytesImporter = AssetImporter.GetAtPath(bytesPath);
                if (bytesImporter != null && bytesImporter.assetBundleName != bundleName)
                {
                    bytesImporter.SetAssetBundleNameAndVariant(bundleName, bundleVariant);
                }
                
                count++;
            }
        }

        // Refresh database to make sure the build doesn't miss the new files
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Sync] Parallel audio sync complete. Processed {count} files.");
    }
}
