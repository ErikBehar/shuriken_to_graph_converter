using System;
using System.IO;
using UnityEngine;

public static class ParticleToVfxVisualValidationIO
{
    public static void Save(ParticleToVfxVisualValidationManifestFile manifest, string outputFolder)
    {
        if (manifest == null)
            return;

        var manifestPath = ParticleToVfxVisualValidationPaths.GetManifestPath(outputFolder);
        if (string.IsNullOrEmpty(manifestPath))
            return;

        SaveToPath(manifest, manifestPath);
    }

    public static void SaveToPath(ParticleToVfxVisualValidationManifestFile manifest, string manifestPath)
    {
        if (manifest == null || string.IsNullOrEmpty(manifestPath))
            return;

        var folder = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        manifest.GeneratedUtcTicks = DateTime.UtcNow.Ticks;
        File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
    }

    public static ParticleToVfxVisualValidationManifestFile Load(string outputFolder)
    {
        var manifestPath = ParticleToVfxVisualValidationPaths.GetManifestPath(outputFolder);
        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonUtility.FromJson<ParticleToVfxVisualValidationManifestFile>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load visual validation manifest: {ex.Message}");
            return null;
        }
    }
}
