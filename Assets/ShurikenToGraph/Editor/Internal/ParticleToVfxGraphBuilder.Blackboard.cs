using System;
using System.Linq;
using System.Reflection;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

public static partial class ParticleToVfxGraphBuilder
{
    internal static void AddExposedBlackboardParameters(VFXGraph graph, BuiltParticleSystem built)
    {
        if (graph == null || built == null)
            return;

        AddSystemBlackboardParameters(graph, built, built.Snapshot.SourceName, prefixNames: false);
    }

    internal static void AddParentComposerBlackboardParameters(
        VFXGraph parentGraph,
        ParticleEffectHierarchySnapshot hierarchy,
        VFXSubgraphContext[] subgraphContexts)
    {
        if (parentGraph == null || hierarchy == null || subgraphContexts == null)
            return;

        for (var i = 0; i < hierarchy.Systems.Count && i < subgraphContexts.Length; i++)
        {
            var snapshot = hierarchy.Systems[i];
            var subgraphContext = subgraphContexts[i];
            if (subgraphContext == null)
                continue;

            ExposeAndLinkParentParameter(
                parentGraph,
                subgraphContext,
                snapshot,
                typeof(float),
                BlackboardNames.EmissionRate,
                snapshot.EmissionRate);

            ExposeAndLinkParentParameter(
                parentGraph,
                subgraphContext,
                snapshot,
                typeof(Texture2D),
                BlackboardNames.MainTexture,
                snapshot.MainTexture);

            ExposeAndLinkParentParameter(
                parentGraph,
                subgraphContext,
                snapshot,
                typeof(float),
                BlackboardNames.StartLifetime,
                snapshot.StartLifetime.Constant);

            if (ShouldExposeStartSize(snapshot))
            {
                ExposeAndLinkParentParameter(
                    parentGraph,
                    subgraphContext,
                    snapshot,
                    typeof(float),
                    BlackboardNames.StartSize,
                    GetExposedStartSizeValue(snapshot));
            }

            if (!snapshot.StartColorTwoColors)
            {
                ExposeAndLinkParentParameter(
                    parentGraph,
                    subgraphContext,
                    snapshot,
                    typeof(Vector3),
                    BlackboardNames.ParticleColor,
                    new Vector3(snapshot.StartColor.r, snapshot.StartColor.g, snapshot.StartColor.b));
            }
        }
    }

    internal static void AddHierarchyBlackboardParameters(
        VFXGraph graph,
        ParticleEffectHierarchySnapshot hierarchy)
    {
        if (graph == null || hierarchy == null)
            return;

        var playRate = Mathf.Max(hierarchy.PlaybackSpeed, 0.01f);
        var parameter = CreateExposedParameter(typeof(float), BlackboardNames.PlayRate, playRate);
        graph.AddChild(parameter);
        parameter.position = new Vector2(-260f, -120f);
    }

    static void AddSystemBlackboardParameters(
        VFXGraph graph,
        BuiltParticleSystem built,
        string systemName,
        bool prefixNames)
    {
        var snapshot = built.Snapshot;
        var yOffset = 0f;

        TryExposeSystemParameter(graph, built, snapshot, prefixNames, systemName, ref yOffset,
            typeof(float), BlackboardNames.EmissionRate, snapshot.EmissionRate,
            parameter => TryLinkParameterToSpawnerRate(parameter, built.Spawner),
            snapshot.EmissionRate > 0.001f && !snapshot.RateOverTime.UsesCurve);

        TryExposeSystemParameter(graph, built, snapshot, prefixNames, systemName, ref yOffset,
            typeof(Texture2D), BlackboardNames.MainTexture, snapshot.MainTexture,
            parameter => TryLinkParameterToOutputTexture(parameter, built.Output),
            snapshot.MainTexture != null);

        TryExposeSystemParameter(graph, built, snapshot, prefixNames, systemName, ref yOffset,
            typeof(float), BlackboardNames.StartLifetime, snapshot.StartLifetime.Constant,
            parameter => TryLinkParameterToLifetime(parameter, built.Initialize),
            snapshot.StartLifetime.Constant > 0.001f);

        TryExposeSystemParameter(graph, built, snapshot, prefixNames, systemName, ref yOffset,
            typeof(float), BlackboardNames.StartSize, GetExposedStartSizeValue(snapshot),
            parameter => TryLinkParameterToStartSize(parameter, built.Initialize),
            ShouldExposeStartSize(snapshot));

        if (!snapshot.StartColorTwoColors)
        {
            TryExposeSystemParameter(graph, built, snapshot, prefixNames, systemName, ref yOffset,
                typeof(Vector3), BlackboardNames.ParticleColor,
                new Vector3(snapshot.StartColor.r, snapshot.StartColor.g, snapshot.StartColor.b),
                parameter => TryLinkParameterToParticleColor(parameter, built.Initialize),
                HasVisibleColor(snapshot.StartColor));
        }
    }

