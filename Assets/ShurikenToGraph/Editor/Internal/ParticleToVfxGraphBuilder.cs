using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;
using VfxBlock = UnityEditor.VFX.Block;
using PositionShapeBlock = UnityEditor.VFX.Block.PositionShape;

public static partial class ParticleToVfxGraphBuilder
{
    const string SeedTemplatePath = ParticleToVfxAssetPaths.SeedTemplateAssetPath;
    const float ContextYOffset = 400f;
    const float SystemXOffset = 520f;
    // Calibration factors that convert a Shuriken noise.strength value into a comparable
    // VFX Turbulence intensity. These are a general unit conversion, not per-effect tuning.
    const float TurbulenceCurlRangeFactor = 2f;
    const float TurbulenceAbsoluteModeScale = 4f;
    const float TurbulenceNoiseBoost = 0.5f;

    internal sealed class BuiltParticleSystem
    {
        public ParticleEffectSnapshot Snapshot;
        public VFXContext Spawner;
        public VFXContext Initialize;
        public VFXContext Update;
        public VFXContext Output;
        public readonly List<VFXBlock> TriggerBlocks = new();
    }

    public static VisualEffectAsset Build(ParticleEffectSnapshot snapshot, string outputFolder, out string assetPath)
    {
        var hierarchy = new ParticleEffectHierarchySnapshot
        {
            RootName = snapshot.SourceName,
            RootLocalPosition = snapshot.LocalPosition,
            RootLocalRotation = snapshot.LocalRotation,
            RootLocalScale = snapshot.LocalScale
        };
        hierarchy.Systems.Add(snapshot);
        return Build(hierarchy, outputFolder, out assetPath);
    }

    public static VisualEffectAsset Build(ParticleEffectHierarchySnapshot hierarchy, string outputFolder, out string assetPath)
    {
        if (hierarchy == null || hierarchy.Systems.Count == 0)
            throw new ArgumentException("No particle systems to convert.");

        if (!ParticleToVfxAssetPaths.SeedTemplateExists())
            throw new FileNotFoundException($"VFX seed template not found at '{ParticleToVfxAssetPaths.SeedTemplateAssetPath}'.");

        Directory.CreateDirectory(outputFolder);

        if (hierarchy.Systems.Count == 1)
            return BuildInlineGraph(hierarchy, hierarchy.Systems[0], outputFolder, out assetPath);

        return BuildComposedSubgraphGraph(hierarchy, outputFolder, out assetPath);
    }

    static VisualEffectAsset BuildInlineGraph(
        ParticleEffectHierarchySnapshot hierarchy,
        ParticleEffectSnapshot snapshot,
        string outputFolder,
        out string assetPath)
    {
        var safeName = ParticleToVfxConversionService.MakeSafeFileName(hierarchy.RootName);
        assetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{safeName}_VFX.vfx");
        var asset = ParticleToVfxAssetPaths.CreateVfxAssetFromSeed(assetPath);
        var resource = asset.GetResource();
        var graph = resource.GetOrCreateGraph();
        graph.RemoveAllChildren();

        var built = BuildSystemChain(snapshot, 0);
        AddBuiltSystemToGraph(graph, built);
        AddExposedBlackboardParameters(graph, built);
        AddHierarchyBlackboardParameters(graph, hierarchy);

        if (snapshot.Prewarm && resource != null)
            ConfigurePrewarm(resource, snapshot);

        FinalizeGraphAsset(resource, assetPath);
        AssetDatabase.SaveAssets();
        return asset;
    }

    internal static void AddBuiltSystemToGraph(VFXGraph graph, BuiltParticleSystem built)
    {
        graph.AddChild(built.Spawner);
        graph.AddChild(built.Initialize);
        graph.AddChild(built.Update);
        graph.AddChild(built.Output);
    }

    internal static void FinalizeGraphAsset(VisualEffectResource resource, string assetPath)
    {
        var graph = resource.GetOrCreateGraph();
        graph.UpdateSubAssets();
        graph.UpdateImportDependencies();
        graph.BuildSubgraphDependencies();

        EditorUtility.SetDirty(resource);
        resource.WriteAssetWithSubAssets();
    }

    // While a single system chain is being built, failures from the reflection-based
    // setting/slot helpers are routed to this snapshot so they surface in the conversion
    // report instead of being silently swallowed.
    static ParticleEffectSnapshot s_ActiveSnapshot;

    static void ReportBuildFailure(string message)
    {
        Debug.LogWarning($"[ParticleToVfx] {message}");
        s_ActiveSnapshot?.Warnings.Add(message);
    }

    internal static BuiltParticleSystem BuildSystemChain(ParticleEffectSnapshot snapshot, int systemIndex, bool preferGpuEventEntry = false)
    {
        var previousSnapshot = s_ActiveSnapshot;
        s_ActiveSnapshot = snapshot;
        try
        {
            var xOffset = systemIndex * SystemXOffset;
            var spawner = preferGpuEventEntry
                ? (VFXContext)ScriptableObject.CreateInstance<VFXBasicGPUEvent>()
                : CreateSpawner(snapshot);
            if (preferGpuEventEntry)
                ((VFXBasicGPUEvent)spawner).label = $"GPU Event {snapshot.SourceName}";

            var init = CreateInitialize(snapshot);
            var update = CreateUpdate(snapshot);
            var output = CreateOutput(snapshot);

            spawner.position = new Vector2(xOffset, 0f);
            init.position = new Vector2(xOffset, ContextYOffset);
            update.position = new Vector2(xOffset, ContextYOffset * 2f);
            output.position = new Vector2(xOffset, ContextYOffset * 3f);

            spawner.label = $"Spawn {snapshot.SourceName}";
            init.label = $"Initialize {snapshot.SourceName}";
            update.label = $"Update {snapshot.SourceName}";
            output.label = $"Output {snapshot.SourceName}";

            spawner.LinkTo(init);
            init.LinkTo(update);
            update.LinkTo(output);

            return new BuiltParticleSystem
            {
                Snapshot = snapshot,
                Spawner = spawner,
                Initialize = init,
                Update = update,
                Output = output
            };
        }
        finally
        {
            s_ActiveSnapshot = previousSnapshot;
        }
    }

    static void WireSubEmitters(BuiltParticleSystem[] builtSystems)
    {
        for (var parentIndex = 0; parentIndex < builtSystems.Length; parentIndex++)
        {
            var parent = builtSystems[parentIndex];
            foreach (var subEmitter in parent.Snapshot.SubEmitters)
            {
                if (subEmitter.TargetSystemIndex < 0 || subEmitter.TargetSystemIndex >= builtSystems.Length)
                    continue;

                if (subEmitter.SkipAutoWire)
                    continue;

                var child = builtSystems[subEmitter.TargetSystemIndex];
                if (!TryWireSubEmitter(parent, child, subEmitter))
                {
                    parent.Snapshot.Warnings.Add(
                        $"Sub-emitter '{subEmitter.Type}' -> '{child.Snapshot.SourceName}' could not be wired automatically.");
                }
            }
        }
    }

    static bool TryWireSubEmitter(BuiltParticleSystem parent, BuiltParticleSystem child, SubEmitterSnapshot subEmitter)
    {
        switch (subEmitter.Type)
        {
            case ParticleSystemSubEmitterType.Birth:
                return WireGpuTriggeredSystem(parent, child, "Always", parent.Initialize, subEmitter);
            case ParticleSystemSubEmitterType.Death:
                return WireGpuTriggeredSystem(parent, child, "OnDie", parent.Update, subEmitter);
            case ParticleSystemSubEmitterType.Collision:
                if (!parent.Snapshot.CollisionEnabled)
                    parent.Snapshot.Warnings.Add(
                        $"Collision sub-emitter to '{child.Snapshot.SourceName}' added a default ground plane collision block.");
                return WireGpuTriggeredSystem(parent, child, "OnCollide", parent.Update, subEmitter);
            case ParticleSystemSubEmitterType.Manual:
                parent.Snapshot.Warnings.Add(
                    $"Manual sub-emitter '{child.Snapshot.SourceName}' must be triggered from script via VisualEffect.SendEvent or a custom GPU event.");
                return false;
            default:
                parent.Snapshot.Warnings.Add(
                    $"Sub-emitter type '{subEmitter.Type}' is not auto-wired.");
                return false;
        }
    }

    static bool WireGpuTriggeredSystem(
        BuiltParticleSystem parent,
        BuiltParticleSystem child,
        string triggerMode,
        VFXContext triggerContext,
        SubEmitterSnapshot subEmitter)
    {
        var spawnCount = ResolveSubEmitterSpawnCount(subEmitter, parent.Snapshot);
        var triggerBlock = CreateTriggerEventBlock(triggerMode, spawnCount);
        if (triggerBlock == null)
            return false;

        triggerContext.AddChild(triggerBlock);
        parent.TriggerBlocks.Add(triggerBlock);

        var gpuEvent = ScriptableObject.CreateInstance<VFXBasicGPUEvent>();
        gpuEvent.label = $"GPU Event -> {child.Snapshot.SourceName}";
        gpuEvent.position = child.Spawner.position + new Vector2(0f, -ContextYOffset * 0.5f);

        var graph = parent.Spawner.GetGraph();
        graph.AddChild(gpuEvent);

        if (!TryLinkEventSlots(triggerBlock, gpuEvent))
        {
            parent.Snapshot.Warnings.Add(
                $"Could not link GPU event from '{parent.Snapshot.SourceName}' to '{child.Snapshot.SourceName}'.");
            return false;
        }

        if (child.Spawner is VFXBasicSpawner basicSpawner)
        {
            graph.RemoveChild(basicSpawner);
            child.Spawner = gpuEvent;
        }
        else if (child.Spawner is VFXBasicGPUEvent)
        {
            parent.Snapshot.Warnings.Add(
                $"Child system '{child.Snapshot.SourceName}' already uses a GPU event spawner; additional parent wiring may conflict.");
        }

        gpuEvent.LinkTo(child.Initialize);
        return true;
    }

