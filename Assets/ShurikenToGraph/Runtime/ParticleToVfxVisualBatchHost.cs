using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.VFX;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class ParticleToVfxVisualBatchHost : MonoBehaviour
{
    const float SideSpacing = 6f;

    [SerializeField] List<ParticleToVfxVisualBatchTarget> targets = new();
    [SerializeField] string outputFolder;
    [SerializeField] float captureTimeSeconds = ParticleToVfxVisualBatchSession.DefaultCaptureTimeSeconds;
    [SerializeField] Camera compareCamera;
    [SerializeField] Camera shurikenCamera;
    [SerializeField] Camera vfxCamera;
    [SerializeField] GameObject compareRoot;
    [SerializeField] GameObject shurikenInstance;
    [SerializeField] GameObject vfxInstance;

    public void Configure(
        Camera compareCam,
        Camera shurikenCam,
        Camera vfxCam,
        GameObject root,
        GameObject shuriken,
        GameObject vfx,
        IReadOnlyList<ParticleToVfxVisualBatchTarget> batchTargets,
        string batchOutputFolder,
        float captureTime)
    {
        compareCamera = compareCam;
        shurikenCamera = shurikenCam;
        vfxCamera = vfxCam;
        compareRoot = root;
        shurikenInstance = shuriken;
        vfxInstance = vfx;
        outputFolder = batchOutputFolder;
        captureTimeSeconds = Mathf.Max(0.25f, captureTime);
        targets = new List<ParticleToVfxVisualBatchTarget>(batchTargets);
    }

    void Start()
    {
        StartCoroutine(RunBatch());
    }

    IEnumerator RunBatch()
    {
        var manifest = ParticleToVfxVisualValidationIO.Load(outputFolder)
            ?? new ParticleToVfxVisualValidationManifestFile
            {
                OutputFolder = outputFolder,
                ValidationFolder = ParticleToVfxVisualValidationPaths.GetValidationFolder(outputFolder),
                Entries = new List<ParticleToVfxVisualValidationEntry>()
            };

        for (var index = 0; index < targets.Count; index++)
        {
            if (ParticleToVfxVisualBatchSession.IsCancelRequested())
            {
                FinalizeManifest(manifest, cancelled: true);
                yield break;
            }

            var target = targets[index];
            ParticleToVfxVisualBatchSession.ReportStatus(
                $"Capturing {index + 1} of {targets.Count}: {target.DisplayName}",
                index,
                targets.Count);

            if (index > 0)
            {
                if (!SwapTarget(target))
                {
                    manifest.Entries.Add(CreateErrorEntry(target, "Failed to swap comparison instances."));
                    ParticleToVfxVisualValidationIO.Save(manifest, outputFolder);
                    continue;
                }
            }

            RestartEffects();
            yield return new WaitForSeconds(captureTimeSeconds);

            if (ParticleToVfxVisualBatchSession.IsCancelRequested())
            {
                FinalizeManifest(manifest, cancelled: true);
                yield break;
            }

            CaptureTarget(target, manifest);
            ParticleToVfxVisualValidationIO.Save(manifest, outputFolder);
            ParticleToVfxVisualBatchSession.ReportStatus(
                $"Captured {index + 1} of {targets.Count}: {target.DisplayName}",
                index + 1,
                targets.Count);
        }

        FinalizeManifest(manifest, cancelled: false);
    }

    bool SwapTarget(ParticleToVfxVisualBatchTarget target)
    {
#if UNITY_EDITOR
        if (compareRoot == null)
            return false;

        var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(target.PrefabAssetPath);
        if (sourcePrefab == null)
            return false;

        GameObject vfxPrefab = null;
        VisualEffectAsset vfxAsset = null;
        if (!string.IsNullOrEmpty(target.VfxPrefabPath))
            vfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(target.VfxPrefabPath);
        if (!string.IsNullOrEmpty(target.VfxAssetPath))
            vfxAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(target.VfxAssetPath);

        if (vfxPrefab == null && vfxAsset == null)
            return false;

        DestroyInstance(shurikenInstance);
        DestroyInstance(vfxInstance);

        shurikenInstance = Instantiate(sourcePrefab, compareRoot.transform);
        shurikenInstance.name = $"{sourcePrefab.name} (Shuriken)";
        shurikenInstance.transform.localPosition = new Vector3(-SideSpacing * 0.5f, 0f, 0f);

        if (vfxPrefab != null)
        {
            vfxInstance = Instantiate(vfxPrefab, compareRoot.transform);
            vfxInstance.name = $"{vfxPrefab.name} (VFX)";
            vfxInstance.transform.localPosition = new Vector3(SideSpacing * 0.5f, 0f, 0f);
        }
        else
        {
            vfxInstance = new GameObject($"{target.DisplayName}_VFX (VFX)");
            vfxInstance.transform.SetParent(compareRoot.transform, false);
            vfxInstance.transform.localPosition = new Vector3(SideSpacing * 0.5f, 0f, 0f);
            var visualEffect = vfxInstance.AddComponent<VisualEffect>();
            visualEffect.visualEffectAsset = vfxAsset;
        }

        ConfigureVisualEffectSeeds(vfxInstance, restart: false);
        return shurikenInstance != null && vfxInstance != null;
#else
        return false;
#endif
    }

    void RestartEffects()
    {
        if (shurikenInstance != null)
        {
            var systems = shurikenInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var system in systems)
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        if (vfxInstance != null)
            ConfigureVisualEffectSeeds(vfxInstance, restart: true);
    }

    static void ConfigureVisualEffectSeeds(GameObject vfxRoot, bool restart)
    {
        var effects = vfxRoot.GetComponentsInChildren<VisualEffect>(true);
        foreach (var effect in effects)
        {
            effect.startSeed = 0;
            effect.resetSeedOnPlay = true;
            if (restart)
            {
                effect.Reinit();
                effect.Play();
            }
        }
    }

    void CaptureTarget(ParticleToVfxVisualBatchTarget target, ParticleToVfxVisualValidationManifestFile manifest)
    {
        var familyFolderAsset = ParticleToVfxVisualValidationPaths.GetFamilyFolder(outputFolder, target.FamilyKey);
        var familyFolderAbsolute = Path.GetFullPath(familyFolderAsset);
        Directory.CreateDirectory(familyFolderAbsolute);

        var timeTag = captureTimeSeconds.ToString("0.0").Replace('.', 'p');
        var shurikenPath = Path.Combine(familyFolderAbsolute, $"shuriken_t{timeTag}.png");
        var vfxPath = Path.Combine(familyFolderAbsolute, $"vfx_t{timeTag}.png");
        var comparePath = Path.Combine(familyFolderAbsolute, $"compare_t{timeTag}.png");

        ParticleToVfxVisualCaptureUtility.CaptureCamera(
            shurikenCamera,
            shurikenPath,
            ParticleToVfxVisualBatchSession.CaptureWidth,
            ParticleToVfxVisualBatchSession.CaptureHeight);
        ParticleToVfxVisualCaptureUtility.CaptureCamera(
            vfxCamera,
            vfxPath,
            ParticleToVfxVisualBatchSession.CaptureWidth,
            ParticleToVfxVisualBatchSession.CaptureHeight);
        ParticleToVfxVisualCaptureUtility.CaptureCamera(
            compareCamera,
            comparePath,
            ParticleToVfxVisualBatchSession.CaptureWidth * 2,
            ParticleToVfxVisualBatchSession.CaptureHeight);

        var diffScore = ParticleToVfxVisualCaptureUtility.ComputeNormalizedDiff(shurikenPath, vfxPath);
        var shurikenAlive = CountShurikenParticles(shurikenInstance);
        var vfxAlive = CountVfxParticles(vfxInstance);
        var status = ParticleToVfxVisualCaptureUtility.ClassifyResult(diffScore, shurikenAlive, vfxAlive);

        manifest.Entries.Add(new ParticleToVfxVisualValidationEntry
        {
            FamilyKey = target.FamilyKey,
            DisplayName = target.DisplayName,
            SourcePrefabPath = target.PrefabAssetPath,
            VfxPrefabPath = target.VfxPrefabPath,
            VfxAssetPath = target.VfxAssetPath,
            VariantCount = target.VariantCount,
            Status = ParticleToVfxVisualValidationPaths.StatusToString(status),
            DiffScore = diffScore,
            ShurikenAliveCount = shurikenAlive,
            VfxAliveCount = vfxAlive,
            CaptureTimeSeconds = captureTimeSeconds,
            CompareImagePath = ToAssetPath(comparePath),
            ShurikenImagePath = ToAssetPath(shurikenPath),
            VfxImagePath = ToAssetPath(vfxPath),
            Notes = BuildNotes(status, shurikenAlive, vfxAlive)
        });
    }

    static ParticleToVfxVisualValidationEntry CreateErrorEntry(ParticleToVfxVisualBatchTarget target, string message)
    {
        return new ParticleToVfxVisualValidationEntry
        {
            FamilyKey = target.FamilyKey,
            DisplayName = target.DisplayName,
            SourcePrefabPath = target.PrefabAssetPath,
            VfxPrefabPath = target.VfxPrefabPath,
            VfxAssetPath = target.VfxAssetPath,
            VariantCount = target.VariantCount,
            Status = ParticleToVfxVisualValidationPaths.StatusToString(ParticleToVfxVisualValidationStatus.Error),
            Notes = message
        };
    }

    void FinalizeManifest(ParticleToVfxVisualValidationManifestFile manifest, bool cancelled)
    {
        manifest.TotalFamilies = manifest.Entries.Count;
        manifest.PassedCount = CountByStatus(manifest, "Passed");
        manifest.ReviewCount = CountByStatus(manifest, "Review");
        manifest.FailedCount = CountByStatus(manifest, "Failed");
        manifest.ErrorCount = CountByStatus(manifest, "Error");

#if UNITY_EDITOR
        ParticleToVfxVisualValidationIO.Save(manifest, outputFolder);
        ParticleToVfxVisualBatchSession.MarkComplete(
            cancelled
                ? "Visual validation cancelled."
                : $"Visual validation complete: {manifest.PassedCount} passed, {manifest.ReviewCount} review, {manifest.FailedCount} failed, {manifest.ErrorCount} errors across {targets.Count} family representative(s).");
        EditorApplication.isPlaying = false;
#endif
    }

    static int CountShurikenParticles(GameObject root)
    {
        if (root == null)
            return 0;

        var total = 0;
        var systems = root.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var system in systems)
            total += system.particleCount;

        return total;
    }

    static int CountVfxParticles(GameObject root)
    {
        if (root == null)
            return 0;

        var total = 0;
        var effects = root.GetComponentsInChildren<VisualEffect>(true);
        foreach (var effect in effects)
            total += effect.aliveParticleCount;

        return total;
    }

    static void DestroyInstance(GameObject instance)
    {
        if (instance == null)
            return;

        Destroy(instance);
    }

    static int CountByStatus(ParticleToVfxVisualValidationManifestFile manifest, string status)
    {
        var count = 0;
        foreach (var entry in manifest.Entries)
        {
            if (entry.Status == status)
                count++;
        }

        return count;
    }

    static string BuildNotes(
        ParticleToVfxVisualValidationStatus status,
        int shurikenAlive,
        int vfxAlive)
    {
        if (vfxAlive <= 0 && shurikenAlive > 0)
            return "VFX reported zero alive particles while Shuriken still has particles.";

        return status switch
        {
            ParticleToVfxVisualValidationStatus.Passed => "Within visual tolerance for the representative family capture.",
            ParticleToVfxVisualValidationStatus.Review => "Moderate visual difference; inspect compare image.",
            ParticleToVfxVisualValidationStatus.Failed => "Large visual difference or missing VFX output.",
            _ => string.Empty
        };
    }

    static string ToAssetPath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return string.Empty;

        var dataPath = Path.GetFullPath(Application.dataPath);
        var fullPath = Path.GetFullPath(absolutePath);
        if (!fullPath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
            return absolutePath.Replace('\\', '/');

        var relative = fullPath.Substring(dataPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"Assets/{relative.Replace('\\', '/')}";
    }
}
