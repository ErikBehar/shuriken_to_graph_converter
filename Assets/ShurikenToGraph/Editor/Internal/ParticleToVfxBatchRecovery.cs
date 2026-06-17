using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

[Serializable]
public sealed class ParticleToVfxBatchManifestEntry
{
    public string SourcePrefabPath;
    public string DisplayName;
    public string VfxAssetPath;
    public string PrefabPath;
    public string ReportPath;
    public long UtcTicks;
}

[Serializable]
public sealed class ParticleToVfxBatchManifestFile
{
    public string OutputFolder;
    public List<ParticleToVfxBatchManifestEntry> Entries = new();
}

public static class ParticleToVfxBatchRecovery
{
    const string ManifestFileName = "ParticleVfxBatchManifest.json";

    public static string GetManifestPath(string outputFolder)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        return string.IsNullOrEmpty(folder) ? null : $"{folder}/{ManifestFileName}";
    }

    public static void RestoreExistingConversions(IEnumerable<ParticleToVfxBatchItem> items, string outputFolder)
    {
        if (items == null)
            return;

        var manifest = LoadManifest(outputFolder);
        foreach (var item in items)
        {
            if (item.Status == ParticleToVfxBatchItemStatus.Success
                && item.Result != null
                && !item.Result.RecoveredFromDisk)
                continue;

            if (TryRestoreItem(item, outputFolder, manifest))
                item.Selected = false;
        }
    }

    public static bool TryRestoreItem(ParticleToVfxBatchItem item, string outputFolder)
    {
        return TryRestoreItem(item, outputFolder, LoadManifest(outputFolder));
    }

    static bool TryRestoreItem(
        ParticleToVfxBatchItem item,
        string outputFolder,
        ParticleToVfxBatchManifestFile manifest)
    {
        if (item == null)
            return false;

        manifest ??= LoadManifest(outputFolder);

        var entry = FindManifestEntry(manifest, item);
        if (entry != null && TryBuildResultFromPaths(item, entry.VfxAssetPath, entry.PrefabPath, entry.ReportPath, recoveredFromManifest: true))
            return true;

        return TryBuildResultFromName(item, outputFolder);
    }

    public static bool IsAlreadyConverted(ParticleToVfxBatchItem item, string outputFolder)
    {
        if (item?.Result != null && item.Result.Success)
            return true;

        var manifest = LoadManifest(outputFolder);
        var entry = FindManifestEntry(manifest, item);
        if (entry != null && AssetExists(entry.VfxAssetPath))
            return true;

        return FindVfxAssetPath(item.DisplayName, outputFolder) != null;
    }

    public static void RecordSuccessfulConversion(ParticleToVfxBatchItem item, string outputFolder)
    {
        if (item?.Result == null || !item.Result.Success)
            return;

        var manifest = LoadManifest(outputFolder) ?? new ParticleToVfxBatchManifestFile
        {
            OutputFolder = NormalizeOutputFolder(outputFolder),
            Entries = new List<ParticleToVfxBatchManifestEntry>()
        };

        manifest.OutputFolder = NormalizeOutputFolder(outputFolder);
        manifest.Entries.RemoveAll(entry =>
            string.Equals(entry.SourcePrefabPath, item.PrefabAssetPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase));

        manifest.Entries.Add(new ParticleToVfxBatchManifestEntry
        {
            SourcePrefabPath = item.PrefabAssetPath,
            DisplayName = item.DisplayName,
            VfxAssetPath = item.Result.VfxAssetPath,
            PrefabPath = item.Result.PrefabPath,
            ReportPath = item.Result.ReportPath,
            UtcTicks = DateTime.UtcNow.Ticks
        });

        SaveManifest(manifest, outputFolder);
    }

    static ParticleToVfxBatchManifestFile LoadManifest(string outputFolder)
    {
        var manifestPath = GetManifestPath(outputFolder);
        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonUtility.FromJson<ParticleToVfxBatchManifestFile>(json);
            MigrateManifestPaths(manifest);
            return manifest;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to read batch manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    static void SaveManifest(ParticleToVfxBatchManifestFile manifest, string outputFolder)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        if (string.IsNullOrEmpty(folder))
            return;

        Directory.CreateDirectory(folder);
        var manifestPath = $"{folder}/{ManifestFileName}";
        var json = JsonUtility.ToJson(manifest, true);
        File.WriteAllText(manifestPath, json);
        AssetDatabase.ImportAsset(manifestPath);
    }

    public static void RebuildManifestFromDisk(IEnumerable<ParticleToVfxBatchItem> items, string outputFolder)
    {
        if (items == null)
            return;

        var folder = NormalizeOutputFolder(outputFolder);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        var manifest = new ParticleToVfxBatchManifestFile
        {
            OutputFolder = folder,
            Entries = new List<ParticleToVfxBatchManifestEntry>()
        };

        foreach (var item in items)
        {
            if (item == null || string.IsNullOrEmpty(item.DisplayName))
                continue;

            var vfxPath = FindVfxAssetPath(item.DisplayName, folder);
            if (string.IsNullOrEmpty(vfxPath))
                continue;

            var safeName = ParticleToVfxConversionService.MakeSafeFileName(item.DisplayName);
            manifest.Entries.Add(new ParticleToVfxBatchManifestEntry
            {
                SourcePrefabPath = item.PrefabAssetPath,
                DisplayName = item.DisplayName,
                VfxAssetPath = vfxPath,
                PrefabPath = FindAssetPathByPrefix(folder, safeName, "_VFX", ".prefab"),
                ReportPath = FindAssetPathByPrefix(folder, safeName, "_VFX", "_ConversionReport.txt"),
                UtcTicks = DateTime.UtcNow.Ticks
            });
        }

        if (manifest.Entries.Count > 0)
            SaveManifest(manifest, outputFolder);
    }

    static ParticleToVfxBatchManifestEntry FindManifestEntry(
        ParticleToVfxBatchManifestFile manifest,
        ParticleToVfxBatchItem item)
    {
        if (manifest?.Entries == null || item == null)
            return null;

        return manifest.Entries.FirstOrDefault(entry =>
                   !string.IsNullOrEmpty(item.PrefabAssetPath)
                   && string.Equals(entry.SourcePrefabPath, item.PrefabAssetPath, StringComparison.OrdinalIgnoreCase))
               ?? manifest.Entries.FirstOrDefault(entry =>
                   string.Equals(entry.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    static bool TryBuildResultFromName(ParticleToVfxBatchItem item, string outputFolder)
    {
        var vfxPath = FindVfxAssetPath(item.DisplayName, outputFolder);
        if (string.IsNullOrEmpty(vfxPath))
            return false;

        var safeName = ParticleToVfxConversionService.MakeSafeFileName(item.DisplayName);
        var prefabPath = FindAssetPathByPrefix(outputFolder, safeName, "_VFX", ".prefab");
        var reportPath = FindAssetPathByPrefix(outputFolder, safeName, "_VFX", "_ConversionReport.txt");
        return TryBuildResultFromPaths(item, vfxPath, prefabPath, reportPath, recoveredFromManifest: false);
    }

    static bool TryBuildResultFromPaths(
        ParticleToVfxBatchItem item,
        string vfxPath,
        string prefabPath,
        string reportPath,
        bool recoveredFromManifest)
    {
        if (string.IsNullOrEmpty(vfxPath) || !AssetExists(vfxPath))
            return false;

        var result = new ParticleToVfxConversionResult
        {
            Success = true,
            SourceName = item.DisplayName,
            SourceAssetPath = item.PrefabAssetPath,
            VfxAssetPath = vfxPath,
            VfxAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(vfxPath),
            RecoveredFromDisk = true,
            RecoveredFromManifest = recoveredFromManifest
        };

        if (!string.IsNullOrEmpty(prefabPath) && AssetExists(prefabPath))
        {
            result.PrefabPath = prefabPath;
            result.VfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }

        if (!string.IsNullOrEmpty(reportPath) && File.Exists(reportPath))
        {
            result.ReportPath = reportPath;
            try
            {
                result.ReportLines.AddRange(File.ReadAllLines(reportPath));
            }
            catch (Exception ex)
            {
                result.ReportLines.Add($"Could not read report file: {ex.Message}");
            }
        }
        else
        {
            result.ReportLines.Add(recoveredFromManifest
                ? "Recovered from batch manifest after interruption."
                : "Recovered existing conversion output by effect name.");
            result.ReportLines.Add($"VFX: {vfxPath}");
            if (!string.IsNullOrEmpty(result.PrefabPath))
                result.ReportLines.Add($"Prefab: {result.PrefabPath}");
        }

        item.Result = result;
        item.Status = ParticleToVfxBatchItemStatus.AlreadyConverted;
        return true;
    }

    public static string FindVfxAssetPath(string displayName, string outputFolder)
    {
        return FindAssetPathByPrefix(outputFolder, ParticleToVfxConversionService.MakeSafeFileName(displayName), "_VFX", ".vfx");
    }

    static string FindAssetPathByPrefix(string outputFolder, string safeName, string middleToken, string extension)
    {
        var folder = NormalizeOutputFolder(outputFolder);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(safeName))
            return null;

        var exactPath = $"{folder}/{safeName}{middleToken}{extension}";
        if (AssetExists(exactPath))
            return exactPath;

        if (!Directory.Exists(folder))
            return null;

        var prefix = $"{safeName}{middleToken}";
        string bestPath = null;
        foreach (var file in Directory.GetFiles(folder, $"*{extension}", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file).Replace('\\', '/');
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var assetPath = $"{folder}/{fileName}";
            if (bestPath == null || string.Compare(fileName, Path.GetFileName(bestPath), StringComparison.OrdinalIgnoreCase) < 0)
                bestPath = assetPath;
        }

        return bestPath;
    }

    static bool AssetExists(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath) && AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
    }

    static void MigrateManifestPaths(ParticleToVfxBatchManifestFile manifest)
    {
        if (manifest == null)
            return;

        manifest.OutputFolder = NormalizeOutputFolder(manifest.OutputFolder);
        if (manifest.Entries == null)
            return;

        foreach (var entry in manifest.Entries)
        {
            entry.SourcePrefabPath = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(entry.SourcePrefabPath);
            entry.VfxAssetPath = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(entry.VfxAssetPath);
            entry.PrefabPath = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(entry.PrefabPath);
            entry.ReportPath = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(entry.ReportPath);
        }
    }

    static string NormalizeOutputFolder(string outputFolder)
    {
        return ParticleToVfxAssetPaths.NormalizeOutputFolder(outputFolder);
    }
}
