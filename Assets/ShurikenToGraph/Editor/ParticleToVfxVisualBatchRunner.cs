using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class ParticleToVfxVisualBatchRunner
{
    public const float DefaultCaptureTimeSeconds = ParticleToVfxVisualBatchSession.DefaultCaptureTimeSeconds;
    public const int CaptureWidth = ParticleToVfxVisualBatchSession.CaptureWidth;
    public const int CaptureHeight = ParticleToVfxVisualBatchSession.CaptureHeight;

    static Action<string> s_OnStatusChanged;
    static Action s_OnCompleted;

    public static bool IsRunning => SessionState.GetBool(ParticleToVfxVisualBatchSession.RunningKey, false);

    static ParticleToVfxVisualBatchRunner()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update += PollSessionStatus;
    }

    public static void RegisterCallbacks(Action<string> onStatusChanged, Action onCompleted)
    {
        if (onStatusChanged != null)
            s_OnStatusChanged = onStatusChanged;
        if (onCompleted != null)
            s_OnCompleted = onCompleted;

        if (IsRunning)
            s_OnStatusChanged?.Invoke(SessionState.GetString(ParticleToVfxVisualBatchSession.StatusKey, "Running visual validation..."));
    }

    public static void Begin(
        IEnumerable<ParticleToVfxBatchItem> allItems,
        string outputFolder,
        float captureTimeSeconds,
        Action<string> onStatusChanged,
        Action onCompleted)
    {
        if (IsRunning)
            return;

        if (EditorApplication.isPlaying)
        {
            onStatusChanged?.Invoke("Exit Play Mode before starting visual validation.");
            return;
        }

        var targets = ParticleToVfxSpellFamilyKey.SelectFamilyRepresentatives(allItems);
        if (targets.Count == 0)
        {
            onStatusChanged?.Invoke("No converted effects available for visual validation.");
            return;
        }

        var familyVariantCounts = BuildFamilyVariantCounts(allItems);
        s_OnStatusChanged = onStatusChanged;
        s_OnCompleted = onCompleted;

        var validationFolder = ParticleToVfxVisualValidationManifest.GetValidationFolder(outputFolder);
        if (!string.IsNullOrEmpty(validationFolder))
            Directory.CreateDirectory(validationFolder);

        var manifest = new ParticleToVfxVisualValidationManifestFile
        {
            OutputFolder = outputFolder,
            ValidationFolder = validationFolder,
            Entries = new List<ParticleToVfxVisualValidationEntry>()
        };
        ParticleToVfxVisualValidationManifest.Save(manifest, outputFolder);

        var firstItem = targets[0];
        ReportStatus($"Preparing 1 of {targets.Count}: {firstItem.DisplayName}", 0, targets.Count);

        ParticleToVfxComparisonSceneBuilder.CloseScenesNamed("VFX Visual Validation (Temp)");
        var context = ParticleToVfxComparisonSceneBuilder.BuildComparisonScene(firstItem);
        if (context == null)
        {
            FinishImmediately("Visual validation failed while building the comparison scene.");
            return;
        }

        var batchTargets = new List<ParticleToVfxVisualBatchTarget>(targets.Count);
        foreach (var item in targets)
        {
            familyVariantCounts.TryGetValue(
                ParticleToVfxSpellFamilyKey.GetFamilyKey(item.DisplayName),
                out var variantCount);
            batchTargets.Add(new ParticleToVfxVisualBatchTarget
            {
                DisplayName = item.DisplayName,
                PrefabAssetPath = item.PrefabAssetPath,
                VfxPrefabPath = item.Result?.PrefabPath,
                VfxAssetPath = item.Result?.VfxAssetPath,
                FamilyKey = ParticleToVfxSpellFamilyKey.GetFamilyKey(item.DisplayName),
                VariantCount = Mathf.Max(1, variantCount)
            });
        }

        var hostObject = new GameObject("VisualBatchHost");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(hostObject, context.Scene);
        var host = hostObject.AddComponent<ParticleToVfxVisualBatchHost>();
        if (host == null)
        {
            ParticleToVfxComparisonSceneBuilder.DestroyContext(context);
            FinishImmediately("Visual validation failed: VisualBatchHost script could not be attached. Ensure VFX.Converter.Runtime compiled successfully.");
            return;
        }

        host.Configure(
            context.CompareCamera,
            context.ShurikenCamera,
            context.VfxCamera,
            context.Root,
            context.ShurikenInstance,
            context.VfxInstance,
            batchTargets,
            outputFolder,
            captureTimeSeconds);

        if (!ParticleToVfxComparisonSceneBuilder.SaveValidationScene(context))
        {
            ParticleToVfxComparisonSceneBuilder.DestroyContext(context);
            FinishImmediately("Visual validation failed while saving the comparison scene.");
            return;
        }

        SessionState.SetBool(ParticleToVfxVisualBatchSession.CancelKey, false);
        SessionState.SetBool(ParticleToVfxVisualBatchSession.PendingCompleteKey, false);
        SessionState.SetString(ParticleToVfxVisualBatchSession.OutputFolderKey, outputFolder);
        SessionState.SetBool(ParticleToVfxVisualBatchSession.RunningKey, true);
        SessionState.SetInt(ParticleToVfxVisualBatchSession.TotalKey, targets.Count);
        SessionState.SetInt(ParticleToVfxVisualBatchSession.IndexKey, 0);

        var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(
            ParticleToVfxComparisonSceneBuilder.ValidationSceneAssetPath);
        if (sceneAsset == null)
        {
            ParticleToVfxComparisonSceneBuilder.DestroyContext(context);
            FinishImmediately("Visual validation failed: saved scene asset is missing.");
            return;
        }

        EditorSceneManager.playModeStartScene = sceneAsset;
        ReportStatus($"Entering Play Mode for {targets.Count} family representative(s)...", 0, targets.Count);
        EditorApplication.isPlaying = true;
    }

    public static void Cancel()
    {
        if (!IsRunning)
            return;

        SessionState.SetBool(ParticleToVfxVisualBatchSession.CancelKey, true);
        ReportStatus(
            "Cancelling visual validation...",
            SessionState.GetInt(ParticleToVfxVisualBatchSession.IndexKey, 0),
            SessionState.GetInt(ParticleToVfxVisualBatchSession.TotalKey, 1));

        if (EditorApplication.isPlaying)
            return;

        CompleteSession("Visual validation cancelled.");
    }

    static void ReportStatus(string message, int index, int total)
    {
        ParticleToVfxVisualBatchSession.ReportStatus(message, index, total);
        s_OnStatusChanged?.Invoke(message);
    }

    static void PollSessionStatus()
    {
        if (!IsRunning)
            return;

        var message = SessionState.GetString(ParticleToVfxVisualBatchSession.StatusKey, "Running visual validation...");
        s_OnStatusChanged?.Invoke(message);
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
            return;

        if (!SessionState.GetBool(ParticleToVfxVisualBatchSession.PendingCompleteKey, false)
            && !SessionState.GetBool(ParticleToVfxVisualBatchSession.CancelKey, false))
            return;

        var message = SessionState.GetString(ParticleToVfxVisualBatchSession.StatusKey, "Visual validation finished.");
        CompleteSession(message);
    }

    static void CompleteSession(string message)
    {
        EditorSceneManager.playModeStartScene = null;
        ParticleToVfxComparisonSceneBuilder.CloseScenesNamed("VFX Visual Validation (Temp)");

        SessionState.EraseBool(ParticleToVfxVisualBatchSession.RunningKey);
        SessionState.EraseBool(ParticleToVfxVisualBatchSession.CancelKey);
        SessionState.EraseBool(ParticleToVfxVisualBatchSession.PendingCompleteKey);
        SessionState.EraseString(ParticleToVfxVisualBatchSession.StatusKey);
        SessionState.EraseInt(ParticleToVfxVisualBatchSession.IndexKey);
        SessionState.EraseInt(ParticleToVfxVisualBatchSession.TotalKey);
        SessionState.EraseString(ParticleToVfxVisualBatchSession.OutputFolderKey);

        EditorUtility.ClearProgressBar();
        s_OnStatusChanged?.Invoke(message);
        s_OnCompleted?.Invoke();
        s_OnStatusChanged = null;
        s_OnCompleted = null;

        AssetDatabase.Refresh();
    }

    static void FinishImmediately(string message)
    {
        SessionState.EraseBool(ParticleToVfxVisualBatchSession.RunningKey);
        EditorSceneManager.playModeStartScene = null;
        EditorUtility.ClearProgressBar();
        s_OnStatusChanged?.Invoke(message);
        s_OnCompleted?.Invoke();
        s_OnStatusChanged = null;
        s_OnCompleted = null;
    }

    static Dictionary<string, int> BuildFamilyVariantCounts(IEnumerable<ParticleToVfxBatchItem> allItems)
    {
        return allItems
            .Where(ParticleToVfxSpellFamilyKey.CanValidateItem)
            .GroupBy(item => ParticleToVfxSpellFamilyKey.GetFamilyKey(item.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }
}
