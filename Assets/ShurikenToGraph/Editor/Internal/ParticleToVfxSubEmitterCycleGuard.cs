using System.Collections.Generic;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

public static class ParticleToVfxSubEmitterCycleGuard
{
    const int MaxSubgraphNestDepth = 32;

    public static void ApplySubEmitterCycleGuards(IReadOnlyList<ParticleEffectSnapshot> systems)
    {
        if (systems == null || systems.Count == 0)
            return;

        var adjacency = new Dictionary<int, List<int>>();
        for (var i = 0; i < systems.Count; i++)
            adjacency[i] = new List<int>();

        foreach (var system in systems)
        {
            foreach (var subEmitter in system.SubEmitters)
            {
                if (subEmitter.TargetSystemIndex < 0 || subEmitter.TargetSystemIndex >= systems.Count)
                    continue;

                var parentIndex = system.SystemIndex;
                var targetIndex = subEmitter.TargetSystemIndex;

                if (parentIndex == targetIndex)
                {
                    subEmitter.SkipAutoWire = true;
                    system.Warnings.Add(
                        $"Sub-emitter targets itself ('{system.SourceName}'); auto-wiring skipped to avoid infinite recursion.");
                    continue;
                }

                if (PathExists(targetIndex, parentIndex, adjacency))
                {
                    subEmitter.SkipAutoWire = true;
                    system.Warnings.Add(
                        $"Sub-emitter cycle detected ({system.SourceName} -> {systems[targetIndex].SourceName}); auto-wiring skipped to avoid infinite recursion.");
                    continue;
                }

                adjacency[parentIndex].Add(targetIndex);
            }
        }
    }

    public static bool ValidateSubgraphEmbedding(
        VisualEffectAsset subgraphAsset,
        ParticleEffectSnapshot snapshot,
        HashSet<VisualEffectAsset> embeddingStack)
    {
        if (subgraphAsset == null)
            return true;

        if (embeddingStack.Contains(subgraphAsset))
        {
            snapshot?.Warnings.Add(
                $"Subgraph '{snapshot.SourceName}' is already being embedded; nested subgraph cycle skipped.");
            return false;
        }

        embeddingStack.Add(subgraphAsset);
        try
        {
            return ValidateNestedSubgraphReferences(subgraphAsset, embeddingStack, 0, snapshot);
        }
        finally
        {
            embeddingStack.Remove(subgraphAsset);
        }
    }

    static bool ValidateNestedSubgraphReferences(
        VisualEffectAsset asset,
        HashSet<VisualEffectAsset> embeddingStack,
        int depth,
        ParticleEffectSnapshot snapshot)
    {
        if (asset == null)
            return true;

        if (depth > MaxSubgraphNestDepth)
        {
            snapshot?.Warnings.Add(
                $"Subgraph nesting exceeds max depth ({MaxSubgraphNestDepth}) under '{snapshot?.SourceName ?? asset.name}'; stopped traversal to avoid infinite recursion.");
            return false;
        }

        var resource = asset.GetResource();
        var graph = resource?.GetOrCreateGraph();
        if (graph == null)
            return true;

        foreach (var child in graph.children)
        {
            if (child is not VFXSubgraphContext subgraphContext)
                continue;

            var nested = subgraphContext.subgraph;
            if (nested == null)
                continue;

            if (embeddingStack.Contains(nested))
            {
                snapshot?.Warnings.Add(
                    $"Nested subgraph cycle detected: '{snapshot?.SourceName ?? asset.name}' references '{nested.name}' which is already in the embed chain.");
                return false;
            }

            embeddingStack.Add(nested);
            try
            {
                if (!ValidateNestedSubgraphReferences(nested, embeddingStack, depth + 1, snapshot))
                    return false;
            }
            finally
            {
                embeddingStack.Remove(nested);
            }
        }

        return true;
    }

    static bool PathExists(int from, int to, IReadOnlyDictionary<int, List<int>> adjacency)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(from);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node == to)
                return true;

            if (!visited.Add(node))
                continue;

            if (!adjacency.TryGetValue(node, out var neighbors))
                continue;

            for (var i = 0; i < neighbors.Count; i++)
                stack.Push(neighbors[i]);
        }

        return false;
    }
}
