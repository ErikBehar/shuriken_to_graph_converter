using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

public static partial class ParticleToVfxGraphBuilder
{
    const float SubgraphContextYOffset = 300f;

    static VisualEffectAsset BuildComposedSubgraphGraph(
        ParticleEffectHierarchySnapshot hierarchy,
        string outputFolder,
        out string assetPath)
    {
        var safeName = ParticleToVfxConversionService.MakeSafeFileName(hierarchy.RootName);
        var subgraphFolder = $"{outputFolder}/{safeName}_Subgraphs";
        Directory.CreateDirectory(subgraphFolder);

        var builtSystems = new BuiltParticleSystem[hierarchy.Systems.Count];
        var subgraphAssets = new VisualEffectAsset[hierarchy.Systems.Count];

        for (var i = 0; i < hierarchy.Systems.Count; i++)
        {
            var snapshot = hierarchy.Systems[i];
            var preferGpuEntry = ShouldUseGpuEventEntry(snapshot, hierarchy);
            builtSystems[i] = BuildSystemChain(snapshot, 0, preferGpuEntry);
            AddTriggerBlocksForOutgoingSubEmitters(builtSystems[i]);
        }

        for (var i = 0; i < hierarchy.Systems.Count; i++)
        {
            var snapshot = hierarchy.Systems[i];
            var subgraphPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{subgraphFolder}/{i:00}_{ParticleToVfxConversionService.MakeSafeFileName(snapshot.SourceName)}.vfx");

            if (!ParticleToVfxAssetPaths.CopySeedTemplateTo(subgraphPath, overwrite: true))
                throw new InvalidOperationException($"Failed to copy VFX template to '{subgraphPath}'.");

            snapshot.SubgraphAssetPath = subgraphPath;
            hierarchy.SubgraphAssetPaths.Add(subgraphPath);
        }

        for (var i = 0; i < hierarchy.Systems.Count; i++)
        {
            var snapshot = hierarchy.Systems[i];
            var subgraphPath = snapshot.SubgraphAssetPath;
            var asset = ParticleToVfxAssetPaths.ImportAndLoadVfxAsset(subgraphPath);
            if (asset == null)
                throw new InvalidOperationException($"Failed to load subgraph asset at '{subgraphPath}'.");

            subgraphAssets[i] = SaveBuiltSystemAsSubgraphAsset(builtSystems[i], subgraphPath, asset);
        }

        assetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{safeName}_VFX.vfx");
        var parentAsset = ParticleToVfxAssetPaths.CreateVfxAssetFromSeed(assetPath);

        var parentResource = parentAsset.GetResource();
        var parentGraph = parentResource.GetOrCreateGraph();
        parentGraph.RemoveAllChildren();

        var subgraphContexts = new VFXSubgraphContext[hierarchy.Systems.Count];
        var embeddingStack = new HashSet<VisualEffectAsset>();
        for (var i = 0; i < hierarchy.Systems.Count; i++)
        {
            subgraphContexts[i] = EmbedSubgraph(
                parentGraph,
                subgraphAssets[i],
                hierarchy.Systems[i],
                i,
                embeddingStack);
        }

        WireSubEmitterCrossLinks(hierarchy, subgraphContexts);
        AddParentComposerBlackboardParameters(parentGraph, hierarchy, subgraphContexts);
        AddHierarchyBlackboardParameters(parentGraph, hierarchy);

        if (hierarchy.Systems[0].Prewarm)
            ConfigurePrewarm(parentResource, hierarchy.Systems[0]);

        FinalizeGraphAsset(parentResource, assetPath);
        AssetDatabase.SaveAssets();
        return parentAsset;
    }

    static VisualEffectAsset SaveBuiltSystemAsSubgraphAsset(
        BuiltParticleSystem built,
        string assetPath,
        VisualEffectAsset asset)
    {
        Debug.Log($"[ParticleToVfx] Writing subgraph '{built.Snapshot.SourceName}' -> '{assetPath}'");

        if (asset == null)
            throw new InvalidOperationException($"Subgraph asset is null for '{assetPath}'.");

        var resource = asset.GetResource();
        var graph = resource.GetOrCreateGraph();
        graph.RemoveAllChildren();
        AddBuiltSystemToGraph(graph, built);
        AddExposedBlackboardParameters(graph, built);
        FinalizeGraphAsset(resource, assetPath);
        return asset;
    }

