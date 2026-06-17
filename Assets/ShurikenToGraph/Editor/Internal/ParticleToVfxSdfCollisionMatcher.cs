using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ParticleToVfxSdfCollisionMatcher
{
    public static void ResolveForSnapshot(ParticleEffectSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.CollisionEnabled)
            return;

        if (snapshot.CollisionType != ParticleSystemCollisionType.World)
            return;

        foreach (var hint in snapshot.NearbyCollisionColliders)
        {
            var matchPath = FindBestSdfAssetPath(hint.ColliderName);
            if (!string.IsNullOrEmpty(matchPath))
                hint.SuggestedSdfAssetPath = matchPath;
        }

        if (snapshot.NearbyCollisionColliders.Count == 0)
            return;

        var names = string.Join(", ", snapshot.NearbyCollisionColliders.Select(hint => hint.ColliderName));
        snapshot.Warnings.Add(
            $"World collision: add a Collision Shape (Signed Distance Field) block and assign a baked Texture3D SDF. Nearby colliders: {names}.");

        var suggested = snapshot.NearbyCollisionColliders
            .Select(hint => hint.SuggestedSdfAssetPath)
            .FirstOrDefault(path => !string.IsNullOrEmpty(path));
        if (!string.IsNullOrEmpty(suggested))
            snapshot.Warnings.Add($"Found candidate SDF asset '{suggested}' for world collision.");
    }

    static string FindBestSdfAssetPath(string colliderName)
    {
        if (string.IsNullOrEmpty(colliderName))
            return null;

        var candidates = new List<(string path, int score)>();
        foreach (var path in FindTexture3DAssetPaths())
        {
            var score = ScoreMatch(path, colliderName);
            if (score > 0)
                candidates.Add((path, score));
        }

        return candidates
            .OrderByDescending(pair => pair.score)
            .ThenBy(pair => pair.path, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.path)
            .FirstOrDefault();
    }

    static IEnumerable<string> FindTexture3DAssetPaths()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture3D"))
            yield return AssetDatabase.GUIDToAssetPath(guid);
    }

    static int ScoreMatch(string assetPath, string colliderName)
    {
        var fileName = Path.GetFileNameWithoutExtension(assetPath);
        if (string.IsNullOrEmpty(fileName))
            return 0;

        if (!fileName.Contains("sdf", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains("distance", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(fileName, colliderName, StringComparison.OrdinalIgnoreCase))
            return 100;

        if (fileName.Contains(colliderName, StringComparison.OrdinalIgnoreCase))
            return 80;

        return 0;
    }
}