    static uint ResolveSubEmitterSpawnCount(SubEmitterSnapshot subEmitter, ParticleEffectSnapshot snapshot)
    {
        if (subEmitter == null)
            return 1u;

        var count = Mathf.Max(1, Mathf.RoundToInt(subEmitter.EmitProbability));
        if (subEmitter.EmitProbability < 0.999f && subEmitter.EmitProbability > 0.001f)
        {
            snapshot.Warnings.Add(
                $"Sub-emitter emit probability {subEmitter.EmitProbability:P0} is not stochastic in VFX; using spawn count {count}.");
        }

        return (uint)count;
    }

    static VFXBlock CreateTriggerEventBlock(string modeLiteral, uint count)
    {
        var block = TryCreateBlock("TriggerEvent");
        if (block == null)
            return null;

        TrySetBlockEnumSetting(block, "mode", "UnityEditor.VFX.Block.TriggerEvent+Mode", modeLiteral);
        block.ResyncSlots(true);
        SetBlockSlotValue(block, "count", count);
        return block;
    }

    static bool TryLinkEventSlots(VFXBlock triggerBlock, VFXContext gpuEventContext)
    {
        var outputSlot = FindSlot(triggerBlock, "evt", isInput: false);
        var inputSlot = FindSlot(gpuEventContext, "evt", isInput: true);
        if (outputSlot == null || inputSlot == null)
            return false;

        outputSlot.Link(inputSlot);
        return true;
    }