    static void AddTriggerBlocksForOutgoingSubEmitters(BuiltParticleSystem built)
    {
        foreach (var subEmitter in built.Snapshot.SubEmitters)
        {
            if (subEmitter.TargetSystemIndex < 0 || subEmitter.SkipAutoWire)
                continue;

            var triggerMode = GetTriggerModeLiteral(subEmitter.Type);
            if (triggerMode == null)
                continue;

            var triggerContext = triggerMode == "Always" ? built.Initialize : built.Update;
            var spawnCount = ResolveSubEmitterSpawnCount(subEmitter, built.Snapshot);
            var triggerBlock = CreateTriggerEventBlock(triggerMode, spawnCount);
            if (triggerBlock == null)
                continue;

            triggerContext.AddChild(triggerBlock);
            built.TriggerBlocks.Add(triggerBlock);
        }
    }

    static VFXSubgraphContext EmbedSubgraph(
        VFXGraph parentGraph,
        VisualEffectAsset subgraphAsset,
        ParticleEffectSnapshot snapshot,
        int index,
        HashSet<VisualEffectAsset> embeddingStack)
    {
        if (!ParticleToVfxSubEmitterCycleGuard.ValidateSubgraphEmbedding(subgraphAsset, snapshot, embeddingStack))
            return null;

        var context = ScriptableObject.CreateInstance<VFXSubgraphContext>();
        context.label = snapshot.SourceName;
        context.position = new Vector2(index * SystemXOffset, index * SubgraphContextYOffset);
        parentGraph.AddChild(context);

        context.SetSettingValue("m_Subgraph", subgraphAsset);
        try
        {
            context.RecreateCopy();
            context.ResyncSlots(true);
            context.PatchInputExpressions();
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add(
                $"Subgraph embed failed for '{snapshot.SourceName}': {ex.Message}. Skipping context to avoid editor instability.");
            parentGraph.RemoveChild(context);
            UnityEngine.Object.DestroyImmediate(context);
            return null;
        }

        return context;
    }

    static void WireSubEmitterCrossLinks(
        ParticleEffectHierarchySnapshot hierarchy,
        VFXSubgraphContext[] subgraphContexts)
    {
        for (var parentIndex = 0; parentIndex < hierarchy.Systems.Count; parentIndex++)
        {
            var parentSnapshot = hierarchy.Systems[parentIndex];
            var parentContext = subgraphContexts[parentIndex];

            foreach (var subEmitter in parentSnapshot.SubEmitters)
            {
                if (subEmitter.TargetSystemIndex < 0 || subEmitter.TargetSystemIndex >= hierarchy.Systems.Count)
                    continue;

                if (subEmitter.SkipAutoWire)
                    continue;

                var childContext = subgraphContexts[subEmitter.TargetSystemIndex];
                if (parentContext == null || childContext == null)
                {
                    if (childContext == null)
                        parentSnapshot.Warnings.Add(
                            $"Sub-emitter target '{hierarchy.Systems[subEmitter.TargetSystemIndex].SourceName}' has no embedded subgraph context.");
                    continue;
                }

                if (!TryWireSubEmitterCrossLink(
                        parentSnapshot,
                        parentContext,
                        subgraphContexts[subEmitter.TargetSystemIndex],
                        subEmitter))
                {
                    parentSnapshot.Warnings.Add(
                        $"Sub-emitter '{subEmitter.Type}' -> '{hierarchy.Systems[subEmitter.TargetSystemIndex].SourceName}' could not be wired in composer.");
                }
            }
        }
    }

