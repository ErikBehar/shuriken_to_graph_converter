using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;

public sealed class ParticleToVfxComparisonSceneContext
{
    public Scene Scene;
    public Camera CompareCamera;
    public Camera ShurikenCamera;
    public Camera VfxCamera;
    public GameObject Root;
    public GameObject ShurikenInstance;
    public GameObject VfxInstance;
    public string Label;
}

public static class ParticleToVfxComparisonSceneBuilder
{
    const float SideSpacing = 6f;
    const string TempSceneName = "VFX Compare (Temp)";
    const string ValidationSceneName = "VFX Visual Validation (Temp)";
    const string EmptyBaseScenePath = ParticleToVfxAssetPaths.RootFolder + "/Editor/Scenes/EmptyComparisonBase.unity";

    public static bool OpenSideBySideComparison(ParticleToVfxBatchItem item)
    {
        return OpenSideBySideComparison(item, null);
    }

    public static bool OpenSideBySideComparison(
        ParticleToVfxConversionResult result,
        GameObject sourceObject)
    {
        if (result == null || !result.Success || sourceObject == null)
            return false;

        var item = new ParticleToVfxBatchItem
        {
            PrefabAssetPath = IsPrefabAssetPath(result.SourceAssetPath) ? result.SourceAssetPath : null,
            DisplayName = result.SourceName,
            SystemCount = result.SystemCount,
            Result = result,
            Status = ParticleToVfxBatchItemStatus.Success
        };

        return OpenSideBySideComparison(item, sourceObject);
    }

    static bool OpenSideBySideComparison(ParticleToVfxBatchItem item, GameObject sourceOverride)
    {
        // Reset to a clean base scene first so any previous compare scene (and its heavy
        // contents) is unloaded quickly, making repeated Compare clicks fast.
        LoadEmptyBaseScene();

        var context = BuildComparisonScene(item, TempSceneName, sourceOverride);
        if (context == null)
            return false;

        EditorSceneManager.SetActiveScene(context.Scene);
        FrameComparisonView(context.CompareCamera);
        return true;
    }

    static void LoadEmptyBaseScene()
    {
        // In play mode the open scene cannot be swapped, so leave the setup alone and let
        // BuildComparisonScene close the prior temp compare scene by name instead.
        if (EditorApplication.isPlaying)
            return;

        EnsureVfxFolderExists();

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(EmptyBaseScenePath) != null
            && IsSceneAssetEmpty(EmptyBaseScenePath))
        {
            EditorSceneManager.OpenScene(EmptyBaseScenePath, OpenSceneMode.Single);
            return;
        }

