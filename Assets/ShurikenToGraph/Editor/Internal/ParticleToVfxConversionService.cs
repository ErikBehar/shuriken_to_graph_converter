using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

public sealed class ParticleToVfxConversionResult
{
    public bool Success;
    public string ErrorMessage;
    public string SourceName;
    public string SourceAssetPath;
    public string VfxAssetPath;
    public string PrefabPath;
    public string ReportPath;
    public int SystemCount;
    public readonly List<string> ReportLines = new();
    public VisualEffectAsset VfxAsset;
    public GameObject VfxPrefab;
    public GameObject SourcePrefab;
    public bool RecoveredFromDisk;
    public bool RecoveredFromManifest;
}

public static class ParticleToVfxConversionService
{
    public const string DefaultOutputFolder = ParticleToVfxAssetPaths.DefaultConvertedFolder;

    public static bool TryAnalyzeSource(GameObject source, out ParticleEffectHierarchySnapshot hierarchy)
    {
        hierarchy = null;
        if (source == null)
            return false;

        var assetPath = AssetDatabase.GetAssetPath(source);
        var isPrefabAsset = !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        if (isPrefabAsset)
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                if (root.GetComponentsInChildren<ParticleSystem>(true).Length == 0)
                    return false;

                // The prefab is loaded into an isolated preview scene, so scanning the
                // scene for colliders/wind zones would hit unrelated objects.
                hierarchy = ParticleSystemAnalyzer.AnalyzeHierarchy(root, allowSceneQueries: false);
                return hierarchy.Systems.Count > 0;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        if (source.GetComponentsInChildren<ParticleSystem>(true).Length == 0)
            return false;

        hierarchy = ParticleSystemAnalyzer.AnalyzeHierarchy(source);
        return hierarchy.Systems.Count > 0;
    }

    public static bool TryAnalyzePrefabAsset(string prefabAssetPath, out ParticleEffectHierarchySnapshot hierarchy)
    {
        hierarchy = null;
        if (string.IsNullOrEmpty(prefabAssetPath))
            return false;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
        return prefab != null && TryAnalyzeSource(prefab, out hierarchy);
    }

    public static ParticleToVfxConversionResult ConvertSource(
        GameObject source,
        string outputFolder,
        bool createPrefab)
    {
        var result = new ParticleToVfxConversionResult
        {
            SourceAssetPath = AssetDatabase.GetAssetPath(source)
        };

        try
        {
            if (!TryAnalyzeSource(source, out var hierarchy))
            {
                result.ErrorMessage = "No ParticleSystem found on the source or its children.";
                return result;
            }

            return ConvertHierarchy(source, hierarchy, outputFolder, createPrefab);
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Debug.LogException(ex);
            return result;
        }
    }

    public static ParticleToVfxConversionResult ConvertPrefabAsset(
        string prefabAssetPath,
        string outputFolder,
        bool createPrefab)
    {
        Debug.Log($"[ParticleToVfx] Converting '{prefabAssetPath}' -> '{outputFolder}'");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
        if (prefab == null)
        {
            return new ParticleToVfxConversionResult
            {
                SourceAssetPath = prefabAssetPath,
                ErrorMessage = $"Prefab not found at '{prefabAssetPath}'."
            };
        }

        var result = ConvertSource(prefab, outputFolder, createPrefab);
        result.SourceAssetPath = prefabAssetPath;
        result.SourcePrefab = prefab;
        return result;
    }

