using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

public static class ParticleToVfxAssetPaths
{
    public const string RootFolder = "Assets/ShurikenToGraph";
    public const string DefaultConvertedFolder = RootFolder + "/Converted";
    public const string SeedTemplateAssetPath = RootFolder + "/Templates/LightFlash.vfx";

    public static string MigrateLegacyAssetPath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return assetPath;

        assetPath = assetPath.Replace('\\', '/').Trim();
        if (assetPath.StartsWith("Assets/VFX/", StringComparison.Ordinal))
            return RootFolder + assetPath["Assets/VFX".Length..];
        if (string.Equals(assetPath, "Assets/VFX", StringComparison.Ordinal))
            return RootFolder;

        return assetPath;
    }

    public static string NormalizeOutputFolder(string outputFolder)
    {
        outputFolder = MigrateLegacyAssetPath(outputFolder);
        if (string.IsNullOrWhiteSpace(outputFolder))
            return DefaultConvertedFolder;

        outputFolder = outputFolder.Replace('\\', '/').Trim();
        if (!outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            return DefaultConvertedFolder;

        return outputFolder;
    }

    public static bool EnsureAssetFolderExists(string folderAssetPath)
    {
        folderAssetPath = NormalizeOutputFolder(folderAssetPath);
        if (AssetDatabase.IsValidFolder(folderAssetPath))
            return true;

        var parts = folderAssetPath.Split('/');
        if (parts.Length < 2 || !string.Equals(parts[0], "Assets", StringComparison.Ordinal))
            return false;

        var current = "Assets";
        for (var i = 1; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i]))
                continue;

            var next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }

        return AssetDatabase.IsValidFolder(folderAssetPath);
    }

    public static string GetProjectRoot()
    {
        return Directory.GetParent(Application.dataPath).FullName;
    }

    public static string ToAbsolutePath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static bool SeedTemplateExists()
    {
        return File.Exists(ToAbsolutePath(SeedTemplateAssetPath));
    }

    public static void EnsureDirectoryForAsset(string assetPath)
    {
        var directory = Path.GetDirectoryName(ToAbsolutePath(assetPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public static bool CopySeedTemplateTo(string destinationAssetPath, bool overwrite)
    {
        var source = ToAbsolutePath(SeedTemplateAssetPath);
        var destination = ToAbsolutePath(destinationAssetPath);
        if (!File.Exists(source))
            return false;

        EnsureDirectoryForAsset(destinationAssetPath);
        File.Copy(source, destination, overwrite);
        return true;
    }

    public static VisualEffectAsset ImportAndLoadVfxAsset(string assetPath)
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
        if (asset != null)
            return asset;

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.Default);
        asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
        if (asset != null)
            return asset;

        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
    }

    public static VisualEffectAsset CreateVfxAssetFromSeed(string assetPath)
    {
        if (!CopySeedTemplateTo(assetPath, overwrite: true))
            throw new FileNotFoundException($"VFX seed template not found at '{SeedTemplateAssetPath}'.");

        var asset = ImportAndLoadVfxAsset(assetPath);
        if (asset == null)
            throw new InvalidOperationException($"Failed to load created VFX asset at '{assetPath}'.");

        return asset;
    }
}