    static void TryExposeSystemParameter(
        VFXGraph graph,
        BuiltParticleSystem built,
        ParticleEffectSnapshot snapshot,
        bool prefixNames,
        string systemName,
        ref float yOffset,
        Type type,
        string baseName,
        object value,
        Func<VFXParameter, bool> linker,
        bool shouldExpose)
    {
        if (!shouldExpose || value == null)
            return;

        var exposedName = prefixNames ? $"{systemName} {baseName}" : baseName;
        var parameter = CreateExposedParameter(type, exposedName, value);
        graph.AddChild(parameter);
        parameter.position = built.Spawner.position + new Vector2(-220f, 40f + yOffset);
        yOffset += 80f;

        if (!linker(parameter))
            snapshot.Warnings.Add($"Could not link exposed '{exposedName}' parameter.");
    }

    static void ExposeAndLinkParentParameter(
        VFXGraph parentGraph,
        VFXSubgraphContext subgraphContext,
        ParticleEffectSnapshot snapshot,
        Type type,
        string baseName,
        object value)
    {
        if (value == null)
            return;

        if (type == typeof(float) && value is float floatValue && floatValue <= 0.001f
            && baseName == BlackboardNames.EmissionRate)
            return;

        if (baseName == BlackboardNames.EmissionRate && snapshot.RateOverTime.UsesCurve)
            return;

        if (type == typeof(float) && value is float lifetime && lifetime <= 0.001f
            && baseName == BlackboardNames.StartLifetime)
            return;

        if (type == typeof(float) && value is float startSize && startSize <= 0.001f
            && baseName == BlackboardNames.StartSize)
            return;

        if (type == typeof(Vector3) && value is Vector3 color && GetVector3MaxComponent(color) <= 0.001f
            && baseName == BlackboardNames.ParticleColor)
            return;

        var exposedName = $"{snapshot.SourceName} {baseName}";
        var parentParameter = CreateExposedParameter(type, exposedName, value);
        parentGraph.AddChild(parentParameter);
        parentParameter.position = subgraphContext.position + new Vector2(-240f, 120f);

        var inputSlot = FindSubgraphContextInputSlot(subgraphContext, baseName);
        if (inputSlot == null)
        {
            snapshot.Warnings.Add($"Parent blackboard could not find subgraph input '{baseName}'.");
            return;
        }

        if (!TryLinkParameterOutput(parentParameter, inputSlot, parentParameter.position))
            snapshot.Warnings.Add($"Could not link parent parameter '{exposedName}' to subgraph input.");
    }

    static VFXSlot FindSubgraphContextInputSlot(VFXSubgraphContext context, string propertyName)
    {
        var slot = FindSlot(context, propertyName, isInput: true);
        if (slot != null)
            return slot;

        if (context is not IVFXSlotContainer slotContainer)
            return null;

        for (var i = 0; i < slotContainer.GetNbInputSlots(); i++)
        {
            var candidate = context.GetInputSlot(i);
            if (candidate?.property.name == propertyName)
                return candidate;
        }

        return null;
    }

    static bool TryLinkParameterToLifetime(VFXParameter parameter, VFXContext initialize)
    {
        var block = FindSetAttributeBlock(initialize, VFXAttribute.Lifetime.name);
        if (block == null)
            return false;

        var inputSlot = block.GetInputSlot(0);
        return TryLinkParameterOutput(parameter, inputSlot, parameter.position);
    }