    static ParticleToVfxConversionResult ConvertHierarchy(
        GameObject source,
        ParticleEffectHierarchySnapshot hierarchy,
        string outputFolder,
        bool createPrefab)
    {
        var result = new ParticleToVfxConversionResult
        {
            SourceName = hierarchy.RootName,
            SourceAssetPath = AssetDatabase.GetAssetPath(source),
            SourcePrefab = source,
            SystemCount = hierarchy.Systems.Count
        };

        BuildReportLines(hierarchy, result.ReportLines);

        if (hierarchy.Systems.Count > ParticleToVfxConversionContext.MaxSystemsPerEffect)
        {
            result.ErrorMessage =
                $"Effect has {hierarchy.Systems.Count} particle systems (limit {ParticleToVfxConversionContext.MaxSystemsPerEffect}). Skipped to avoid editor instability.";
            result.SystemCount = hierarchy.Systems.Count;
            return result;
        }

        ParticleToVfxOutputCleaner.CleanEffectOutput(outputFolder, hierarchy.RootName);

        var asset = ParticleToVfxGraphBuilder.Build(hierarchy, outputFolder, out var assetPath);
        result.VfxAsset = asset;
        result.VfxAssetPath = assetPath;
        result.ReportLines.Add($"Created VFX Graph: {assetPath}");

        if (hierarchy.SubgraphAssetPaths.Count > 0)
        {
            result.ReportLines.Add($"Created {hierarchy.SubgraphAssetPaths.Count} subgraph asset(s):");
            foreach (var subgraphPath in hierarchy.SubgraphAssetPaths)
                result.ReportLines.Add($"  {subgraphPath}");
        }

        AppendWarnings(hierarchy, result.ReportLines);

        if (createPrefab)
        {
            result.VfxPrefab = CreateResultPrefab(asset, hierarchy, assetPath);
            result.PrefabPath = AssetDatabase.GetAssetPath(result.VfxPrefab);
            result.ReportLines.Add($"Created prefab: {result.PrefabPath}");
        }

        PersistConvertedAssets(assetPath, hierarchy, result.PrefabPath);

        result.ReportPath = ParticleToVfxConversionReportWriter.Write(assetPath, result.ReportLines);
        if (!string.IsNullOrEmpty(result.ReportPath))
            result.ReportLines.Add($"Wrote conversion report: {result.ReportPath}");

        result.Success = true;
        return result;
    }

