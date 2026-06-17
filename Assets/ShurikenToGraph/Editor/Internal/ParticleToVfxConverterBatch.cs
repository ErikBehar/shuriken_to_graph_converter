using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public enum ParticleToVfxBatchItemStatus
{
    Pending,
    Converting,
    Success,
    Failed,
    Skipped,
    AlreadyConverted
}

public sealed class ParticleToVfxBatchItem
{
    public string PrefabAssetPath;
    public string DisplayName;
    public int SystemCount;
    public bool Selected = true;
    public bool ShowReport;
    public ParticleToVfxBatchItemStatus Status = ParticleToVfxBatchItemStatus.Pending;
    public ParticleToVfxConversionResult Result;
}

public static class ParticleToVfxConverterBatch
{
    public static List<ParticleToVfxBatchItem> ScanFolder(string folderAssetPath)
    {
        var items = new List<ParticleToVfxBatchItem>();
        if (string.IsNullOrEmpty(folderAssetPath) || !AssetDatabase.IsValidFolder(folderAssetPath))
            return items;

        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folderAssetPath });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryCountParticleSystems(path, out var systemCount))
                continue;

            items.Add(new ParticleToVfxBatchItem
            {
                PrefabAssetPath = path,
                DisplayName = Path.GetFileNameWithoutExtension(path),
                SystemCount = systemCount,
                Selected = true,
                Status = ParticleToVfxBatchItemStatus.Pending
            });
        }

        items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    static bool TryCountParticleSystems(string prefabAssetPath, out int systemCount)
    {
        systemCount = 0;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
        if (prefab == null)
            return false;

        systemCount = prefab.GetComponentsInChildren<ParticleSystem>(true).Length;
        return systemCount > 0;
    }

    public static string NormalizeFolderAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        path = path.Replace('\\', '/').Trim();
        if (!path.StartsWith("Assets/", StringComparison.Ordinal) && path != "Assets")
            return string.Empty;

        return AssetDatabase.IsValidFolder(path) ? path : string.Empty;
    }

    public static string PickFolderPanel(string currentFolder)
    {
        var start = string.IsNullOrEmpty(currentFolder) ? "Assets" : currentFolder;
        var absolute = Application.dataPath;
        if (start.StartsWith("Assets", StringComparison.Ordinal))
        {
            var relative = start == "Assets" ? string.Empty : start["Assets".Length..].TrimStart('/');
            absolute = string.IsNullOrEmpty(relative)
                ? Application.dataPath
                : Path.Combine(Application.dataPath, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        var picked = EditorUtility.OpenFolderPanel("Select Particle Effect Folder", absolute, string.Empty);
        if (string.IsNullOrEmpty(picked))
            return null;

        picked = picked.Replace('\\', '/');
        var dataPath = Application.dataPath.Replace('\\', '/');
        if (!picked.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            return null;

        return "Assets" + picked[dataPath.Length..];
    }
}