    static bool TryLinkParameterToStartSize(VFXParameter parameter, VFXContext initialize)
    {
        var block = FindSetAttributeBlock(initialize, VFXAttribute.Size.name);
        if (block == null)
            return false;

        // Random min/max size uses two input slots; linking only slot 0 overrides the min bound.
        if (block.GetNbInputSlots() > 1)
            return false;

        var inputSlot = block.GetInputSlot(0);
        return TryLinkParameterOutput(parameter, inputSlot, parameter.position);
    }

    static bool TryLinkParameterToParticleColor(VFXParameter parameter, VFXContext initialize)
    {
        var block = FindSetAttributeBlock(initialize, VFXAttribute.Color.name);
        if (block == null)
            return false;

        var inputSlot = FindSlot(block, "_Color", isInput: true) ?? block.GetInputSlot(0);
        return TryLinkParameterOutput(parameter, inputSlot, parameter.position);
    }

    static VFXBlock FindSetAttributeBlock(VFXContext context, string attributeName)
    {
        foreach (var block in context.children.OfType<VFXBlock>())
        {
            if (block.GetType().Name != "SetAttribute")
                continue;

            var attributeField = block.GetType().GetField("attribute", BindingFlags.Instance | BindingFlags.Public);
            if (attributeField?.GetValue(block) as string == attributeName)
                return block;
        }

        return null;
    }

    static VFXParameter CreateExposedParameter(Type type, string exposedName, object value)
    {
        var parameter = ScriptableObject.CreateInstance<VFXParameter>();
        parameter.Init(type);
        parameter.value = value;
        SetPrivateField(parameter, "m_ExposedName", exposedName);
        SetPrivateField(parameter, "m_Exposed", true);
        SetPrivateField(parameter, "m_Category", "Converted");
        return parameter;
    }

    static bool TryLinkParameterToSpawnerRate(VFXParameter parameter, VFXContext spawner)
    {
        var rateBlock = spawner.children.OfType<VFXSpawnerConstantRate>().FirstOrDefault();
        if (rateBlock == null)
            return false;

        var inputSlot = FindSlot(rateBlock, "Rate", isInput: true)
                        ?? rateBlock.GetInputSlot(0);
        return TryLinkParameterOutput(parameter, inputSlot, parameter.position);
    }

    static bool TryLinkParameterToOutputTexture(VFXParameter parameter, VFXContext output)
    {
        var inputSlot = FindSlot(output, "mainTexture", isInput: true);
        if (inputSlot == null && output is IVFXSlotContainer slotContainer)
        {
            for (var i = 0; i < slotContainer.GetNbInputSlots(); i++)
            {
                var slot = output.GetInputSlot(i);
                if (slot?.property.name == "mainTexture")
                {
                    inputSlot = slot;
                    break;
                }
            }
        }

        return TryLinkParameterOutput(parameter, inputSlot, parameter.position);
    }

    static bool TryLinkParameterOutput(VFXParameter parameter, VFXSlot inputSlot, Vector2 nodePosition)
    {
        if (parameter == null || inputSlot == null || parameter.outputSlots.Count == 0)
            return false;

        var outputSlot = parameter.outputSlots[0];
        outputSlot.Link(inputSlot);
        parameter.AddNode(nodePosition);
        parameter.ValidateNodes();
        return true;
    }

    static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(target, value);
    }

    static bool HasVisibleColor(Color color) => GetVector3MaxComponent(new Vector3(color.r, color.g, color.b)) > 0.001f;

    static float GetVector3MaxComponent(Vector3 value) => Mathf.Max(value.x, Mathf.Max(value.y, value.z));

    static class BlackboardNames
    {
        public const string EmissionRate = "Emission Rate";
        public const string MainTexture = "Main Texture";
        public const string StartLifetime = "Start Lifetime";
        public const string StartSize = "Start Size";
        public const string ParticleColor = "Particle Color";
        public const string PlayRate = "Play Rate";
    }
}
