using System;
using System.Linq;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;
using VfxBlock = UnityEditor.VFX.Block;

public static partial class ParticleToVfxGraphBuilder
{
    internal static void AddAdvancedModuleBlocks(VFXContext update, ParticleEffectSnapshot snapshot)
    {
        if (snapshot.ForceOverLifetimeEnabled)
            AddForceOverLifetimeBlock(update, snapshot);

        if (snapshot.LimitVelocityEnabled)
            AddLimitVelocityBlock(update, snapshot);

        if (snapshot.SizeBySpeedEnabled)
            AddAttributeFromSpeedBlock(update, snapshot, VFXAttribute.Size, snapshot.SizeBySpeed, snapshot.SizeBySpeedRange);

        if (snapshot.RotationBySpeedEnabled)
            AddAttributeFromSpeedBlock(update, snapshot, VFXAttribute.AngleZ, snapshot.RotationBySpeed, snapshot.RotationBySpeedRange, Mathf.Deg2Rad);

        if (snapshot.ColorBySpeedEnabled)
            AddColorBySpeedBlock(update, snapshot);

        if (NeedsCollisionBlock(snapshot))
            AddCollisionBlocks(update, snapshot);

        if (snapshot.ExternalForcesEnabled)
            AddExternalForcesBlock(update, snapshot);

        if (snapshot.TrailsEnabled)
            AddTrailWidthOverLifeBlock(update, snapshot);
    }

    static void AddTrailWidthOverLifeBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var trails = snapshot.Trails;
        if (trails == null)
            return;