    static bool TryWireSubEmitterCrossLink(
        ParticleEffectSnapshot parentSnapshot,
        VFXSubgraphContext parentContext,
        VFXSubgraphContext childContext,
        SubEmitterSnapshot subEmitter)
    {
        var triggerMode = GetTriggerModeLiteral(subEmitter.Type);
        if (triggerMode == null)
        {
            parentSnapshot.Warnings.Add(
                $"Sub-emitter type '{subEmitter.Type}' is not auto-wired in composed subgraphs.");
            return false;
        }

        var parentSubChildren = GetSubgraphChildren(parentContext);
        var childSubChildren = GetSubgraphChildren(childContext);
        if (parentSubChildren == null || childSubChildren == null)
            return false;

        // A parent may have several sub-emitters of the same trigger mode, each of which
        // produced its own TriggerEvent block (in sub-emitter order). Select the block at
        // this sub-emitter's ordinal so distinct targets don't all bind to the first block.
        var ordinal = GetSubEmitterModeOrdinal(parentSnapshot, subEmitter, triggerMode);
        var parentTrigger = FindTriggerBlockAt(parentSubChildren, triggerMode, ordinal);
        var childGpuEvent = childSubChildren.OfType<VFXBasicGPUEvent>().FirstOrDefault();
        if (parentTrigger == null || childGpuEvent == null)
        {
            if (childGpuEvent == null)
                parentSnapshot.Warnings.Add("Child sub-emitter system has no GPU Event entry for cross-graph wiring.");
            return false;
        }

        return TryLinkEventSlots(parentTrigger, childGpuEvent);
    }

    // Counts how many earlier auto-wired sub-emitters share this sub-emitter's trigger mode,
    // matching the order in which AddTriggerBlocksForOutgoingSubEmitters created the blocks.
    static int GetSubEmitterModeOrdinal(
        ParticleEffectSnapshot parentSnapshot,
        SubEmitterSnapshot target,
        string triggerMode)
    {
        var ordinal = 0;
        foreach (var subEmitter in parentSnapshot.SubEmitters)
        {
            if (subEmitter == target)
                return ordinal;

            if (subEmitter.TargetSystemIndex < 0 || subEmitter.SkipAutoWire)
                continue;

            if (GetTriggerModeLiteral(subEmitter.Type) == triggerMode)
                ordinal++;
        }

        return ordinal;
    }

    static string GetTriggerModeLiteral(ParticleSystemSubEmitterType type)
    {
        return type switch
        {
            ParticleSystemSubEmitterType.Birth => "Always",
            ParticleSystemSubEmitterType.Death => "OnDie",
            ParticleSystemSubEmitterType.Collision => "OnCollide",
            _ => null
        };
    }

    static VFXBlock FindTriggerBlockAt(IEnumerable<VFXModel> subChildren, string modeLiteral, int ordinal)
    {
        var matchIndex = 0;
        foreach (var context in subChildren.OfType<VFXContext>())
        {
            foreach (var block in context.children.OfType<VFXBlock>())
            {
                if (block.GetType().Name != "TriggerEvent")
                    continue;
                if (!block.name.Contains(modeLiteral, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (matchIndex == ordinal)
                    return block;
                matchIndex++;
            }
        }

        return null;
    }

    static IEnumerable<VFXModel> GetSubgraphChildren(VFXSubgraphContext context)
    {
        var field = typeof(VFXSubgraphContext).GetField(
            "m_SubChildren",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(context) as IEnumerable<VFXModel>;
    }

    static bool ShouldUseGpuEventEntry(ParticleEffectSnapshot snapshot, ParticleEffectHierarchySnapshot hierarchy)
    {
        var targeted = hierarchy.Systems.Any(system =>
            system.SubEmitters.Any(subEmitter => subEmitter.TargetSystemIndex == snapshot.SystemIndex));

        var hasEmission = snapshot.EmissionRate > 0.001f || snapshot.Bursts.Count > 0;
        if (targeted && hasEmission)
            snapshot.Warnings.Add("System is both independently emitted and used as a sub-emitter; keeping spawner entry (GPU-only entry skipped).");

        return targeted && !hasEmission;
    }
}