    public static void BuildReportLines(
        ParticleEffectHierarchySnapshot hierarchy,
        List<string> report)
    {
        report.Add($"Analyzed '{hierarchy.RootName}' with {hierarchy.Systems.Count} particle system(s).");
        foreach (var system in hierarchy.Systems)
        {
            report.Add($"  [{system.SystemIndex}] {system.HierarchyPath}");
            report.Add(
                $"      Emission: {system.EmissionRate:0.##}/s{(system.RateOverTime.UsesCurve ? " (curve)" : "")}, Bursts: {system.Bursts.Count}, Lifetime: {DescribeMinMax(system.StartLifetime)}, Size: {DescribeMinMax(system.StartSize)}");
            report.Add(
                $"      Shape: {(system.ShapeEnabled ? system.ShapeType.ToString() : "disabled")}, Noise: {(system.NoiseEnabled ? "on" : "off")}, Sub-emitters: {system.SubEmitters.Count}");
            report.Add(
                $"      Flipbook: {(system.TextureSheetEnabled ? $"{system.TextureSheet.TilesX}x{system.TextureSheet.TilesY}" : "off")}, Trails: {(system.TrailsEnabled ? "on" : "off")}, Collision: {(system.CollisionEnabled ? $"{system.CollisionType} ({system.CollisionPlanes.Count} planes)" : "off")}");
            report.Add(
                $"      External Forces: {(system.ExternalForcesEnabled ? (system.HasSceneWindZone ? system.SceneWindZoneName : "no Wind Zone") : "off")}, Inherit Velocity: {(system.InheritVelocityEnabled ? "on" : "off")}");
            report.Add(
                $"      Mesh: {(system.RenderMesh != null ? system.RenderMesh.name : "none")}, Rate/Dist: {(system.RateOverDistance > 0.001f ? system.RateOverDistance.ToString("0.##") : "off")}");
            report.Add(
                $"      Texture: {(system.MainTexture != null ? system.MainTexture.name : "default particle")}, Shader: {(string.IsNullOrEmpty(system.ShaderName) ? "built-in/unknown" : system.ShaderName)}");
            report.Add(
                $"      Render: shadows={(system.CastShadows ? "on" : "off")}, sort={system.SortingOrder}, alphaClip={(system.UseAlphaClipping ? system.AlphaCutoff.ToString("0.##") : "off")}, twoSided={(system.UseDoubleSided ? "on" : "off")}");

            if (system.StartSize3D)
                report.Add("      Size: 3D start size (approximated as uniform)");
            if (system.CollisionEnabled && system.CollisionType == ParticleSystemCollisionType.World
                && system.NearbyCollisionColliders.Count > 0)
            {
                report.Add(
                    $"      World collision colliders: {string.Join(", ", system.NearbyCollisionColliders.ConvertAll(hint => hint.ColliderName))}");
                foreach (var hint in system.NearbyCollisionColliders)
                {
                    if (!string.IsNullOrEmpty(hint.SuggestedSdfAssetPath))
                        report.Add($"      Candidate SDF: {hint.SuggestedSdfAssetPath}");
                }
            }

            if (system.UsesCustomShader)
            {
                if (!string.IsNullOrEmpty(system.SuggestedShaderGraphPath))
                    report.Add($"      Custom shader → candidate Shader Graph: {system.SuggestedShaderGraphPath}");
                else
                    report.Add("      Custom shader requires manual VFX Shader Graph assignment.");
            }

            if (system.CustomDataEnabled)
                report.Add($"      Custom Data: {system.CustomDataChannelCount} channel(s) not converted");
            if (system.TriggerEnabled)
                report.Add(
                    $"      Trigger: {system.TriggerColliderCount} collider(s){(system.TriggerColliderNames.Count > 0 ? $" [{string.Join(", ", system.TriggerColliderNames)}]" : "")}");
        }

        report.Add(
            $"Prefab playRate: {hierarchy.PlaybackSpeed:0.##}, blackboard Play Rate exposed on graph, sortingOrder: {hierarchy.SortingOrder}, sortingLayer: {hierarchy.SortingLayerName ?? "Default"}");
    }

    static void AppendWarnings(ParticleEffectHierarchySnapshot hierarchy, List<string> report)
    {
        foreach (var warning in hierarchy.Warnings)
            report.Add($"Warning: {warning}");
        foreach (var system in hierarchy.Systems)
        {
            foreach (var warning in system.Warnings)
                report.Add($"[{system.SourceName}] Warning: {warning}");
            foreach (var unsupported in system.UnsupportedFeatures)
                report.Add($"[{system.SourceName}] Unsupported: {unsupported}");
        }
    }

    static GameObject CreateResultPrefab(
        VisualEffectAsset asset,
        ParticleEffectHierarchySnapshot hierarchy,
        string vfxAssetPath)
    {
        var prefabFolder = GetPrefabFolder(vfxAssetPath);
        Directory.CreateDirectory(prefabFolder);

        var prefabPath = AssetDatabase.GenerateUniqueAssetPath(
            $"{prefabFolder}/{MakeSafeFileName(hierarchy.RootName)}_VFX.prefab");

        var root = new GameObject(hierarchy.RootName + "_VFX");
        try
        {
            root.transform.localPosition = hierarchy.RootLocalPosition;
            root.transform.localEulerAngles = hierarchy.RootLocalRotation;
            root.transform.localScale = hierarchy.RootLocalScale;

            var visualEffect = root.AddComponent<VisualEffect>();
            visualEffect.visualEffectAsset = asset;
            visualEffect.startSeed = 0;
            visualEffect.resetSeedOnPlay = true;
            visualEffect.playRate = Mathf.Max(hierarchy.PlaybackSpeed, 0.01f);

            var renderer = root.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sortingOrder = hierarchy.SortingOrder;
                renderer.shadowCastingMode = hierarchy.ShadowCastingMode;
                renderer.receiveShadows = hierarchy.ReceiveShadows;
                if (!string.IsNullOrEmpty(hierarchy.SortingLayerName))
                    renderer.sortingLayerID = SortingLayer.NameToID(hierarchy.SortingLayerName);
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    public static string GetPrefabFolder(string vfxAssetPath)
    {
        var dir = Path.GetDirectoryName(vfxAssetPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(dir) ? DefaultOutputFolder : dir;
    }

    public static string DescribeMinMax(MinMaxSnapshot value)
    {
        return value.IsRandom ? $"{value.Min:0.###} - {value.Max:0.###}" : $"{value.Constant:0.###}";
    }

    static void PersistConvertedAssets(
        string vfxAssetPath,
        ParticleEffectHierarchySnapshot hierarchy,
        string prefabPath)
    {
        AssetDatabase.SaveAssets();

        ImportAssetIfExists(vfxAssetPath);
        if (hierarchy?.SubgraphAssetPaths != null)
        {
            foreach (var subgraphPath in hierarchy.SubgraphAssetPaths)
                ImportAssetIfExists(subgraphPath);
        }

        ImportAssetIfExists(prefabPath);
    }

    static void ImportAssetIfExists(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return;

        if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null
            || System.IO.File.Exists(ParticleToVfxAssetPaths.ToAbsolutePath(assetPath)))
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.Default);
    }

    public static string MakeSafeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "ConvertedParticle" : name.Trim();
    }
}