        // Missing, contains objects, or is not a clean empty scene — create/overwrite.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "EmptyComparisonBase";
        EditorSceneManager.SaveScene(scene, EmptyBaseScenePath);
    }

    static bool IsSceneAssetEmpty(string scenePath)
    {
        var activeScene = EditorSceneManager.GetActiveScene();
        if (activeScene.path == scenePath)
            return activeScene.isLoaded && activeScene.rootCount == 0;

        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
        var isEmpty = scene.isLoaded && scene.rootCount == 0;
        if (scene.isLoaded)
            EditorSceneManager.CloseScene(scene, removeScene: true);
        return isEmpty;
    }

    static void EnsureVfxFolderExists()
    {
        ParticleToVfxAssetPaths.EnsureAssetFolderExists(ParticleToVfxAssetPaths.RootFolder + "/Editor/Scenes");
    }

    public static ParticleToVfxComparisonSceneContext BuildComparisonScene(
        ParticleToVfxBatchItem item,
        string sceneName = ValidationSceneName,
        GameObject sourceOverride = null)
    {
        if (item == null)
            return null;

        var sourcePrefab = ResolveComparisonSource(item, sourceOverride);
        if (sourcePrefab == null)
        {
            Debug.LogError("Comparison failed: source object could not be resolved.");
            return null;
        }

        GameObject vfxPrefab = null;
        VisualEffectAsset vfxAsset = null;
        if (item.Result != null && item.Result.Success)
        {
            vfxPrefab = item.Result.VfxPrefab;
            vfxAsset = item.Result.VfxAsset;
            if (vfxPrefab == null && !string.IsNullOrEmpty(item.Result.PrefabPath))
                vfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.Result.PrefabPath);
            if (vfxAsset == null && !string.IsNullOrEmpty(item.Result.VfxAssetPath))
                vfxAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(item.Result.VfxAssetPath);
        }

        if (vfxPrefab == null && vfxAsset == null)
        {
            Debug.LogWarning($"Comparison for '{item.DisplayName}' requires a successful conversion with a VFX asset or prefab.");
            return null;
        }

        CloseScenesNamed(sceneName);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        scene.name = sceneName;
        return CreateComparisonRoot(scene, sourcePrefab, vfxPrefab, vfxAsset, item.DisplayName);
    }

    public static void CloseScenesNamed(string sceneName)
    {
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded || scene.name != sceneName)
                continue;

            EditorSceneManager.CloseScene(scene, true);
        }
    }

    public static void DestroyContext(ParticleToVfxComparisonSceneContext context)
    {
        if (context == null)
            return;

        if (context.Scene.isLoaded)
            EditorSceneManager.CloseScene(context.Scene, true);
    }

    public static int CountShurikenParticles(GameObject root)
    {
        if (root == null)
            return 0;

        var total = 0;
        var systems = root.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var system in systems)
            total += system.particleCount;

        return total;
    }

    public static int CountVfxParticles(GameObject root)
    {
        if (root == null)
            return 0;

        var total = 0;
        var effects = root.GetComponentsInChildren<VisualEffect>(true);
        foreach (var effect in effects)
            total += effect.aliveParticleCount;

        return total;
    }

    public const string ValidationSceneAssetPath = ParticleToVfxAssetPaths.RootFolder + "/Editor/Temp/VisualValidation.unity";

    public static bool SaveValidationScene(ParticleToVfxComparisonSceneContext context)
    {
        if (context == null || !context.Scene.IsValid() || !context.Scene.isLoaded)
            return false;

        EnsureTempFolderExists();
        return EditorSceneManager.SaveScene(context.Scene, ValidationSceneAssetPath);
    }

    public static bool SwapComparisonItem(ParticleToVfxComparisonSceneContext context, ParticleToVfxBatchItem item)
    {
        if (context == null || item == null || context.Root == null)
            return false;

        if (!TryResolvePrefabs(item, out var sourcePrefab, out var vfxPrefab, out var vfxAsset))
            return false;

        DestroyInstance(context.ShurikenInstance);
        DestroyInstance(context.VfxInstance);
        context.ShurikenInstance = null;
        context.VfxInstance = null;

        context.ShurikenInstance = InstantiateEffect(sourcePrefab, context.Scene, context.Root.transform, new Vector3(-SideSpacing * 0.5f, 0f, 0f), $"{sourcePrefab.name} (Shuriken)");
        context.VfxInstance = InstantiateVfxEffect(vfxPrefab, vfxAsset, context.Scene, context.Root.transform, item.DisplayName, new Vector3(SideSpacing * 0.5f, 0f, 0f));
        context.Label = item.DisplayName;
        return context.ShurikenInstance != null && context.VfxInstance != null;
    }

    public static void RestartEffects(ParticleToVfxComparisonSceneContext context)
    {
        if (context?.ShurikenInstance != null)
        {
            var systems = context.ShurikenInstance.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var system in systems)
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Play(true);
            }
        }

        if (context?.VfxInstance != null)
            ConfigureVisualEffectSeeds(context.VfxInstance, restart: true);
    }

    static void EnsureTempFolderExists()
    {
        ParticleToVfxAssetPaths.EnsureAssetFolderExists(ParticleToVfxAssetPaths.RootFolder + "/Editor/Temp");
    }

    static bool TryResolvePrefabs(
        ParticleToVfxBatchItem item,
        out GameObject sourcePrefab,
        out GameObject vfxPrefab,
        out VisualEffectAsset vfxAsset)
    {
        sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.PrefabAssetPath);
        vfxPrefab = null;
        vfxAsset = null;

        if (sourcePrefab == null)
            return false;

        if (item.Result != null && item.Result.Success)
        {
            vfxPrefab = item.Result.VfxPrefab;
            vfxAsset = item.Result.VfxAsset;
            if (vfxPrefab == null && !string.IsNullOrEmpty(item.Result.PrefabPath))
                vfxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.Result.PrefabPath);
            if (vfxAsset == null && !string.IsNullOrEmpty(item.Result.VfxAssetPath))
                vfxAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(item.Result.VfxAssetPath);
        }

        return vfxPrefab != null || vfxAsset != null;
    }

    static GameObject InstantiateEffect(
        GameObject prefab,
        Scene scene,
        Transform parent,
        Vector3 localPosition,
        string instanceName)
    {
        if (prefab == null)
            return null;

        var instance = EditorApplication.isPlaying
            ? UnityEngine.Object.Instantiate(prefab)
            : PrefabUtility.IsPartOfPrefabAsset(prefab)
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefab)
                : UnityEngine.Object.Instantiate(prefab);

        SceneManager.MoveGameObjectToScene(instance, scene);
        instance.name = instanceName;
        instance.transform.SetParent(parent, false);
        instance.transform.localPosition = localPosition;
        return instance;
    }

    static GameObject InstantiateVfxEffect(
        GameObject vfxPrefab,
        VisualEffectAsset vfxAsset,
        Scene scene,
        Transform parent,
        string label,
        Vector3 localPosition)
    {
        GameObject vfxInstance;
        if (vfxPrefab != null)
        {
            vfxInstance = InstantiateEffect(vfxPrefab, scene, parent, localPosition, $"{vfxPrefab.name} (VFX)");
        }
        else
        {
            vfxInstance = new GameObject($"{label}_VFX (VFX)");
            SceneManager.MoveGameObjectToScene(vfxInstance, scene);
            var visualEffect = vfxInstance.AddComponent<VisualEffect>();
            visualEffect.visualEffectAsset = vfxAsset;
            vfxInstance.transform.SetParent(parent, false);
            vfxInstance.transform.localPosition = localPosition;
        }

        ConfigureVisualEffectSeeds(vfxInstance);
        return vfxInstance;
    }

    static void DestroyInstance(GameObject instance)
    {
        if (instance == null)
            return;

        if (EditorApplication.isPlaying)
            UnityEngine.Object.Destroy(instance);
        else
            UnityEngine.Object.DestroyImmediate(instance);
    }

    static ParticleToVfxComparisonSceneContext CreateComparisonRoot(
        Scene scene,
        GameObject sourcePrefab,
        GameObject vfxPrefab,
        VisualEffectAsset vfxAsset,
        string label)
    {
        var context = new ParticleToVfxComparisonSceneContext
        {
            Scene = scene,
            Label = label
        };

        var root = new GameObject($"Compare_{label}");
        context.Root = root;
        SceneManager.MoveGameObjectToScene(root, scene);

        var sourceInstance = InstantiateEffect(
            sourcePrefab,
            scene,
            root.transform,
            new Vector3(-SideSpacing * 0.5f, 0f, 0f),
            $"{sourcePrefab.name} (Shuriken)");
        context.ShurikenInstance = sourceInstance;

        GameObject vfxInstance;
        if (vfxPrefab != null)
        {
            vfxInstance = (GameObject)PrefabUtility.InstantiatePrefab(vfxPrefab);
            SceneManager.MoveGameObjectToScene(vfxInstance, scene);
            vfxInstance.name = $"{vfxPrefab.name} (VFX)";
        }
        else
        {
            vfxInstance = new GameObject($"{label}_VFX (VFX)");
            SceneManager.MoveGameObjectToScene(vfxInstance, scene);
            var visualEffect = vfxInstance.AddComponent<VisualEffect>();
            visualEffect.visualEffectAsset = vfxAsset;
            visualEffect.startSeed = 0;
            visualEffect.resetSeedOnPlay = true;
        }

        vfxInstance.transform.SetParent(root.transform, false);
        vfxInstance.transform.localPosition = new Vector3(SideSpacing * 0.5f, 0f, 0f);
        context.VfxInstance = vfxInstance;

        ConfigureVisualEffectSeeds(vfxInstance);
        CreateLabel(scene, root.transform, "Shuriken (left)", new Vector3(-SideSpacing * 0.5f, 2.5f, 0f));
        CreateLabel(scene, root.transform, "VFX Graph (right)", new Vector3(SideSpacing * 0.5f, 2.5f, 0f));
        CreateSceneLight(scene);
        CreateSceneCameras(scene, context);
        return context;
    }

    static void CreateSceneLight(Scene scene)
    {
        var lightGo = new GameObject("Compare Light");
        SceneManager.MoveGameObjectToScene(lightGo, scene);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    static void ConfigureVisualEffectSeeds(GameObject vfxRoot, bool restart = false)
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

    static void CreateSceneCameras(Scene scene, ParticleToVfxComparisonSceneContext context)
    {
        var compareGo = new GameObject("Compare Camera");
        SceneManager.MoveGameObjectToScene(compareGo, scene);
        context.CompareCamera = compareGo.AddComponent<Camera>();
        context.CompareCamera.clearFlags = CameraClearFlags.SolidColor;
        context.CompareCamera.backgroundColor = new Color(0.08f, 0.08f, 0.1f, 1f);
        context.CompareCamera.transform.position = new Vector3(0f, 2f, -10f);
        context.CompareCamera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        compareGo.tag = "MainCamera";

        context.ShurikenCamera = CreateCaptureCamera(
            scene,
            "Shuriken Capture Camera",
            new Vector3(-SideSpacing * 0.5f, 2f, -10f),
            context.CompareCamera);

        context.VfxCamera = CreateCaptureCamera(
            scene,
            "VFX Capture Camera",
            new Vector3(SideSpacing * 0.5f, 2f, -10f),
            context.CompareCamera);
    }

    static Camera CreateCaptureCamera(Scene scene, string name, Vector3 position, Camera template)
    {
        var cameraGo = new GameObject(name);
        SceneManager.MoveGameObjectToScene(cameraGo, scene);
        var camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = template.clearFlags;
        camera.backgroundColor = template.backgroundColor;
        camera.fieldOfView = template.fieldOfView;
        camera.nearClipPlane = template.nearClipPlane;
        camera.farClipPlane = template.farClipPlane;
        camera.transform.position = position;
        camera.transform.rotation = template.transform.rotation;
        return camera;
    }

    static void CreateLabel(Scene scene, Transform parent, string text, Vector3 localPosition)
    {
        var labelGo = new GameObject(text);
        SceneManager.MoveGameObjectToScene(labelGo, scene);
        labelGo.transform.SetParent(parent, false);
        labelGo.transform.localPosition = localPosition;

        var textMesh = labelGo.AddComponent<TextMesh>();
        textMesh.text = text;
        textMesh.characterSize = 0.1f;
        textMesh.fontSize = 48;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = Color.white;
    }

    static void FrameComparisonView(Camera camera)
    {
        if (SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.Frame(new Bounds(Vector3.zero, new Vector3(SideSpacing + 2f, 4f, 4f)), false);
            SceneView.lastActiveSceneView.Repaint();
        }

        if (camera != null)
            camera.transform.position = new Vector3(0f, 2f, -10f);
    }

    static GameObject ResolveComparisonSource(ParticleToVfxBatchItem item, GameObject sourceOverride)
    {
        if (!string.IsNullOrEmpty(item?.PrefabAssetPath))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(item.PrefabAssetPath);
            if (prefab != null)
                return prefab;
        }

        if (item?.Result?.SourcePrefab != null)
            return item.Result.SourcePrefab;

        if (sourceOverride != null)
            return sourceOverride;

        return null;
    }

    static bool IsPrefabAssetPath(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath)
               && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
    }
}