    static VFXSlot FindSlot(object owner, string propertyName, bool isInput)
    {
        if (owner == null)
            return null;

        var methodName = isInput ? "GetInputSlot" : "GetOutputSlot";
        var ownerType = owner.GetType();

        var stringMethod = ownerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        if (stringMethod != null)
        {
            var slot = stringMethod.Invoke(owner, new object[] { propertyName }) as VFXSlot;
            if (slot != null)
                return slot;
        }

        var intMethod = ownerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(int) },
            null);
        if (intMethod == null)
            return null;

        var slotCount = isInput ? GetInputSlotCount(owner) : GetOutputSlotCount(owner);
        for (var i = 0; i < slotCount; i++)
        {
            var slot = intMethod.Invoke(owner, new object[] { i }) as VFXSlot;
            if (slot != null && GetSlotPropertyName(slot) == propertyName)
                return slot;
        }

        return null;
    }

    static int GetOutputSlotCount(object owner)
    {
        var method = owner?.GetType().GetMethod(
            "GetNbOutputSlots",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
            return 0;

        return (int)method.Invoke(owner, null);
    }

    static void ConfigurePrewarm(VisualEffectResource resource, ParticleEffectSnapshot snapshot)
    {
        const float prewarmDeltaTime = 0.05f;
        var serializedResource = new SerializedObject(resource);
        var deltaProperty = serializedResource.FindProperty("m_Infos.m_PreWarmDeltaTime");
        var stepProperty = serializedResource.FindProperty("m_Infos.m_PreWarmStepCount");
        if (deltaProperty != null && stepProperty != null)
        {
            deltaProperty.floatValue = prewarmDeltaTime;
            stepProperty.intValue = Mathf.Max(1, Mathf.CeilToInt(snapshot.Duration / prewarmDeltaTime));
            serializedResource.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            snapshot.Warnings.Add("Could not configure VFX prewarm on the converted asset.");
        }
    }

    static VFXBasicSpawner CreateSpawner(ParticleEffectSnapshot snapshot)
    {
        var spawner = ScriptableObject.CreateInstance<VFXBasicSpawner>();
        spawner.label = "Spawn System";

        ConfigureSpawnerLoop(spawner, snapshot);
        AddSpawnerBlocks(spawner, snapshot);

        return spawner;
    }

    static void AddSpawnerBlocks(VFXBasicSpawner spawner, ParticleEffectSnapshot snapshot)
    {
        var hasCurveRate = snapshot.RateOverTime.UsesCurve;
        var hasConstantRate = !hasCurveRate && snapshot.EmissionRate > 0.001f;
        if (hasCurveRate)
            AddRateOverTimeCurveSpawner(spawner, snapshot);
        else if (hasConstantRate)
        {
            var rate = ScriptableObject.CreateInstance<VFXSpawnerConstantRate>();
            SetBlockSlotValue(rate, 0, snapshot.EmissionRate);
            spawner.AddChild(rate);
        }

        foreach (var burst in snapshot.Bursts)
            AddBurstBlock(spawner, snapshot, burst);

        if (snapshot.RateOverDistance > 0.001f)
            AddRateOverDistanceSpawner(spawner, snapshot);

        if (!hasCurveRate && !hasConstantRate && snapshot.Bursts.Count == 0 && snapshot.RateOverDistance <= 0.001f)
        {
            var fallbackRate = ScriptableObject.CreateInstance<VFXSpawnerConstantRate>();
            SetBlockSlotValue(fallbackRate, 0, 0.01f);
            spawner.AddChild(fallbackRate);
            snapshot.Warnings.Add("No emission source found; using minimal spawn rate.");
        }
    }

    static void AddBurstBlock(VFXBasicSpawner spawner, ParticleEffectSnapshot snapshot, EmissionBurstSnapshot burst)
    {
        var burstBlock = ScriptableObject.CreateInstance<VFXSpawnerBurst>();
        var repeatMode = burst.CycleCount > 1 || burst.RepeatInterval > 0.001f
            ? VFXSpawnerBurst.RepeatMode.Periodic
            : VFXSpawnerBurst.RepeatMode.Single;

        burstBlock.SetSettingValue("repeat", repeatMode);

        var minCount = Mathf.Max(0, burst.CountMin);
        var maxCount = Mathf.Max(minCount, burst.CountMax > 0 ? burst.CountMax : burst.Count);
        if (burst.Probability < 0.999f)
        {
            minCount = Mathf.Max(0, Mathf.RoundToInt(minCount * burst.Probability));
            maxCount = Mathf.Max(minCount, Mathf.RoundToInt(maxCount * burst.Probability));
            snapshot.Warnings.Add(
                $"Burst at {burst.Time:0.##}s probability {burst.Probability:P0} approximated by scaling burst counts.");
        }

        var usesRandomCount = maxCount > minCount;
        burstBlock.SetSettingValue(
            "spawnMode",
            usesRandomCount ? VFXSpawnerBurst.RandomMode.Random : VFXSpawnerBurst.RandomMode.Constant);
        burstBlock.SetSettingValue("delayMode", VFXSpawnerBurst.RandomMode.Constant);
        burstBlock.ResyncSlots(true);

        if (usesRandomCount)
        {
            SetBlockSlotValue(burstBlock, "Count", new Vector2(minCount, maxCount));
        }
        else
        {
            SetBlockSlotValue(burstBlock, "Count", (float)Mathf.Max(maxCount, 1));
        }

        var delay = burst.Time;
        if (repeatMode == VFXSpawnerBurst.RepeatMode.Periodic && burst.RepeatInterval > 0.001f)
            delay = burst.RepeatInterval;
        SetBlockSlotValue(burstBlock, "Delay", Mathf.Max(delay, 0f));

        spawner.AddChild(burstBlock);
    }

    static void ConfigureSpawnerLoop(VFXBasicSpawner spawner, ParticleEffectSnapshot snapshot)
    {
        spawner.SetSettingValue("loopDuration", VFXBasicSpawner.LoopMode.Constant);
        spawner.SetSettingValue(
            "loopCount",
            snapshot.Looping ? VFXBasicSpawner.LoopMode.Infinite : VFXBasicSpawner.LoopMode.Constant);

        spawner.ResyncSlots(true);
        SetContextSlotValue(spawner, "LoopDuration", snapshot.Duration);

        if (!snapshot.Looping)
            SetContextSlotValue(spawner, "LoopCount", 1);
    }

    static VFXBasicInitialize CreateInitialize(ParticleEffectSnapshot snapshot)
    {
        var init = ScriptableObject.CreateInstance<VFXBasicInitialize>();
        init.label = "Initialize";
        init.SetSettingValue("capacity", (uint)Mathf.Clamp(snapshot.MaxParticles, 1, 100000));

        if (snapshot.TrailsEnabled)
        {
            init.SetSettingValue("dataType", VFXDataParticle.DataType.ParticleStrip);
            ConfigureTrailStripSettings(init, snapshot);
        }

        ConfigureSimulationSpace(init, snapshot);

        AddLifetimeBlock(init, snapshot.StartLifetime);
        AddPositionBlock(init, snapshot);
        AddSizeBlock(init, snapshot);
        AddColorBlock(init, snapshot);
        AddAlphaBlock(init, GetBaseSpawnAlpha(snapshot));
        if (snapshot.TextureSheetEnabled && !ShouldUseFlipbookLifetimeCycles(snapshot.TextureSheet))
            AddTexIndexBlock(init, snapshot.TextureSheet?.StartFrame ?? 0);
        AddRotationBlock(init, snapshot);
        AddVelocityBlock(init, snapshot);
        if (snapshot.VelocityOverLifetimeEnabled && HasConstantVelocityOverLifetime(snapshot))
            AddVelocityOverLifetimeBlock(init, snapshot, VfxBlock.AttributeCompositionMode.Add);
        AddAngularVelocityBlock(init, snapshot);

        return init;
    }

    static void ConfigureTrailStripSettings(VFXBasicInitialize init, ParticleEffectSnapshot snapshot)
    {
        var trails = snapshot.Trails;
        if (trails == null)
            return;

        // Shuriken PerParticle trails = one ribbon per particle. Map each concurrent
        // particle to its own strip; a single strip would merge every particle into one ribbon.
        var pointsPerStrip = EstimateTrailStripPointCount(snapshot);
        var stripCount = EstimateTrailStripCount(snapshot);
        var capacity = Mathf.Clamp(stripCount * pointsPerStrip, pointsPerStrip, 100000);

        init.SetSettingValue("stripCapacity", (uint)stripCount);
        init.SetSettingValue("particlePerStripCount", (uint)pointsPerStrip);
        init.SetSettingValue("capacity", (uint)capacity);
    }

    static int EstimateTrailStripPointCount(ParticleEffectSnapshot snapshot)
    {
        var trails = snapshot.Trails;
        var lifetime = Mathf.Max(trails.Lifetime, 0.05f);
        var speed = Mathf.Max(Mathf.Max(snapshot.StartSpeed.Constant, snapshot.StartSpeed.Max), 0.5f);
        var travelDistance = speed * lifetime;
        // minVertexDistance is often 0 in source; fall back to a sane spacing so we don't
        // generate dozens of points for a short trail.
        var minVertexDistance = trails.MinimumVertexDistance > 0.001f ? trails.MinimumVertexDistance : 0.15f;
        var segments = Mathf.CeilToInt(travelDistance / minVertexDistance);
        return Mathf.Clamp(segments + 1, 4, 32);
    }

    static int EstimateTrailStripCount(ParticleEffectSnapshot snapshot)
    {
        var maxLifetime = Mathf.Max(snapshot.StartLifetime.Max, snapshot.StartLifetime.Constant, 0.1f);
        var rate = Mathf.Max(snapshot.EmissionRate, 1f);
        var concurrent = Mathf.CeilToInt(rate * maxLifetime);
        // Headroom so trails are not recycled visibly while still alive.
        return Mathf.Clamp(concurrent + 2, 2, 256);
    }

    static void ConfigureSimulationSpace(VFXBasicInitialize init, ParticleEffectSnapshot snapshot)
    {
        init.space = snapshot.SimulationSpace == ParticleSystemSimulationSpace.World
            ? VFXSpace.World
            : VFXSpace.Local;
    }

    static VFXBasicUpdate CreateUpdate(ParticleEffectSnapshot snapshot)
    {
        var update = ScriptableObject.CreateInstance<VFXBasicUpdate>();
        update.label = "Update";

        if (snapshot.TrailsEnabled && snapshot.Trails is { WorldSpace: true })
            update.space = VFXSpace.World;
        else if (snapshot.SimulationSpace == ParticleSystemSimulationSpace.World)
            update.space = VFXSpace.World;

        if (Mathf.Abs(snapshot.GravityModifier) > 0.001f)
            AddGravityBlock(update, snapshot);

        if (snapshot.NoiseEnabled)
            AddTurbulenceBlock(update, snapshot);

        if (snapshot.SizeOverLifetimeEnabled)
            AddSizeOverLifeBlock(update, snapshot);

        if (snapshot.ColorOverLifetimeEnabled)
            AddColorOverLifeBlock(update, snapshot);

        if (snapshot.VelocityOverLifetimeEnabled && !HasConstantVelocityOverLifetime(snapshot))
            AddVelocityOverLifetimeCurveBlock(update, snapshot);

        if (HasOrbitalVelocity(snapshot))
            AddOrbitalVelocityBlock(update, snapshot);

        if (snapshot.TextureSheetEnabled)
            AddFlipbookPlayBlock(update, snapshot);

        AddAdvancedModuleBlocks(update, snapshot);

        return update;
    }

    static VFXContext CreateOutput(ParticleEffectSnapshot snapshot)
    {
        VFXContext output;
        if (TryCreateMeshOutput(snapshot, out output))
        {
            // Created mesh output.
        }
        else if (snapshot.TrailsEnabled)
        {
            output = ScriptableObject.CreateInstance<VFXQuadStripOutput>();
            output.label = "Output Trail";
        }
        else
        {
            output = ScriptableObject.CreateInstance<VFXPlanarPrimitiveOutput>();
            output.label = "Output Particle";
        }

        ConfigureOutput(output, snapshot);
        if (output is VFXMeshOutput)
            ConfigureMeshOutput(output, snapshot);

        AddOrientBlock(output, snapshot);

        return output;
    }

    static void ConfigureOutput(VFXContext output, ParticleEffectSnapshot snapshot)
    {
        output.SetSettingValue("blendMode", snapshot.UseAdditiveBlend
            ? VFXAbstractRenderedOutput.BlendMode.Additive
            : VFXAbstractRenderedOutput.BlendMode.Alpha);
        output.SetSettingValue("useSoftParticle", snapshot.SoftParticlesEnabled);
        output.SetSettingValue("castShadows", snapshot.CastShadows);
        output.SetSettingValue("sortingPriority", snapshot.SortingOrder);
        output.SetSettingValue("useAlphaClipping", snapshot.UseAlphaClipping);
        if (output is VFXPlanarPrimitiveOutput)
            output.SetSettingValue("primitiveType", VFXPrimitiveType.Quad);
        TryAssignSuggestedShaderGraph(output, snapshot);
        output.SetSettingValue(
            "useBaseColorMap",
            VFXAbstractParticleOutput.BaseColorMapMode.ColorAndAlpha);

        if (snapshot.UseDoubleSided)
            output.SetSettingValue(
                "cullMode",
                GetEnumValue("UnityEditor.VFX.VFXAbstractRenderedOutput+CullMode", "Off"));

        output.ResyncSlots(true);

        if (UsesHdrParticleColor(snapshot))
            ConfigureHdrExposure(output, snapshot);

        if (snapshot.SoftParticlesEnabled)
            SetContextSlotValue(output, "softParticleFadeDistance", Mathf.Max(snapshot.SoftParticleFar, 0.01f));

        if (snapshot.UseAlphaClipping)
            SetContextSlotValue(output, "alphaThreshold", Mathf.Clamp01(snapshot.AlphaCutoff));

        if (snapshot.MainTexture != null)
            SetOutputMainTexture(output, ParticleToVfxAssetReferenceGuard.SanitizeForVfxAsset(
                snapshot.MainTexture, "main texture", snapshot));

        if (snapshot.TextureSheetEnabled)
            ConfigureFlipbookOutput(output, snapshot);
    }

    static bool UsesHdrParticleColor(ParticleEffectSnapshot snapshot)
    {
        if (IsHdrColor(snapshot.MaterialColor))
            return true;

        if (snapshot.StartColorTwoColors)
        {
            return IsHdrColor(snapshot.StartColorMin) || IsHdrColor(snapshot.StartColorMax);
        }

        return IsHdrColor(snapshot.StartColor);
    }

    static void ConfigureHdrExposure(VFXContext output, ParticleEffectSnapshot snapshot)
    {
        output.SetSettingValue("useExposureWeight", true);
        output.ResyncSlots(true);
        // The exposureWeight slot only exists on HDRP outputs; on other pipelines it is
        // legitimately absent, so record a report note rather than logging a warning.
        if (!SetContextSlotValue(output, "exposureWeight", 1f, logFailure: false))
            snapshot.Warnings.Add(
                "HDR material detected but the output has no exposure weight slot (non-HDRP pipeline).");
    }

    static bool IsHdrColor(Color color)
    {
        return color.r > 1.05f || color.g > 1.05f || color.b > 1.05f;
    }

    static void TryAssignSuggestedShaderGraph(VFXContext output, ParticleEffectSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.SuggestedShaderGraphPath)
            || !ParticleToVfxShaderGraphMatcher.TryLoadShaderGraphVfxAsset(
                snapshot.SuggestedShaderGraphPath,
                out var shaderGraphAsset))
        {
            output.SetSettingValue("shaderGraph", null);
            return;
        }

        var safeShaderGraph = ParticleToVfxAssetReferenceGuard.SanitizeForVfxAsset(
            shaderGraphAsset, "shader graph", snapshot);
        output.SetSettingValue("shaderGraph", safeShaderGraph);
        if (safeShaderGraph != null)
            snapshot.Warnings.Add($"Assigned VFX Shader Graph '{snapshot.SuggestedShaderGraphPath}'.");
    }

    static void ConfigureFlipbookOutput(VFXContext output, ParticleEffectSnapshot snapshot)
    {
        output.SetSettingValue("uvMode", VFXAbstractParticleOutput.UVMode.Flipbook);
        output.SetSettingValue("flipbookBlendFrames", true);
        output.ResyncSlots(true);

        var sheet = snapshot.TextureSheet;
        var flipBook = CreateStructValue(
            "UnityEditor.VFX.FlipBook",
            ("x", sheet.TilesX),
            ("y", sheet.TilesY));
        SetContextSlotValue(output, "flipBookSize", flipBook);
    }

    static bool ShouldUseFlipbookLifetimeCycles(TextureSheetSnapshot sheet)
    {
        if (sheet == null)
            return false;

        if (sheet.TimeMode == ParticleSystemAnimationTimeMode.Lifetime)
            return true;

        // A frame-over-time ramp authored on a whole-sheet animation maps the full
        // flipbook across the particle's lifetime; play it once over life (Cycles)
        // so the whole animation is visible even when the authored fps is too low
        // to traverse the sheet before the particle dies.
        return sheet.FrameOverTimeUsesCurve && sheet.Cycles <= 1;
    }

    static void AddFlipbookPlayBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("FlipbookPlay");
        if (block == null)
        {
            snapshot.Warnings.Add("Flipbook Player block unavailable.");
            return;
        }

        var sheet = snapshot.TextureSheet;
        var frameCount = Mathf.Max(1, sheet.TilesX * sheet.TilesY);
        var useLifetimeCycles = ShouldUseFlipbookLifetimeCycles(sheet);

        if (sheet.AnimationType == ParticleSystemAnimationType.SingleRow)
        {
            TrySetSetting(block, "mode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+Mode", "FrameRate"));
            TrySetSetting(block, "frameRateMode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+FrameRateMode", "Constant"));
            TrySetSetting(block, "animationRange", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+AnimationRange", "FlipbookRow"));
            TrySetSetting(block, "useCustomRange", true);
        }
        else if (useLifetimeCycles || sheet.Cycles > 1)
        {
            TrySetSetting(block, "mode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+Mode", "Cycles"));
            TrySetSetting(block, "cyclesMode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+CyclesMode", "Constant"));
            TrySetSetting(block, "animationRange", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+AnimationRange", "EntireFlipbook"));
        }
        else
        {
            TrySetSetting(block, "mode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+Mode", "FrameRate"));
            TrySetSetting(block, "frameRateMode", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+FrameRateMode", "Constant"));
            TrySetSetting(block, "animationRange", GetEnumValue("UnityEditor.VFX.Block.FlipbookPlay+AnimationRange", "EntireFlipbook"));
            TrySetSetting(block, "useCustomRange", true);
        }

        block.ResyncSlots(true);

        if (sheet.AnimationType == ParticleSystemAnimationType.SingleRow)
            SetBlockSlotValue(block, "index", sheet.FlipbookRow);

        if (sheet.AnimationType != ParticleSystemAnimationType.SingleRow
            && (useLifetimeCycles || sheet.Cycles > 1))
        {
            var cycleCount = (float)Mathf.Max(1, sheet.Cycles);
            if (!SetBlockSlotValue(block, "cycles", cycleCount))
                SetBlockSlotValue(block, 0, cycleCount);
        }
        else
        {
            var frameRate = sheet.TimeMode == ParticleSystemAnimationTimeMode.FPS
                ? sheet.Fps
                : Mathf.Max(sheet.FrameOverTimeSpeed * frameCount, 0.01f);
            if (!SetBlockSlotValue(block, "frameRate", frameRate))
                SetBlockSlotValue(block, 0, frameRate);
        }

        context.AddChild(block);
    }

    static object GetEnumValue(string enumTypeName, string literal)
    {
        var enumType = typeof(VFXBlock).Assembly.GetType(enumTypeName);
        return enumType != null && Enum.TryParse(enumType, literal, out var value) ? value : literal;
    }

    static void SetOutputMainTexture(VFXContext output, Texture2D texture)
    {
        if (SetContextSlotValue(output, "mainTexture", texture))
            return;

        if (output is not IVFXSlotContainer slotContainer)
            return;

        for (var i = 0; i < slotContainer.GetNbInputSlots(); i++)
        {
            var slot = output.GetInputSlot(i);
            if (slot == null || slot.property.name != "mainTexture")
                continue;

            slot.value = texture;
            return;
        }
    }

    static void AddLifetimeBlock(VFXContext context, MinMaxSnapshot lifetime)
    {
        var block = CreateSetAttributeBlock(VFXAttribute.Lifetime);
        ConfigureRandomOrConstant(block, lifetime);
        context.AddChild(block);
    }

    static void AddRotationBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        // Shuriken supports negative start rotation; only skip when the range is zero.
        if (Mathf.Abs(snapshot.StartRotation.Max) <= 0.001f && Mathf.Abs(snapshot.StartRotation.Min) <= 0.001f)
            return;

        var block = CreateSetAttributeBlock(VFXAttribute.AngleZ);
        var rotation = snapshot.StartRotation;
        if (rotation.IsRandom && !Mathf.Approximately(rotation.Min, rotation.Max))
        {
            TrySetSettingEnum(block, "Random", "Uniform");
            SetBlockSlotValue(block, 0, rotation.Min * Mathf.Deg2Rad);
            SetBlockSlotValue(block, 1, rotation.Max * Mathf.Deg2Rad);
        }
        else
        {
            SetBlockSlotValue(block, 0, rotation.Constant * Mathf.Deg2Rad);
        }

        context.AddChild(block);
    }

    static void AddAngularVelocityBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (!snapshot.RotationOverLifetimeEnabled)
            return;

        AddAngularVelocityAxis(context, VFXAttribute.AngularVelocityX, snapshot.AngularVelocityX);
        AddAngularVelocityAxis(context, VFXAttribute.AngularVelocityY, snapshot.AngularVelocityY);
        AddAngularVelocityAxis(context, VFXAttribute.AngularVelocityZ, snapshot.AngularVelocityZ);
    }

    static void AddAngularVelocityAxis(VFXContext context, VFXAttribute attribute, MinMaxSnapshot values)
    {
        if (Mathf.Approximately(values.Min, 0f) && Mathf.Approximately(values.Max, 0f))
            return;

        var block = CreateSetAttributeBlock(attribute);
        ConfigureRandomOrConstant(block, values);
        context.AddChild(block);
    }

    static bool HasConstantVelocityOverLifetime(ParticleEffectSnapshot snapshot)
    {
        return !snapshot.VelocityX.UsesCurve
               && !snapshot.VelocityY.UsesCurve
               && !snapshot.VelocityZ.UsesCurve;
    }

    static void AddGravityBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("Gravity");
        if (block == null)
        {
            snapshot.Warnings.Add("Gravity block unavailable.");
            return;
        }

        var force = Physics.gravity * snapshot.GravityModifier;
        SetBlockSlotValue(block, "Force", (Vector)force);
        context.AddChild(block);
    }

    static void AddPositionBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (!snapshot.ShapeEnabled)
        {
            if (HasEmitterPositionOffset(snapshot))
                AddEmitterPositionBlock(context, snapshot);
            return;
        }

        if (TryAddPositionMeshBlock(context, snapshot))
            return;

        if (TryAddPositionSpriteBoxBlock(context, snapshot))
            return;

        var block = ScriptableObject.CreateInstance<PositionShapeBlock>();
        var shapeType = MapShapeType(snapshot.ShapeType);

        block.SetSettingValue("shape", shapeType);
        ConfigureShapePositionMode(block, snapshot);
        block.SetSettingValue("compositionPosition", VfxBlock.AttributeCompositionMode.Overwrite);
        block.ResyncSlots(true);

        if (!TryConfigureShapeSlots(block, snapshot, shapeType))
        {
            snapshot.Warnings.Add($"Shape '{snapshot.ShapeType}' is only partially mapped; using a box spawn volume.");
            block.SetSettingValue("shape", VfxBlock.PositionShapeBase.Type.OrientedBox);
            block.ResyncSlots(true);
            SetBlockSlotValue(block, "Box", BuildOrientedBox(snapshot));
        }

        context.AddChild(block);
    }

    static bool HasEmitterPositionOffset(ParticleEffectSnapshot snapshot)
    {
        return snapshot.SpawnOffset.sqrMagnitude > 0.0001f
               || snapshot.ShapePosition.sqrMagnitude > 0.0001f;
    }

    static void AddEmitterPositionBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = CreateSetAttributeBlock(VFXAttribute.Position);
        TrySetSetting(block, "Composition", VfxBlock.AttributeCompositionMode.Overwrite);
        SetBlockSlotValue(block, 0, (Vector)GetShapeCenter(snapshot));
        context.AddChild(block);
    }

    static bool TryAddPositionMeshBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (!IsResolvableMeshShape(snapshot))
            return false;

        var block = TryCreateBlock("PositionMesh");
        if (block == null)
        {
            snapshot.Warnings.Add("Position Mesh block unavailable; using a box spawn volume.");
            return false;
        }

        TrySetBlockEnumSetting(
            block,
            "sourceMesh",
            "UnityEditor.VFX.Operator.SampleMesh+SourceType",
            "Mesh");
        TrySetSetting(block, "compositionPosition", VfxBlock.AttributeCompositionMode.Overwrite);
        block.ResyncSlots(true);

        var mesh = snapshot.ShapeType switch
        {
            ParticleSystemShapeType.MeshRenderer => snapshot.ShapeRendererMesh,
            ParticleSystemShapeType.SkinnedMeshRenderer => snapshot.ShapeSkinnedMeshRenderer?.sharedMesh,
            _ => snapshot.ShapeMesh
        };
        mesh = ParticleToVfxAssetReferenceGuard.SanitizeForVfxAsset(mesh, "spawn shape mesh", snapshot);
        if (mesh == null)
            return false;

        if (snapshot.ShapeType == ParticleSystemShapeType.SkinnedMeshRenderer)
        {
            snapshot.Warnings.Add(
                "Skinned mesh spawn shape converted using the bind-pose mesh asset; bind the Skinned Mesh Renderer on the VFX blackboard manually for animated sampling.");
        }

        if (!SetBlockSlotValue(block, "mesh", mesh))
            SetBlockSlotValue(block, 0, mesh);

        context.AddChild(block);
        return true;
    }

    static bool TryAddPositionSpriteBoxBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (!TryGetSpriteShapeSize(snapshot, out var spriteSize))
            return false;

        var block = ScriptableObject.CreateInstance<PositionShapeBlock>();
        block.SetSettingValue("shape", VfxBlock.PositionShapeBase.Type.OrientedBox);
        block.SetSettingValue("compositionPosition", VfxBlock.AttributeCompositionMode.Overwrite);
        block.SetSettingValue("positionMode", VfxBlock.PositionBase.PositionMode.Volume);
        block.ResyncSlots(true);

        var box = CreateStructValue(
            "UnityEditor.VFX.OrientedBox",
            ("center", (object)GetShapeCenter(snapshot)),
            ("angles", GetShapeAngles(snapshot)),
            ("size", Vector3.Scale(spriteSize, snapshot.ShapeScale == Vector3.zero ? Vector3.one : snapshot.ShapeScale)));
        SetBlockSlotValue(block, "Box", box);
        context.AddChild(block);
        return true;
    }

    static bool TryGetSpriteShapeSize(ParticleEffectSnapshot snapshot, out Vector3 spriteSize)
    {
        spriteSize = Vector3.one;
        Sprite sprite = null;
        if (snapshot.ShapeType == ParticleSystemShapeType.Sprite)
            sprite = snapshot.ShapeSprite;
        else if (snapshot.ShapeType == ParticleSystemShapeType.SpriteRenderer && snapshot.ShapeSpriteRenderer != null)
            sprite = snapshot.ShapeSpriteRenderer.sprite;

        if (sprite == null)
            return false;

        var bounds = sprite.bounds.size;
        spriteSize = new Vector3(
            Mathf.Max(bounds.x, 0.01f),
            Mathf.Max(bounds.y, 0.01f),
            Mathf.Max(bounds.z, 0.01f));
        return true;
    }

    static bool IsResolvableMeshShape(ParticleEffectSnapshot snapshot)
    {
        return snapshot.ShapeType switch
        {
            ParticleSystemShapeType.Mesh => snapshot.ShapeMesh != null,
            ParticleSystemShapeType.MeshRenderer => snapshot.ShapeRendererMesh != null,
            ParticleSystemShapeType.SkinnedMeshRenderer => snapshot.ShapeSkinnedMeshRenderer != null
                && snapshot.ShapeSkinnedMeshRenderer.sharedMesh != null,
            _ => false
        };
    }

    static void ConfigureShapePositionMode(PositionShapeBlock block, ParticleEffectSnapshot snapshot)
    {
        var isShell = snapshot.ShapeType == ParticleSystemShapeType.SphereShell
                      || snapshot.ShapeType == ParticleSystemShapeType.HemisphereShell
                      || snapshot.ShapeType == ParticleSystemShapeType.ConeShell
                      || snapshot.ShapeRadiusThickness > 0.001f && snapshot.ShapeRadiusThickness < 0.999f;

        if (isShell)
        {
            block.SetSettingValue("positionMode", VfxBlock.PositionBase.PositionMode.ThicknessRelative);
            block.ResyncSlots(true);
            SetBlockSlotValue(block, "Thickness", Mathf.Clamp01(snapshot.ShapeRadiusThickness));
        }
        else
        {
            block.SetSettingValue("positionMode", VfxBlock.PositionBase.PositionMode.Volume);
        }
    }

    static bool TryConfigureShapeSlots(
        PositionShapeBlock block,
        ParticleEffectSnapshot snapshot,
        VfxBlock.PositionShapeBase.Type shapeType)
    {
        switch (shapeType)
        {
            case VfxBlock.PositionShapeBase.Type.OrientedBox:
                SetBlockSlotValue(block, "Box", BuildOrientedBox(snapshot));
                return true;
            case VfxBlock.PositionShapeBase.Type.Sphere:
                SetBlockSlotValue(block, "arcSphere", BuildArcSphere(snapshot));
                return true;
            case VfxBlock.PositionShapeBase.Type.Cone:
                SetBlockSlotValue(block, "arcCone", BuildArcCone(snapshot));
                return true;
            case VfxBlock.PositionShapeBase.Type.Torus:
                SetBlockSlotValue(block, "arcTorus", BuildArcTorus(snapshot));
                return true;
            case VfxBlock.PositionShapeBase.Type.Circle:
                SetBlockSlotValue(block, "arcCircle", BuildArcCircle(snapshot));
                return true;
            default:
                return false;
        }
    }

    static VfxBlock.PositionShapeBase.Type MapShapeType(ParticleSystemShapeType shapeType)
    {
        switch (shapeType)
        {
            case ParticleSystemShapeType.Sphere:
            case ParticleSystemShapeType.SphereShell:
            case ParticleSystemShapeType.Hemisphere:
            case ParticleSystemShapeType.HemisphereShell:
                return VfxBlock.PositionShapeBase.Type.Sphere;
            case ParticleSystemShapeType.Cone:
            case ParticleSystemShapeType.ConeShell:
                return VfxBlock.PositionShapeBase.Type.Cone;
            case ParticleSystemShapeType.Donut:
                return VfxBlock.PositionShapeBase.Type.Torus;
            case ParticleSystemShapeType.Circle:
                return VfxBlock.PositionShapeBase.Type.Circle;
            case ParticleSystemShapeType.Box:
            case ParticleSystemShapeType.Rectangle:
            case ParticleSystemShapeType.Mesh:
            case ParticleSystemShapeType.MeshRenderer:
            case ParticleSystemShapeType.SkinnedMeshRenderer:
            case ParticleSystemShapeType.Sprite:
            case ParticleSystemShapeType.SpriteRenderer:
            default:
                return VfxBlock.PositionShapeBase.Type.OrientedBox;
        }
    }

    static void AddAttributeBlock(VFXContext context, VFXAttribute attribute, MinMaxSnapshot values)
    {
        var block = CreateSetAttributeBlock(attribute);
        ConfigureRandomOrConstant(block, values);
        context.AddChild(block);
    }

    static void AddColorBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = CreateSetAttributeBlock(VFXAttribute.Color);
        if (snapshot.StartColorTwoColors
            && !ColorsApproximatelyEqual(snapshot.StartColorMin, snapshot.StartColorMax))
        {
            TrySetSettingEnum(block, "Random", "Uniform");
            block.ResyncSlots(true);
            SetBlockSlotValue(block, "A", new Vector3(snapshot.StartColorMin.r, snapshot.StartColorMin.g, snapshot.StartColorMin.b));
            SetBlockSlotValue(block, "B", new Vector3(snapshot.StartColorMax.r, snapshot.StartColorMax.g, snapshot.StartColorMax.b));
        }
        else
        {
            var color = snapshot.StartColor;
            if (!SetBlockSlotValue(block, "_Color", new Vector3(color.r, color.g, color.b)))
                SetBlockSlotValue(block, 0, new Vector3(color.r, color.g, color.b));
        }

        context.AddChild(block);
    }

    static bool ColorsApproximatelyEqual(Color a, Color b)
    {
        return Mathf.Approximately(a.r, b.r)
            && Mathf.Approximately(a.g, b.g)
            && Mathf.Approximately(a.b, b.b)
            && Mathf.Approximately(a.a, b.a);
    }

    static void AddAlphaBlock(VFXContext context, float alpha)
    {
        var block = CreateSetAttributeBlock(VFXAttribute.Alpha);
        if (!SetBlockSlotValue(block, "_Alpha", alpha))
            SetBlockSlotValue(block, 0, alpha);
        context.AddChild(block);
    }

    static void AddTexIndexBlock(VFXContext context, float frameIndex)
    {
        var block = CreateSetAttributeBlock(VFXAttribute.TexIndex);
        SetBlockSlotValue(block, 0, frameIndex);
        context.AddChild(block);
    }

    static float GetBaseSpawnAlpha(ParticleEffectSnapshot snapshot)
    {
        var alpha = !snapshot.StartColorTwoColors
            ? snapshot.StartColor.a
            : (snapshot.StartColorMin.a + snapshot.StartColorMax.a) * 0.5f;

        // Trail-only Shuriken systems often keep particle alpha at 0 while rendering
        // visible ribbons through trail module/material settings.
        if (snapshot.TrailsEnabled && alpha <= 0.001f)
            return Mathf.Max(snapshot.MaterialColor.a, 1f);

        return alpha;
    }

    static void AddSizeBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        AddAttributeBlock(context, VFXAttribute.Size, GetConvertedStartSize(snapshot));
    }

    static MinMaxSnapshot GetBaseStartSize(ParticleEffectSnapshot snapshot)
    {
        if (snapshot.StartSize3D)
            return CombineAxisSizes(snapshot.StartSizeX, snapshot.StartSizeY, snapshot.StartSizeZ);

        return snapshot.StartSize;
    }

    static MinMaxSnapshot GetConvertedStartSize(ParticleEffectSnapshot snapshot)
    {
        var size = GetBaseStartSize(snapshot);
        if (snapshot.SizeOverLifetimeEnabled)
        {
            // Shuriken final size = startSize * sizeOverLifetime(t). The over-life block
            // overwrites size each frame, so the initial value only matters for frame 0.
            var startMultiplier = snapshot.SizeOverLifetime.ToCurve().Evaluate(0f);
            size = ScaleMinMaxSnapshot(size, startMultiplier);
        }

        return size;
    }

    static bool ShouldExposeStartSize(ParticleEffectSnapshot snapshot)
    {
        if (snapshot.SizeOverLifetimeEnabled)
            return false;

        return snapshot.StartSize.Constant > 0.001f || snapshot.StartSize.IsRandom;
    }

    static float GetExposedStartSizeValue(ParticleEffectSnapshot snapshot)
    {
        var converted = GetConvertedStartSize(snapshot);
        return converted.IsRandom ? converted.Min : converted.Constant;
    }

    static MinMaxSnapshot ScaleMinMaxSnapshot(MinMaxSnapshot size, float scale)
    {
        if (Mathf.Approximately(scale, 1f))
            return size;

        if (size.IsRandom)
            return new MinMaxSnapshot(size.Min * scale, size.Max * scale);

        return new MinMaxSnapshot(size.Constant * scale);
    }

    static MinMaxSnapshot CombineAxisSizes(
        MinMaxSnapshot sizeX,
        MinMaxSnapshot sizeY,
        MinMaxSnapshot sizeZ)
    {
        return new MinMaxSnapshot(
            Mathf.Max(sizeX.Min, Mathf.Max(sizeY.Min, sizeZ.Min)),
            Mathf.Max(sizeX.Max, Mathf.Max(sizeY.Max, sizeZ.Max)));
    }

    static void AddVelocityBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        AddStartSpeedBlock(context, snapshot);
    }

    static void AddStartSpeedBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        // Shuriken supports negative start speed (inward emission); only skip when the
        // whole range is effectively zero.
        if (Mathf.Abs(snapshot.StartSpeed.Min) <= 0.001f && Mathf.Abs(snapshot.StartSpeed.Max) <= 0.001f)
            return;

        if (UsesShapeEmissionDirection(snapshot))
            AddVelocityAlongEmissionAxisBlock(context, snapshot);
        else
            AddVelocityRandomSpeedBlock(context, snapshot);
    }

    static bool UsesShapeEmissionDirection(ParticleEffectSnapshot snapshot)
    {
        if (!snapshot.ShapeEnabled)
            return false;

        if (snapshot.ShapeRandomDirectionAmount >= 0.01f
            || snapshot.ShapeSphericalDirectionAmount >= 0.01f)
            return false;

        return snapshot.ShapeType switch
        {
            ParticleSystemShapeType.Sphere or ParticleSystemShapeType.SphereShell
                or ParticleSystemShapeType.Hemisphere or ParticleSystemShapeType.HemisphereShell
                => false,
            _ => true
        };
    }

    static void AddVelocityAlongEmissionAxisBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("VelocityDirection");
        if (block == null)
        {
            snapshot.Warnings.Add("Directional velocity block unavailable; falling back to random velocity.");
            AddVelocityRandomSpeedBlock(context, snapshot);
            return;
        }

        ConfigureSpeedFromDirectionBlock(block, snapshot);
        var direction = GetShapeLocalEmissionAxis(snapshot);
        if (direction.sqrMagnitude <= 0.0001f)
            direction = Vector3.up;
        else
            direction.Normalize();

        SetBlockSlotValue(block, "Direction", BuildDirectionType(direction));
        context.AddChild(block);
    }

    static void AddVelocityRandomSpeedBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("VelocityRandomize");
        if (block == null)
        {
            snapshot.Warnings.Add("Random velocity block unavailable; start speed not converted.");
            return;
        }

        ConfigureSpeedFromDirectionBlock(block, snapshot);
        context.AddChild(block);
    }

    static void ConfigureSpeedFromDirectionBlock(VFXBlock block, ParticleEffectSnapshot snapshot)
    {
        TrySetSetting(block, "composition", VfxBlock.AttributeCompositionMode.Overwrite);
        TrySetBlockEnumSetting(block, "speedMode", "UnityEditor.VFX.Block.VelocityBase+SpeedMode", "Random");
        block.ResyncSlots(true);

        SetBlockSlotValue(block, "MinSpeed", snapshot.StartSpeed.Min);
        SetBlockSlotValue(block, "MaxSpeed", snapshot.StartSpeed.Max);
    }

    static object BuildDirectionType(Vector3 direction)
    {
        return CreateStructValue("UnityEditor.VFX.DirectionType", ("direction", (object)direction));
    }

    static void AddVelocityOverLifetimeBlock(
        VFXContext context,
        ParticleEffectSnapshot snapshot,
        VfxBlock.AttributeCompositionMode composition = VfxBlock.AttributeCompositionMode.Overwrite)
    {
        var velocityMin = new Vector3(snapshot.VelocityX.Min, snapshot.VelocityY.Min, snapshot.VelocityZ.Min);
        var velocityMax = new Vector3(snapshot.VelocityX.Max, snapshot.VelocityY.Max, snapshot.VelocityZ.Max);
        if (velocityMin == Vector3.zero && velocityMax == Vector3.zero)
            return;

        var block = CreateSetAttributeBlock(VFXAttribute.Velocity);
        TrySetSetting(block, "Composition", composition);

        var isRandom = velocityMin != velocityMax;
        if (isRandom)
        {
            TrySetSettingEnum(block, "Random", "PerComponent");
            SetBlockSlotValue(block, 0, (Vector)velocityMin);
            SetBlockSlotValue(block, 1, (Vector)velocityMax);
        }
        else
        {
            SetBlockSlotValue(block, 0, (Vector)velocityMin);
        }

        context.AddChild(block);
    }

    // Velocity-over-lifetime authored as curves varies per axis over the particle's life.
    // Sampling the curve with AttributeFromCurve (Over Life, per component) reproduces the
    // shape faithfully. Overwrite composition is used so the velocity is not re-added every
    // Update tick (which would accumulate); when a non-zero start speed is also present the
    // two cannot both be preserved, so a warning is emitted.
    static void AddVelocityOverLifetimeCurveBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", VFXAttribute.Velocity.name);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.OverLife);
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Overwrite);
        block.SetSettingValue("Mode", VfxBlock.AttributeFromCurve.ComputeMode.PerComponent);
        block.ResyncSlots(true);

        // AttributeFromCurve exposes per-component curve slots Velocity_x/_y/_z for a
        // Vector3 attribute when Mode == PerComponent.
        SetBlockSlotValue(block, "Velocity_x", VelocityAxisToCurve(snapshot.VelocityX));
        SetBlockSlotValue(block, "Velocity_y", VelocityAxisToCurve(snapshot.VelocityY));
        SetBlockSlotValue(block, "Velocity_z", VelocityAxisToCurve(snapshot.VelocityZ));

        context.AddChild(block);

        if (Mathf.Abs(snapshot.StartSpeed.Min) > 0.001f || Mathf.Abs(snapshot.StartSpeed.Max) > 0.001f)
            snapshot.Warnings.Add(
                "Velocity over lifetime curve overwrites velocity each frame; its combination with non-zero start speed is approximated.");
    }

    static AnimationCurve VelocityAxisToCurve(MinMaxSnapshot axis)
    {
        if (axis.UsesCurve)
        {
            if (axis.Mode == MinMaxMode.TwoCurves && axis.CurveMax != null)
                return new AnimationCurve(axis.CurveMax.keys);
            if (axis.Curve != null)
                return new AnimationCurve(axis.Curve.keys);
        }

        // Constant or two-constant axis: a flat curve at the (max) value preserves magnitude.
        var value = axis.IsRandom ? axis.Max : axis.Constant;
        return AnimationCurve.Linear(0f, value, 1f, value);
    }

    static bool HasOrbitalVelocity(ParticleEffectSnapshot snapshot)
    {
        return snapshot.VelocityOverLifetimeEnabled
               && snapshot.OrbitalVelocity.sqrMagnitude > 0.0001f;
    }

    static void AddOrbitalVelocityBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        // Shuriken orbital velocity revolves particles around the system center.
        // VFX Graph has no orbital block, but Tangential Velocity sets velocity along
        // the tangent around an axis, which reproduces the circular orbit path.
        var block = TryCreateBlock("VelocityTangent");
        if (block == null)
        {
            snapshot.Warnings.Add("Tangential velocity block unavailable; orbital motion not converted (trails may look angular).");
            return;
        }

        TrySetSetting(block, "composition", VfxBlock.AttributeCompositionMode.Overwrite);
        TrySetBlockEnumSetting(block, "speedMode", "UnityEditor.VFX.Block.VelocityBase+SpeedMode", "Constant");
        block.ResyncSlots(true);

        var axisDir = snapshot.OrbitalVelocity.normalized;
        var axis = CreateStructValue(
            "UnityEditor.VFX.Line",
            ("start", (object)Vector3.zero),
            ("end", (object)axisDir));
        SetBlockSlotValue(block, "axis", axis);

        // Shuriken orbital velocity is angular (radians/sec); tangential speed scales
        // with the orbit radius. Use the emitter radius as the nominal orbit radius.
        var radius = Mathf.Max(snapshot.ShapeRadius, 0.01f);
        var angularSpeed = snapshot.OrbitalVelocity.magnitude;
        SetBlockSlotValue(block, "Speed", angularSpeed * radius);

        // Keep most of the motion tangential (circular) while retaining a little of the
        // outward emission direction so the orbit spirals like the source radial term.
        SetBlockSlotValue(block, "DirectionBlend", snapshot.RadialVelocity > 0.001f ? 0.9f : 1f);

        context.AddChild(block);
    }

    static void AddTurbulenceBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("Turbulence");
        if (block == null)
        {
            snapshot.Warnings.Add("Turbulence block unavailable.");
            return;
        }

        TrySetSetting(block, "Mode", VfxBlock.ForceMode.Relative);
        TrySetSetting(block, "NoiseType", UnityEditor.VFX.Operator.NoiseBase.NoiseType.Perlin);
        block.ResyncSlots(true);

        var intensity = ComputeTurbulenceIntensity(snapshot);
        var frequency = Mathf.Max(snapshot.NoiseFrequency, 0.01f);
        var octaves = Mathf.Clamp(snapshot.NoiseOctaves, 1, 8);
        var roughness = Mathf.Clamp(snapshot.NoiseRoughness, 0.1f, 1f);
        var lacunarity = Mathf.Max(snapshot.NoiseLacunarity, 0.01f);

        SetBlockSlotValue(block, "Intensity", intensity);
        SetBlockSlotValue(block, "frequency", frequency);
        SetBlockSlotValue(block, "octaves", octaves);
        SetBlockSlotValue(block, "roughness", roughness);
        SetBlockSlotValue(block, "lacunarity", lacunarity);

        // The Turbulence block has no scrolling/animation input, so a non-zero noise
        // scroll speed in the source cannot be reproduced faithfully here.
        if (!Mathf.Approximately(snapshot.NoiseScrollSpeed, 0f))
            snapshot.Warnings.Add(
                "Noise scroll speed has no equivalent on the VFX Turbulence block; skipped.");

        context.AddChild(block);
    }

    static float ComputeTurbulenceIntensity(ParticleEffectSnapshot snapshot)
    {
        var speed = Mathf.Max(snapshot.SimulationSpeed, 0.01f);
        var strength = snapshot.NoiseStrength;
        if (snapshot.NoiseStrengthMin < strength)
            strength = (snapshot.NoiseStrengthMin + snapshot.NoiseStrength) * 0.5f;

        return strength * TurbulenceCurlRangeFactor * TurbulenceAbsoluteModeScale * speed * TurbulenceNoiseBoost;
    }

    static void AddSizeOverLifeBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var multiplierCurve = snapshot.SizeOverLifetime.ToCurve();

        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", VFXAttribute.Size.name);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.OverLife);
        block.ResyncSlots(true);

        // Multiply compounds each frame; overwrite with absolute size matches Shuriken startSize * curve(t).
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Overwrite);
        block.ResyncSlots(true);
        SetBlockSlotValue(block, "Size", ScaleCurve(multiplierCurve, GetSizeOverLifeCurveScale(snapshot)));

        if (!TryGetRepresentativeStartSize(snapshot, out _))
            snapshot.Warnings.Add("Size over lifetime uses max start size for random-sized particles.");

        context.AddChild(block);
    }

    static float GetSizeOverLifeCurveScale(ParticleEffectSnapshot snapshot)
    {
        var size = GetBaseStartSize(snapshot);
        return size.IsRandom ? size.Max : size.Constant;
    }

    static bool TryGetRepresentativeStartSize(ParticleEffectSnapshot snapshot, out float size)
    {
        var startSize = GetConvertedStartSize(snapshot);

        if (startSize.IsRandom && !Mathf.Approximately(startSize.Min, startSize.Max))
        {
            size = 0f;
            return false;
        }

        size = startSize.IsRandom ? startSize.Min : startSize.Constant;
        return size > 0.0001f;
    }

    static void AddColorOverLifeBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (snapshot.ColorOverLifetime == null)
            return;

        var gradient = ParticleToVfxGradientUtility.SimplifyForVfx(
            snapshot.ColorOverLifetime,
            snapshot,
            "Color Over Lifetime");

        AddAttributeOverLifeCurveBlock(
            context,
            VFXAttribute.Alpha,
            ScaleCurve(GradientToAlphaCurve(gradient), GetBaseSpawnAlpha(snapshot)),
            overwrite: true);

        if (GradientHasVaryingColor(gradient))
        {
            AddAttributeOverLifeGradientBlock(context, VFXAttribute.Color, gradient);
        }
    }

    static void AddAttributeOverLifeCurveBlock(
        VFXContext context,
        VFXAttribute attribute,
        AnimationCurve curve,
        bool overwrite = false)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", attribute.name);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.OverLife);
        block.ResyncSlots(true);

        var slotName = char.ToUpper(attribute.name[0]) + attribute.name.Substring(1);

        if (overwrite)
        {
            TrySetSetting(block, "Composition", VfxBlock.AttributeCompositionMode.Overwrite);
            block.ResyncSlots(true);
            SetBlockSlotValue(block, slotName, curve);
            context.AddChild(block);
            return;
        }

        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Blend);
        block.ResyncSlots(true);
        SetBlockSlotValue(block, slotName, curve);
        SetBlockSlotValue(block, "Blend", 1f);
        context.AddChild(block);
    }

    static void AddAttributeOverLifeGradientBlock(
        VFXContext context,
        VFXAttribute attribute,
        Gradient gradient)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", attribute.name);
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Blend);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.OverLife);
        // PerComponent (not Uniform) is required for a color attribute: Uniform exposes a
        // single AnimationCurve slot, whereas PerComponent exposes the Gradient slot we assign.
        block.SetSettingValue("Mode", VfxBlock.AttributeFromCurve.ComputeMode.PerComponent);
        block.SetSettingValue("ColorMode", VfxBlock.AttributeFromCurve.ColorApplicationMode.ColorAndAlpha);
        block.ResyncSlots(true);

        var slotName = char.ToUpper(attribute.name[0]) + attribute.name.Substring(1);
        SetBlockSlotValue(block, slotName, gradient);
        SetBlockSlotValue(block, "Blend", 1f);
        context.AddChild(block);
    }

    static AnimationCurve GradientToAlphaCurve(Gradient gradient)
    {
        var alphaKeys = gradient.alphaKeys;
        if (alphaKeys == null || alphaKeys.Length == 0)
            return AnimationCurve.Linear(0f, 1f, 1f, 1f);

        var keys = new Keyframe[alphaKeys.Length];
        for (var i = 0; i < alphaKeys.Length; i++)
            keys[i] = new Keyframe(alphaKeys[i].time, alphaKeys[i].alpha);

        return new AnimationCurve(keys);
    }

    static bool GradientHasVaryingColor(Gradient gradient)
    {
        var colorKeys = gradient.colorKeys;
        if (colorKeys == null || colorKeys.Length <= 1)
            return false;

        var first = colorKeys[0].color;
        for (var i = 1; i < colorKeys.Length; i++)
        {
            if (colorKeys[i].color != first)
                return true;
        }

        return false;
    }

    static void AddOrientBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (snapshot.RenderMode == ParticleSystemRenderMode.Mesh || snapshot.TrailsEnabled)
            return;

        var block = TryCreateBlock("Orient");
        if (block == null)
            return;

        var orientMode = MapOrientMode(snapshot, out var axesWarning, out var fixedAxes);
        if (!string.IsNullOrEmpty(axesWarning))
            snapshot.Warnings.Add(axesWarning);

        TrySetBlockEnumSetting(block, "mode", "UnityEditor.VFX.Block.Orient+Mode", orientMode);
        if (orientMode == "Advanced" && !string.IsNullOrEmpty(fixedAxes))
        {
            TrySetBlockEnumSetting(block, "axes", "UnityEditor.VFX.Block.Orient+AxesPair", fixedAxes);
            block.ResyncSlots(true);
            ConfigureAdvancedOrientAxes(block, fixedAxes);
        }
        else
        {
            block.ResyncSlots(true);
        }

        context.AddChild(block);
    }

    static void ConfigureAdvancedOrientAxes(VFXBlock block, string axesPair)
    {
        if (!TryGetAdvancedOrientAxisSlots(axesPair, out var slot1, out var direction1, out var slot2, out var direction2))
            return;

        SetBlockSlotValue(block, slot1, BuildDirectionType(direction1));
        SetBlockSlotValue(block, slot2, BuildDirectionType(direction2));
    }

    static bool TryGetAdvancedOrientAxisSlots(
        string axesPair,
        out string slot1,
        out Vector3 direction1,
        out string slot2,
        out Vector3 direction2)
    {
        slot1 = null;
        slot2 = null;
        direction1 = Vector3.zero;
        direction2 = Vector3.zero;

        switch (axesPair)
        {
            case "XY":
                slot1 = "AxisX"; direction1 = Vector3.right;
                slot2 = "AxisY"; direction2 = Vector3.up;
                return true;
            case "XZ":
                // Horizontal billboard: quad spans X and Z with normal +Y.
                // Advanced Orient maps slot1 -> axisX, slot2 -> axisZ (normal), axisY = cross(normal, axisX).
                slot1 = "AxisX"; direction1 = Vector3.right;
                slot2 = "AxisZ"; direction2 = Vector3.up;
                return true;
            case "YX":
                slot1 = "AxisY"; direction1 = Vector3.up;
                slot2 = "AxisX"; direction2 = Vector3.right;
                return true;
            case "YZ":
                slot1 = "AxisY"; direction1 = Vector3.up;
                slot2 = "AxisZ"; direction2 = Vector3.forward;
                return true;
            case "ZX":
                slot1 = "AxisZ"; direction1 = Vector3.forward;
                slot2 = "AxisX"; direction2 = Vector3.right;
                return true;
            case "ZY":
                // Vertical billboard: quad spans Z and Y with normal +X.
                slot1 = "AxisZ"; direction1 = Vector3.forward;
                slot2 = "AxisY"; direction2 = Vector3.right;
                return true;
            default:
                return false;
        }
    }

    static string MapOrientMode(ParticleEffectSnapshot snapshot, out string warning, out string fixedAxes)
    {
        warning = null;
        fixedAxes = null;
        switch (snapshot.RenderMode)
        {
            case ParticleSystemRenderMode.Stretch:
                return "AlongVelocity";
            case ParticleSystemRenderMode.HorizontalBillboard:
                fixedAxes = "XZ";
                warning = "Horizontal billboard approximated with advanced fixed-axis XZ orientation.";
                return "Advanced";
            case ParticleSystemRenderMode.VerticalBillboard:
                fixedAxes = "ZY";
                warning = "Vertical billboard approximated with advanced fixed-axis ZY orientation.";
                return "Advanced";
            case ParticleSystemRenderMode.Billboard:
            default:
                return snapshot.RenderAlignment == ParticleSystemRenderSpace.Velocity
                    ? "AlongVelocity"
                    : "FaceCameraPlane";
        }
    }

    static Quaternion GetShapeOrientation(ParticleEffectSnapshot snapshot)
    {
        return Quaternion.Euler(snapshot.SpawnRotation) * Quaternion.Euler(snapshot.ShapeRotation);
    }

    static Vector3 GetShapeLocalEmissionAxis(ParticleEffectSnapshot snapshot)
    {
        return GetShapeOrientation(snapshot) * Vector3.forward;
    }

    static VfxBlock.SetAttribute CreateSetAttributeBlock(VFXAttribute attribute)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.SetAttribute>();
        block.SetSettingValue("attribute", attribute.name);
        return block;
    }

    static VFXBlock TryCreateBlock(params string[] typeNames)
    {
        var assembly = typeof(VFXBlock).Assembly;
        foreach (var typeName in typeNames)
        {
            var type = assembly.GetType($"UnityEditor.VFX.Block.{typeName}")
                       ?? assembly.GetType($"UnityEditor.VFX.Block.Implementations.{typeName}");
            if (type != null && typeof(VFXBlock).IsAssignableFrom(type))
                return (VFXBlock)ScriptableObject.CreateInstance(type);
        }

        return null;
    }

    static void ConfigureRandomOrConstant(VFXBlock block, MinMaxSnapshot values)
    {
        if (values.IsRandom && !Mathf.Approximately(values.Min, values.Max))
        {
            TrySetSettingEnum(block, "Random", "Uniform");
            SetBlockSlotValue(block, 0, values.Min);
            SetBlockSlotValue(block, 1, values.Max);
        }
        else
        {
            SetBlockSlotValue(block, 0, values.Constant);
        }
    }

    static Vector3 GetShapeCenter(ParticleEffectSnapshot snapshot)
    {
        return snapshot.ShapePosition + snapshot.SpawnOffset;
    }

    static Vector3 GetShapeAngles(ParticleEffectSnapshot snapshot)
    {
        return GetShapeOrientation(snapshot).eulerAngles;
    }

    static object BuildOrientedBox(ParticleEffectSnapshot snapshot)
    {
        var size = snapshot.ShapeScale;
        if (size == Vector3.zero)
            size = Vector3.one;

        return CreateStructValue(
            "UnityEditor.VFX.OrientedBox",
            ("center", (object)GetShapeCenter(snapshot)),
            ("angles", GetShapeAngles(snapshot)),
            ("size", size));
    }

    static object BuildShapeTransform(ParticleEffectSnapshot snapshot)
    {
        return CreateStructValue(
            "UnityEditor.VFX.Transform",
            ("position", (object)GetShapeCenter(snapshot)),
            ("angles", GetShapeAngles(snapshot)),
            ("scale", Vector3.one));
    }

    static float GetShapeArcRadians(ParticleEffectSnapshot snapshot)
    {
        var arcDegrees = snapshot.ShapeArc;
        if (arcDegrees <= 0.001f)
        {
            if (snapshot.ShapeType == ParticleSystemShapeType.Hemisphere
                || snapshot.ShapeType == ParticleSystemShapeType.HemisphereShell)
                arcDegrees = 180f;
            else
                arcDegrees = 360f;
        }

        return arcDegrees * Mathf.Deg2Rad;
    }

    static float GetScaledRadius(ParticleEffectSnapshot snapshot)
    {
        var scale = snapshot.ShapeScale;
        if (scale == Vector3.zero)
            scale = Vector3.one;

        var radius = Mathf.Max(snapshot.ShapeRadius, 0.01f);
        return radius * Mathf.Max(scale.x, scale.y, scale.z);
    }

    static object BuildArcSphere(ParticleEffectSnapshot snapshot)
    {
        var sphere = CreateStructValue(
            "UnityEditor.VFX.TSphere",
            ("transform", BuildShapeTransform(snapshot)),
            ("radius", GetScaledRadius(snapshot)));

        return CreateStructValue(
            "UnityEditor.VFX.TArcSphere",
            ("sphere", sphere),
            ("arc", GetShapeArcRadians(snapshot)));
    }

    static object BuildArcCone(ParticleEffectSnapshot snapshot)
    {
        var radius = GetScaledRadius(snapshot);
        var height = snapshot.ShapeLength;
        if (height <= 0.001f && snapshot.ShapeAngle > 0.001f)
            height = radius / Mathf.Tan(snapshot.ShapeAngle * 0.5f * Mathf.Deg2Rad);
        if (height <= 0.001f)
            height = 1f;

        var isShell = snapshot.ShapeType == ParticleSystemShapeType.ConeShell;
        var topRadius = isShell
            ? radius * Mathf.Clamp01(1f - snapshot.ShapeRadiusThickness)
            : 0f;

        var cone = CreateStructValue(
            "UnityEditor.VFX.TCone",
            ("transform", BuildShapeTransform(snapshot)),
            ("baseRadius", radius),
            ("topRadius", topRadius),
            ("height", height));

        return CreateStructValue(
            "UnityEditor.VFX.TArcCone",
            ("cone", cone),
            ("arc", GetShapeArcRadians(snapshot)));
    }

    static object BuildArcTorus(ParticleEffectSnapshot snapshot)
    {
        var majorRadius = Mathf.Max(snapshot.ShapeRadius, 0.01f);
        var minorRadius = Mathf.Max(snapshot.ShapeDonutRadius, 0.01f);

        var torus = CreateStructValue(
            "UnityEditor.VFX.TTorus",
            ("transform", BuildShapeTransform(snapshot)),
            ("majorRadius", majorRadius),
            ("minorRadius", minorRadius));

        return CreateStructValue(
            "UnityEditor.VFX.TArcTorus",
            ("torus", torus),
            ("arc", GetShapeArcRadians(snapshot)));
    }

    static object BuildArcCircle(ParticleEffectSnapshot snapshot)
    {
        var circle = CreateStructValue(
            "UnityEditor.VFX.TCircle",
            ("transform", BuildShapeTransform(snapshot)),
            ("radius", GetScaledRadius(snapshot)));

        return CreateStructValue(
            "UnityEditor.VFX.TArcCircle",
            ("circle", circle),
            ("arc", GetShapeArcRadians(snapshot)));
    }

    static object CreateStructValue(string typeName, params (string fieldName, object value)[] fields)
    {
        var type = typeof(VFXBlock).Assembly.GetType(typeName);
        if (type == null)
            return null;

        var value = Activator.CreateInstance(type);
        foreach (var (fieldName, fieldValue) in fields)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            field?.SetValue(value, fieldValue);
        }

        return value;
    }

    static bool SetBlockSlotValue(VFXBlock block, int index, object value)
    {
        if (block == null)
            return false;

        if (block is not IVFXSlotContainer slotContainer || index < 0 || index >= slotContainer.GetNbInputSlots())
            return false;

        var slot = block.GetInputSlot(index);
        return TryAssignSlotValue(slot, value);
    }

    static bool SetBlockSlotValue(VFXBlock block, string propertyName, object value)
    {
        if (block == null)
            return false;

        if (TrySetNamedSlotValue(block, propertyName, value))
            return true;

        // Console only: callers often probe a named slot and fall back to an index, so this
        // is not necessarily a definitive failure and should not spam the conversion report.
        Debug.LogWarning($"Could not set VFX block slot '{propertyName}' on '{block?.GetType().Name}'.");
        return false;
    }

    static bool SetContextSlotValue(VFXContext context, string propertyName, object value, bool logFailure = true)
    {
        if (context == null)
            return false;

        if (TrySetNamedSlotValue(context, propertyName, value))
            return true;

        if (logFailure)
            Debug.LogWarning($"Could not set VFX context slot '{propertyName}' on '{context?.GetType().Name}'.");
        return false;
    }

    static bool TrySetNamedSlotValue(object owner, string propertyName, object value)
    {
        if (owner == null)
            return false;

        var ownerType = owner.GetType();
        var stringSlotMethod = ownerType.GetMethod(
            "GetInputSlot",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        if (stringSlotMethod != null)
        {
            var slot = stringSlotMethod.Invoke(owner, new object[] { propertyName });
            return TryAssignSlotValue(slot, value);
        }

        var intSlotMethod = ownerType.GetMethod(
            "GetInputSlot",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(int) },
            null);
        if (intSlotMethod == null)
            return false;

        var slotCount = GetInputSlotCount(owner);
        for (var i = 0; i < slotCount; i++)
        {
            object slot;
            try
            {
                slot = intSlotMethod.Invoke(owner, new object[] { i });
            }
            catch (TargetInvocationException)
            {
                break;
            }

            if (slot == null)
                break;

            if (string.Equals(GetSlotPropertyName(slot), propertyName, StringComparison.OrdinalIgnoreCase))
                return TryAssignSlotValue(slot, value);
        }

        return false;
    }

    static int GetInputSlotCount(object owner)
    {
        return owner is IVFXSlotContainer slotContainer ? slotContainer.GetNbInputSlots() : 0;
    }

    static string GetSlotPropertyName(object slot)
    {
        if (slot is VFXSlot vfxSlot)
        {
            var propertyName = vfxSlot.property.name;
            return string.IsNullOrEmpty(propertyName) ? null : propertyName;
        }

        return null;
    }

    static bool TryAssignSlotValue(object slot, object value)
    {
        if (slot == null)
            return false;

        var valueProperty = slot.GetType().GetProperty(
            "value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (valueProperty == null || !valueProperty.CanWrite)
            return false;

        try
        {
            valueProperty.SetValue(slot, CoerceSlotValue(slot, value));
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to assign VFX slot value: {ex.Message}");
            return false;
        }
    }

    static object CoerceSlotValue(object slot, object value)
    {
        if (value is Gradient gradient)
            value = ParticleToVfxGradientUtility.SimplifyForVfx(gradient);

        if (value is UnityEngine.Object unityObject && !ParticleToVfxAssetReferenceGuard.IsSafeForVfxAsset(unityObject))
        {
            Debug.LogWarning(
                $"Skipping non-persistent {unityObject.GetType().Name} '{unityObject.name}' for VFX asset serialization.");
            return null;
        }

        if (value == null)
            return null;

        var slotTypeName = slot.GetType().Name;

        // Scalar float slots reject boxed integers, so widen int/double to float.
        if (slotTypeName == "VFXSlotFloat")
        {
            if (value is int intValue)
                return (float)intValue;
            if (value is double doubleValue)
                return (float)doubleValue;
        }

        if (value is Color color)
            value = new Vector3(color.r, color.g, color.b);

        if (value is Vector4 vector4)
            value = new Vector3(vector4.x, vector4.y, vector4.z);

        // Position/Direction slots use dedicated struct types (UnityEditor.VFX.Position /
        // DirectionType) rather than a bare Vector, so a Vector(3) must be wrapped.
        if (TryGetVector3(value, out var coords))
        {
            if (slotTypeName.Contains("Position"))
                return CreateStructValue("UnityEditor.VFX.Position", ("position", (object)coords));
            if (slotTypeName.Contains("Direction"))
                return CreateStructValue("UnityEditor.VFX.DirectionType", ("direction", (object)coords));
        }

        if (slotTypeName.Contains("Float3") || slotTypeName.Contains("Vector3"))
        {
            if (value is Vector vectorValue)
                return vectorValue.vector;
            return value;
        }

        if (value is Vector3 vector3 && slotTypeName.Contains("Vector"))
            return (Vector)vector3;

        return value;
    }

    static bool TryGetVector3(object value, out Vector3 result)
    {
        switch (value)
        {
            case Vector3 v3:
                result = v3;
                return true;
            case Vector vectorWrapper:
                result = vectorWrapper.vector;
                return true;
            default:
                result = Vector3.zero;
                return false;
        }
    }

    static void TrySetSetting(VFXBlock block, string settingName, object value)
    {
        try
        {
            block.SetSettingValue(settingName, value);
        }
        catch (Exception ex)
        {
            // Settings names/enum types can differ across VFX Graph versions. Report the
            // failure (it used to be swallowed) so a package upgrade that breaks a mapping
            // is visible in the console and the conversion report rather than silent.
            ReportBuildFailure(
                $"Could not set setting '{settingName}' on block '{block?.GetType().Name}': {ex.Message}");
        }
    }

    static void TrySetSettingEnum(VFXBlock block, string settingName, string enumLiteral)
    {
        if (TrySetBlockEnumSetting(block, settingName, "UnityEditor.VFX.Block.RandomMode", enumLiteral)
            || TrySetBlockEnumSetting(block, settingName, "UnityEditor.VFX.Block+RandomMode", enumLiteral))
            return;

        TrySetSetting(block, settingName, enumLiteral);
    }

    static bool TrySetBlockEnumSetting(VFXBlock block, string settingName, string enumTypeName, string enumLiteral)
    {
        var enumType = typeof(VFXBlock).Assembly.GetType(enumTypeName);
        if (enumType == null || !Enum.TryParse(enumType, enumLiteral, out var enumValue))
            return false;

        TrySetSetting(block, settingName, enumValue);
        return true;
    }
}