        var widthCurve = trails.WidthOverTrail;
        if (!widthCurve.HasCurve && Mathf.Approximately(trails.WidthMultiplier, 1f))
            return;

        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", VFXAttribute.Size.name);
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Multiply);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.OverLife);
        block.ResyncSlots(true);

        var curve = widthCurve.HasCurve
            ? ScaleCurve(widthCurve.ToCurve(), trails.WidthMultiplier)
            : AnimationCurve.Linear(0f, trails.WidthMultiplier, 1f, trails.WidthMultiplier);

        if (!SetBlockSlotValue(block, VFXAttribute.Size.name, curve))
            SetBlockSlotValue(block, 0, curve);

        context.AddChild(block);
    }

    static void AddExternalForcesBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (!snapshot.HasSceneWindZone)
            return;

        var block = TryCreateBlock("Force");
        if (block == null)
        {
            snapshot.Warnings.Add("Force block unavailable for external forces.");
            return;
        }

        var force = snapshot.WindZoneForce * snapshot.ExternalForcesMultiplier.Constant;
        if (force.sqrMagnitude <= 0.0001f)
            return;

        TrySetBlockEnumSetting(block, "Mode", "UnityEditor.VFX.Block.ForceMode", "Absolute");
        block.ResyncSlots(true);
        SetBlockSlotValue(block, "Force", (Vector)force);
        context.AddChild(block);

        snapshot.Warnings.Add(
            $"External Forces approximated from Wind Zone '{snapshot.SceneWindZoneName}'; spherical zones and turbulence are not converted.");
    }

    static bool NeedsCollisionBlock(ParticleEffectSnapshot snapshot)
    {
        if (snapshot.CollisionEnabled)
            return true;

        return snapshot.SubEmitters.Any(subEmitter =>
            subEmitter.Type == ParticleSystemSubEmitterType.Collision);
    }

    static void AddForceOverLifetimeBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var force = new Vector3(snapshot.ForceX.Constant, snapshot.ForceY.Constant, snapshot.ForceZ.Constant);
        if (force.sqrMagnitude <= 0.0001f)
            return;

        var block = TryCreateBlock("Force");
        if (block == null)
        {
            snapshot.Warnings.Add("Force block unavailable.");
            return;
        }

        TrySetBlockEnumSetting(block, "Mode", "UnityEditor.VFX.Block.ForceMode", "Absolute");
        block.ResyncSlots(true);
        SetBlockSlotValue(block, "Force", (Vector)force);
        context.AddChild(block);
    }

    static void AddLimitVelocityBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = TryCreateBlock("Drag");
        if (block == null)
        {
            snapshot.Warnings.Add("Linear Drag block unavailable.");
            return;
        }

        var drag = snapshot.LimitVelocityDrag > 0.001f
            ? snapshot.LimitVelocityDrag
            : Mathf.Lerp(0.1f, 2f, snapshot.LimitVelocityDampen);

        SetBlockSlotValue(block, "dragCoefficient", Mathf.Max(drag, 0.01f));
        context.AddChild(block);

        if (snapshot.LimitVelocityMagnitude > 0.001f)
            snapshot.Warnings.Add("Limit velocity magnitude cap is approximated with drag only.");
    }

    static void AddAttributeFromSpeedBlock(
        VFXContext context,
        ParticleEffectSnapshot snapshot,
        VFXAttribute attribute,
        AnimationCurveSnapshot curve,
        Vector2 speedRange,
        float scale = 1f)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", attribute.name);
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Multiply);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.BySpeed);
        block.ResyncSlots(true);

        var scaledCurve = ScaleCurve(curve.ToCurve(), scale);
        if (!SetBlockSlotValue(block, attribute.name, scaledCurve))
            SetBlockSlotValue(block, 0, scaledCurve);

        if (speedRange.y > speedRange.x)
            snapshot.Warnings.Add($"{attribute.name} by speed uses VFX default speed normalization; Shuriken range {speedRange.x:0.##}-{speedRange.y:0.##} may differ.");

        context.AddChild(block);
    }

    static AnimationCurve ScaleCurve(AnimationCurve curve, float scale)
    {
        if (Mathf.Approximately(scale, 1f))
            return curve;

        var keys = curve.keys;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            key.value *= scale;
            keys[i] = key;
        }

        return new AnimationCurve(keys);
    }

    static void AddColorBySpeedBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = ScriptableObject.CreateInstance<VfxBlock.AttributeFromCurve>();
        block.SetSettingValue("attribute", VFXAttribute.Color.name);
        block.SetSettingValue("Composition", VfxBlock.AttributeCompositionMode.Blend);
        block.SetSettingValue("SampleMode", VfxBlock.AttributeFromCurve.CurveSampleMode.BySpeed);
        block.ResyncSlots(true);

        if (snapshot.ColorBySpeed != null)
            SetBlockSlotValue(block, "Color", snapshot.ColorBySpeed);

        context.AddChild(block);
    }

    static bool NeedsCollisionEventAttributes(ParticleEffectSnapshot snapshot)
    {
        return snapshot.SubEmitters.Any(subEmitter =>
            subEmitter.Type == ParticleSystemSubEmitterType.Collision);
    }

    static void AddCollisionBlocks(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        if (snapshot.CollisionType == ParticleSystemCollisionType.World)
        {
            AddCollisionSphereBlock(context, snapshot);
            return;
        }

        if (snapshot.CollisionPlanes.Count > 0)
        {
            foreach (var plane in snapshot.CollisionPlanes)
                AddCollisionPlaneBlock(context, snapshot, plane.Position, plane.Normal);
            return;
        }

        AddCollisionPlaneBlock(context, snapshot, Vector3.zero, Vector3.up);
        if (snapshot.CollisionEnabled)
            snapshot.Warnings.Add("No collision planes assigned; using a default ground plane collider.");
    }

    static void AddCollisionPlaneBlock(
        VFXContext context,
        ParticleEffectSnapshot snapshot,
        Vector3 position,
        Vector3 normal)
    {
        var block = CreateCollisionShapeBlock(snapshot, "Plane");
        if (block == null)
            return;

        var plane = CreateStructValue(
            "UnityEditor.VFX.Plane",
            ("position", (object)position),
            ("normal", normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up));
        SetBlockSlotValue(block, "Plane", plane);
        context.AddChild(block);
    }

    static void AddCollisionSphereBlock(VFXContext context, ParticleEffectSnapshot snapshot)
    {
        var block = CreateCollisionShapeBlock(snapshot, "Sphere");
        if (block == null)
            return;

        var radius = Mathf.Max(GetCollisionSphereRadius(snapshot), 0.5f);
        var sphere = CreateStructValue(
            "UnityEditor.VFX.TSphere",
            ("transform", CreateStructValue(
                "UnityEditor.VFX.Transform",
                ("position", (object)Vector3.zero),
                ("angles", Vector3.zero),
                ("scale", Vector3.one))),
            ("radius", radius));
        SetBlockSlotValue(block, "sphere", sphere);
        context.AddChild(block);
    }

    static float GetCollisionSphereRadius(ParticleEffectSnapshot snapshot)
    {
        var shapeScale = snapshot.ShapeScale == Vector3.zero ? Vector3.one : snapshot.ShapeScale;
        var shapeRadius = snapshot.ShapeRadius > 0.001f
            ? snapshot.ShapeRadius * Mathf.Max(shapeScale.x, shapeScale.y, shapeScale.z)
            : 2f;
        return shapeRadius * Mathf.Max(snapshot.CollisionRadiusScale, 0.1f);
    }

    static VFXBlock CreateCollisionShapeBlock(ParticleEffectSnapshot snapshot, string shapeLiteral)
    {
        var block = TryCreateBlock("CollisionShape");
        if (block == null)
        {
            snapshot.Warnings.Add("Collision Shape block unavailable.");
            return null;
        }

        TrySetBlockEnumSetting(block, "shape", "UnityEditor.VFX.Block.CollisionShapeBase+Type", shapeLiteral);
        TrySetBlockEnumSetting(block, "behavior", "UnityEditor.VFX.Block.CollisionBase+Behavior", "Collision");
        TrySetBlockEnumSetting(block, "radiusMode", "UnityEditor.VFX.Block.CollisionBase+RadiusMode", "FromSize");

        var collisionAttributes = NeedsCollisionEventAttributes(snapshot)
            ? "WriteAlways"
            : "NoWrite";
        TrySetBlockEnumSetting(
            block,
            "collisionAttributes",
            "UnityEditor.VFX.Block.CollisionBase+CollisionAttributesMode",
            collisionAttributes);

        block.ResyncSlots(true);
        SetBlockSlotValue(block, "Bounce", Mathf.Clamp01(snapshot.CollisionBounce));
        SetBlockSlotValue(block, "Friction", Mathf.Max(snapshot.CollisionFriction, 0f));
        SetBlockSlotValue(block, "LifetimeLoss", Mathf.Clamp01(snapshot.CollisionLifetimeLoss));
        return block;
    }

    internal static void AddRateOverDistanceSpawner(VFXBasicSpawner spawner, ParticleEffectSnapshot snapshot)
    {
        var wrapper = ScriptableObject.CreateInstance<VFXSpawnerCustomWrapper>();
        wrapper.SetSettingValue("m_customType", new SerializableType(typeof(SpawnOverDistance)));
        wrapper.ResyncSlots(true);

        SetBlockSlotValue(wrapper, "RatePerUnit", Mathf.Max(snapshot.RateOverDistance, 0.01f));
        SetBlockSlotValue(wrapper, "ClampToOne", false);
        spawner.AddChild(wrapper);
    }

    internal static void AddRateOverTimeCurveSpawner(VFXBasicSpawner spawner, ParticleEffectSnapshot snapshot)
    {
        var spawnerType = ResolveSpawnAtCurveRateType();
        if (spawnerType == null)
        {
            snapshot.Warnings.Add("SpawnAtCurveRate custom spawner unavailable; using average emission rate.");
            var fallbackRate = ScriptableObject.CreateInstance<VFXSpawnerConstantRate>();
            SetBlockSlotValue(fallbackRate, 0, Mathf.Max(snapshot.EmissionRate, 0.01f));
            spawner.AddChild(fallbackRate);
            return;
        }

        var wrapper = ScriptableObject.CreateInstance<VFXSpawnerCustomWrapper>();
        wrapper.SetSettingValue("m_customType", new SerializableType(spawnerType));
        wrapper.ResyncSlots(true);

        var duration = Mathf.Max(snapshot.Duration, 0.01f);
        SetBlockSlotValue(wrapper, "LoopDuration", duration);

        if (snapshot.RateOverTime.Mode == MinMaxMode.TwoCurves)
        {
            SetBlockSlotValue(wrapper, "RateCurveMin", snapshot.RateOverTime.CurveMin);
            SetBlockSlotValue(wrapper, "RateCurveMax", snapshot.RateOverTime.CurveMax);
            SetBlockSlotValue(wrapper, "UseRandomBetweenCurves", true);
        }
        else
        {
            var curve = snapshot.RateOverTime.Curve
                        ?? AnimationCurve.Linear(0f, snapshot.EmissionRate, 1f, snapshot.EmissionRate);
            SetBlockSlotValue(wrapper, "RateCurve", curve);
            SetBlockSlotValue(wrapper, "UseRandomBetweenCurves", false);
        }

        spawner.AddChild(wrapper);
        snapshot.Warnings.Add("Emission rate-over-time curve converted with custom Spawn At Curve Rate spawner.");
    }

    static Type ResolveSpawnAtCurveRateType()
    {
        return Type.GetType("UnityEngine.VFX.SpawnAtCurveRate, VFX.Converter.Runtime");
    }

    internal static bool TryCreateMeshOutput(ParticleEffectSnapshot snapshot, out VFXContext output)
    {
        output = null;
        if (snapshot.TrailsEnabled
            || snapshot.RenderMode != ParticleSystemRenderMode.Mesh
            || snapshot.RenderMesh == null)
            return false;

        output = ScriptableObject.CreateInstance<VFXMeshOutput>();
        output.label = "Output Mesh";
        return true;
    }

    internal static void ConfigureMeshOutput(VFXContext output, ParticleEffectSnapshot snapshot)
    {
        var mesh = ParticleToVfxAssetReferenceGuard.SanitizeForVfxAsset(
            snapshot.RenderMesh, "mesh output", snapshot);
        if (mesh == null)
        {
            snapshot.Warnings.Add("Mesh output has no persistent mesh asset; using billboard output instead.");
            return;
        }

        SetContextSlotValue(output, "mesh", mesh);
    }
}
