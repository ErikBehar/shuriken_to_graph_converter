using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ParticleToVfxVisualValidationManifest
{
    public const string ManifestFileName = ParticleToVfxVisualValidationPaths.ManifestFileName;
    public const string ValidationSubfolder = ParticleToVfxVisualValidationPaths.ValidationSubfolder;

    public static string GetValidationFolder(string outputFolder)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        return string.IsNullOrEmpty(folder) ? null : ParticleToVfxVisualValidationPaths.GetValidationFolder(folder);
    }

    public static string GetManifestPath(string outputFolder)
    {
        var validationFolder = GetValidationFolder(outputFolder);
        return string.IsNullOrEmpty(validationFolder) ? null : $"{validationFolder}/{ManifestFileName}";
    }

    public static string GetFamilyFolder(string outputFolder, string familyKey)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        if (string.IsNullOrEmpty(folder))
            return null;

        return ParticleToVfxVisualValidationPaths.GetFamilyFolder(folder, familyKey);
    }

    public static void Save(ParticleToVfxVisualValidationManifestFile manifest, string outputFolder)
    {
        if (manifest == null)
            return;

        var manifestPath = GetManifestPath(outputFolder);
        if (string.IsNullOrEmpty(manifestPath))
            return;

        ParticleToVfxVisualValidationIO.SaveToPath(manifest, manifestPath);
        AssetDatabase.ImportAsset(manifestPath);
    }

    public static ParticleToVfxVisualValidationManifestFile Load(string outputFolder)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        if (string.IsNullOrEmpty(folder))
            return null;

        return ParticleToVfxVisualValidationIO.Load(folder);
    }

    public static string StatusToString(ParticleToVfxVisualValidationStatus status)
    {
        return ParticleToVfxVisualValidationPaths.StatusToString(status);
    }

    static string NormalizeOutputFolder(string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
            return string.Empty;

        outputFolder = outputFolder.Replace('\\', '/').Trim();
        if (!outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            return string.Empty;

        return AssetDatabase.IsValidFolder(outputFolder) ? outputFolder : string.Empty;
    }
}
