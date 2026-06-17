using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ParticleToVfxShaderGraphMatcher
{
    const string ShaderGraphVfxAssetTypeName = "ShaderGraphVfxAsset";

    public static void ResolveForSnapshot(ParticleEffectSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.UsesCustomShader)
            return;

        var matchPath = FindBestShaderGraphAssetPath(snapshot.ShaderName, snapshot.MaterialName);
        if (string.IsNullOrEmpty(matchPath))
            return;

        snapshot.SuggestedShaderGraphPath = matchPath;
    }

    public static bool TryLoadShaderGraphVfxAsset(string assetPath, out UnityEngine.Object vfxShaderAsset)
    {
        vfxShaderAsset = null;
        if (string.IsNullOrEmpty(assetPath))
            return false;

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
        {
            if (asset == null || asset.GetType().Name != ShaderGraphVfxAssetTypeName)
                continue;

            vfxShaderAsset = asset;
            return true;
        }

        return false;
    }

    static string FindBestShaderGraphAssetPath(string shaderName, string materialName)
    {
        var candidates = new List<(string path, int score)>();
        foreach (var path in FindShaderGraphAssetPaths())
        {
            var score = ScoreMatch(path, shaderName, materialName);
            if (score > 0)
                candidates.Add((path, score));
        }

        return candidates
            .OrderByDescending(pair => pair.score)
            .ThenBy(pair => pair.path, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.path)
            .FirstOrDefault();
    }

    static IEnumerable<string> FindShaderGraphAssetPaths()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Shader"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    static int ScoreMatch(string shaderGraphPath, string shaderName, string materialName)
    {
        var fileName = Path.GetFileNameWithoutExtension(shaderGraphPath);
        var shaderLeaf = GetLeafName(shaderName);
        var materialLeaf = GetLeafName(materialName);

        if (string.IsNullOrEmpty(fileName))
            return 0;

        if (!string.IsNullOrEmpty(materialLeaf)
            && string.Equals(fileName, materialLeaf, StringComparison.OrdinalIgnoreCase))
            return 100;

        if (!string.IsNullOrEmpty(shaderLeaf)
            && string.Equals(fileName, shaderLeaf, StringComparison.OrdinalIgnoreCase))
            return 90;

        if (!string.IsNullOrEmpty(materialLeaf)
            && fileName.Contains(materialLeaf, StringComparison.OrdinalIgnoreCase))
            return 70;

        if (!string.IsNullOrEmpty(shaderLeaf)
            && fileName.Contains(shaderLeaf, StringComparison.OrdinalIgnoreCase))
            return 60;

        return 0;
    }

    static string GetLeafName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var leaf = value;
        var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
        if (slash >= 0 && slash < leaf.Length - 1)
            leaf = leaf[(slash + 1)..];

        if (leaf.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
            leaf = Path.GetFileNameWithoutExtension(leaf);

        return leaf;
    }
}
