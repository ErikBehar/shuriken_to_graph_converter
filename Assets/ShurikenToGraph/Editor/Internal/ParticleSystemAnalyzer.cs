using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ParticleSystemAnalyzer
{
    const int MaxCollisionPlanes = 6;

    /// <param name="allowSceneQueries">
    /// When true (live scene object), modules that depend on the surrounding scene
    /// (world Collision colliders, External Forces wind zones) are resolved by scanning
    /// the scene. Set false when analyzing an isolated prefab-asset copy, where such a
    /// scan would hit an unrelated scene and produce misleading results.
    /// </param>
    public static ParticleEffectHierarchySnapshot AnalyzeHierarchy(GameObject root, bool allowSceneQueries = true)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));

        var hierarchy = new ParticleEffectHierarchySnapshot
        {
            RootName = root.name,
            RootLocalPosition = root.transform.localPosition,
            RootLocalRotation = root.transform.localEulerAngles,
            RootLocalScale = root.transform.localScale
        };

        var particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
        var pathLookup = new Dictionary<ParticleSystem, string>();

        for (var i = 0; i < particleSystems.Length; i++)
        {
            var particleSystem = particleSystems[i];
            var path = BuildHierarchyPath(root.transform, particleSystem.transform);
            pathLookup[particleSystem] = path;

            var snapshot = Analyze(
                particleSystem,
                particleSystem.transform,
                root.transform,
                path,
                i,
                allowSceneQueries);
            hierarchy.Systems.Add(snapshot);
        }

        ResolveSubEmitterTargets(hierarchy.Systems, pathLookup);
        ParticleToVfxSubEmitterCycleGuard.ApplySubEmitterCycleGuards(hierarchy.Systems);
        FinalizeHierarchyMetadata(hierarchy);

        foreach (var system in hierarchy.Systems)
        {
            hierarchy.Warnings.AddRange(system.Warnings);
            foreach (var unsupported in system.UnsupportedFeatures)
                hierarchy.Warnings.Add($"[{system.SourceName}] {unsupported}");
        }

        return hierarchy;
    }

    public static ParticleEffectSnapshot Analyze(
        ParticleSystem particleSystem,
        Transform sourceTransform,
        Transform rootTransform = null,
        string hierarchyPath = null,
        int systemIndex = 0,
        bool allowSceneQueries = true)
    {
        if (particleSystem == null)
            throw new ArgumentNullException(nameof(particleSystem));

        rootTransform ??= sourceTransform;
        hierarchyPath ??= BuildHierarchyPath(rootTransform, sourceTransform);

        var snapshot = new ParticleEffectSnapshot
        {
            SourceName = particleSystem.gameObject.name,
            HierarchyPath = hierarchyPath,
            SystemIndex = systemIndex,
            LocalPosition = sourceTransform.localPosition,
            LocalRotation = sourceTransform.localEulerAngles,
            LocalScale = sourceTransform.localScale,
            SpawnOffset = rootTransform.InverseTransformPoint(sourceTransform.position),
            SpawnRotation = (Quaternion.Inverse(rootTransform.rotation) * sourceTransform.rotation).eulerAngles
        };

        var main = particleSystem.main;
        snapshot.Looping = main.loop;
        snapshot.Duration = main.duration;
        snapshot.SimulationSpeed = main.simulationSpeed;
        snapshot.Prewarm = main.prewarm;
        snapshot.MaxParticles = main.maxParticles;
        snapshot.SimulationSpace = main.simulationSpace;
        snapshot.ScalingMode = main.scalingMode;
        snapshot.GravityModifier = main.gravityModifier.constant;
        snapshot.StartLifetime = MinMaxSnapshot.FromCurve(main.startLifetime, snapshot.Warnings, "Start Lifetime");
        snapshot.StartSpeed = MinMaxSnapshot.FromCurve(main.startSpeed, snapshot.Warnings, "Start Speed");
        snapshot.StartSize = MinMaxSnapshot.FromCurve(main.startSize, snapshot.Warnings, "Start Size");
        snapshot.StartSize3D = main.startSize3D;
        if (main.startSize3D)
        {
            snapshot.StartSizeX = MinMaxSnapshot.FromCurve(main.startSizeX, snapshot.Warnings, "Start Size X");
            snapshot.StartSizeY = MinMaxSnapshot.FromCurve(main.startSizeY, snapshot.Warnings, "Start Size Y");
            snapshot.StartSizeZ = MinMaxSnapshot.FromCurve(main.startSizeZ, snapshot.Warnings, "Start Size Z");
            snapshot.Warnings.Add("3D start size is approximated with a single uniform size using per-axis maxima.");
        }

        snapshot.StartColor = main.startColor.color;
        snapshot.StartColorTwoColors = main.startColor.mode == ParticleSystemGradientMode.TwoColors;
        if (snapshot.StartColorTwoColors)
        {
            snapshot.StartColorMin = main.startColor.colorMin;
            snapshot.StartColorMax = main.startColor.colorMax;
        }
        snapshot.StartRotation = MinMaxSnapshot.FromCurve(main.startRotation, snapshot.Warnings, "Start Rotation");

        var emission = particleSystem.emission;
        if (emission.enabled)
        {
            snapshot.RateOverTime = MinMaxSnapshot.FromEmissionRateCurve(emission.rateOverTime, snapshot.Warnings);
            snapshot.EmissionRate = snapshot.RateOverTime.Constant;
            snapshot.RateOverDistance = EvaluateEmissionRate(emission.rateOverDistance, snapshot.Warnings);
            CaptureBursts(emission, snapshot);
        }

        var shape = particleSystem.shape;
        snapshot.ShapeEnabled = shape.enabled;
        snapshot.ShapeType = shape.shapeType;
        snapshot.ShapePosition = shape.position;
        snapshot.ShapeRotation = shape.rotation;
        snapshot.ShapeScale = shape.scale;
        snapshot.ShapeRadius = shape.radius;
        snapshot.ShapeAngle = shape.angle;
        snapshot.ShapeLength = shape.length;
        snapshot.ShapeDonutRadius = shape.donutRadius;
        snapshot.ShapeArc = shape.arc;
        snapshot.ShapeRadiusThickness = shape.radiusThickness;
        snapshot.ShapeRandomDirectionAmount = shape.randomDirectionAmount;
        snapshot.ShapeSphericalDirectionAmount = shape.sphericalDirectionAmount;
        snapshot.ShapeMesh = shape.mesh;
        snapshot.ShapeMeshRenderer = shape.meshRenderer;
        snapshot.ShapeRendererMesh = ResolveMeshFromRenderer(shape.meshRenderer);
        snapshot.ShapeSkinnedMeshRenderer = shape.skinnedMeshRenderer;
        snapshot.ShapeSprite = shape.sprite;
        snapshot.ShapeSpriteRenderer = shape.spriteRenderer;

        var inheritVelocity = particleSystem.inheritVelocity;
        snapshot.InheritVelocityEnabled = inheritVelocity.enabled;
        if (inheritVelocity.enabled)
        {
            snapshot.InheritVelocityMode = inheritVelocity.mode;
            snapshot.InheritVelocityMultiplier = MinMaxSnapshot.FromCurve(
                inheritVelocity.curve,
                snapshot.Warnings,
                "Inherit Velocity");
        }

        var velocity = particleSystem.velocityOverLifetime;
        snapshot.VelocityOverLifetimeEnabled = velocity.enabled;
        if (velocity.enabled)
        {
            snapshot.VelocityX = MinMaxSnapshot.FromCurve(velocity.x, snapshot.Warnings, "Velocity Over Lifetime X");
            snapshot.VelocityY = MinMaxSnapshot.FromCurve(velocity.y, snapshot.Warnings, "Velocity Over Lifetime Y");
            snapshot.VelocityZ = MinMaxSnapshot.FromCurve(velocity.z, snapshot.Warnings, "Velocity Over Lifetime Z");
            snapshot.OrbitalVelocity = new Vector3(
                velocity.orbitalX.constant,
                velocity.orbitalY.constant,
                velocity.orbitalZ.constant);
            snapshot.RadialVelocity = velocity.radial.constant;
            snapshot.VelocityOverLifetimeWorldSpace = velocity.space == ParticleSystemSimulationSpace.World;
        }

        var sizeOverLifetime = particleSystem.sizeOverLifetime;
        snapshot.SizeOverLifetimeEnabled = sizeOverLifetime.enabled;
        if (sizeOverLifetime.enabled)
        {
            snapshot.SizeOverLifetime = new AnimationCurveSnapshot(sizeOverLifetime.size.curveMax);
            snapshot.SizeOverLifetimeMultiplier = MinMaxSnapshot.FromCurve(sizeOverLifetime.size, snapshot.Warnings, "Size Over Lifetime");
        }

        var colorOverLifetime = particleSystem.colorOverLifetime;
        snapshot.ColorOverLifetimeEnabled = colorOverLifetime.enabled;
        if (colorOverLifetime.enabled)
        {
            snapshot.ColorOverLifetime = ParticleToVfxGradientUtility.SimplifyForVfx(
                colorOverLifetime.color.gradient,
                snapshot,
                "Color Over Lifetime");
        }

        var rotationOverLifetime = particleSystem.rotationOverLifetime;
        snapshot.RotationOverLifetimeEnabled = rotationOverLifetime.enabled;
        if (rotationOverLifetime.enabled)
        {
            snapshot.AngularVelocityX = MinMaxSnapshot.FromCurve(rotationOverLifetime.x, snapshot.Warnings, "Rotation Over Lifetime X");
            snapshot.AngularVelocityY = MinMaxSnapshot.FromCurve(rotationOverLifetime.y, snapshot.Warnings, "Rotation Over Lifetime Y");
            snapshot.AngularVelocityZ = MinMaxSnapshot.FromCurve(rotationOverLifetime.z, snapshot.Warnings, "Rotation Over Lifetime Z");
        }

        var noise = particleSystem.noise;
        snapshot.NoiseEnabled = noise.enabled;
        if (noise.enabled)
        {
            snapshot.NoiseStrength = noise.strength.constant;
            snapshot.NoiseStrengthMin = noise.strength.mode == ParticleSystemCurveMode.TwoConstants
                ? noise.strength.constantMin
                : noise.strength.constant;
            snapshot.NoiseFrequency = noise.frequency;
            snapshot.NoiseOctaves = noise.octaveCount;
            snapshot.NoiseRoughness = noise.octaveMultiplier;
            snapshot.NoiseLacunarity = noise.octaveScale;
            snapshot.NoiseScrollSpeed = noise.scrollSpeed.constant;
        }

        CaptureRenderer(particleSystem, snapshot);
        CaptureTextureSheet(particleSystem, snapshot);
        CaptureTrails(particleSystem, snapshot);
        CaptureForceOverLifetime(particleSystem, snapshot);
        CaptureBySpeedModules(particleSystem, snapshot);
        CaptureLimitVelocity(particleSystem, snapshot);
        CaptureCollision(particleSystem, snapshot, allowSceneQueries);
        CaptureExternalForces(particleSystem, snapshot, sourceTransform, allowSceneQueries);
        CaptureTrigger(particleSystem, snapshot);
        CaptureLights(particleSystem, snapshot);
        CaptureCustomData(particleSystem, snapshot);
        CaptureSubEmitters(particleSystem, snapshot, rootTransform);

        // Shuriken multiplies the material color across the whole particle color (start color
        // AND the color-over-life / color-by-speed ramps). Start color is tinted in
        // CaptureRenderer; tint the gradients here, once both they and MaterialColor are known,
        // so a varying ramp stays consistent instead of dropping the material tint.
        snapshot.ColorOverLifetime = MultiplyGradientRgb(snapshot.ColorOverLifetime, snapshot.MaterialColor);
        snapshot.ColorBySpeed = MultiplyGradientRgb(snapshot.ColorBySpeed, snapshot.MaterialColor);

        RecordUnsupportedModules(particleSystem, snapshot);
        return snapshot;
    }

    static void CaptureRenderer(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
        if (renderer == null)
            return;

        snapshot.RenderMode = renderer.renderMode;
        snapshot.RenderAlignment = renderer.alignment;
        snapshot.MaxParticleSize = renderer.maxParticleSize;
        snapshot.LengthScale = renderer.lengthScale;
        snapshot.VelocityScale = renderer.velocityScale;
        snapshot.RenderMesh = renderer.renderMode == ParticleSystemRenderMode.Mesh
            ? ResolvePersistentRenderMesh(renderer)
            : null;

        snapshot.CastShadows = renderer.shadowCastingMode != ShadowCastingMode.Off;
        snapshot.ShadowCastingMode = renderer.shadowCastingMode;
        snapshot.ReceiveShadows = renderer.receiveShadows;
        snapshot.SortingOrder = renderer.sortingOrder;
        snapshot.SortingFudge = renderer.sortingFudge;
        snapshot.SortingLayerName = renderer.sortingLayerName;

        var material = renderer.sharedMaterial;
        if (particleSystem.trails.enabled && renderer.trailMaterial != null)
            material = renderer.trailMaterial;

        if (material == null)
            return;

        snapshot.MaterialName = material.name;
        if (material.shader != null)
            snapshot.ShaderName = material.shader.name;

        if (!string.IsNullOrEmpty(snapshot.ShaderName) && !IsBuiltInParticleShader(snapshot.ShaderName))
        {
            snapshot.UsesCustomShader = true;
            snapshot.Warnings.Add(
                $"Custom shader '{snapshot.ShaderName}' detected; assign a matching VFX Shader Graph on the output context manually.");
        }

        if (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null)
            snapshot.MainTexture = material.GetTexture("_BaseMap") as Texture2D;
        else if (material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null)
            snapshot.MainTexture = material.GetTexture("_MainTex") as Texture2D;

        if (material.HasProperty("_BaseColor"))
            snapshot.MaterialColor = material.GetColor("_BaseColor");
        else if (material.HasProperty("_Color"))
            snapshot.MaterialColor = material.GetColor("_Color");

        snapshot.UseAdditiveBlend = material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")
            || material.IsKeywordEnabled("_CFXR_ADDITIVE")
            || (material.HasProperty("_DstBlend") && material.GetFloat("_DstBlend") == (float)UnityEngine.Rendering.BlendMode.One);

        if (material.HasProperty("_SoftParticlesEnabled"))
            snapshot.SoftParticlesEnabled = material.GetFloat("_SoftParticlesEnabled") > 0.5f;
        if (material.HasProperty("_SoftParticlesNearFadeDistance"))
            snapshot.SoftParticleNear = material.GetFloat("_SoftParticlesNearFadeDistance");
        if (material.HasProperty("_SoftParticlesFarFadeDistance"))
            snapshot.SoftParticleFar = material.GetFloat("_SoftParticlesFarFadeDistance");

        if (material.IsKeywordEnabled("_ALPHATEST_ON")
            || material.IsKeywordEnabled("_AlphaClip")
            || (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f))
        {
            snapshot.UseAlphaClipping = true;
            if (material.HasProperty("_Cutoff"))
                snapshot.AlphaCutoff = material.GetFloat("_Cutoff");
            else if (material.HasProperty("_AlphaCutoff"))
                snapshot.AlphaCutoff = material.GetFloat("_AlphaCutoff");
        }

        if (material.HasProperty("_Cull") && Mathf.Approximately(material.GetFloat("_Cull"), 0f))
            snapshot.UseDoubleSided = true;
        else if (material.doubleSidedGI)
            snapshot.UseDoubleSided = true;

        snapshot.StartColor *= snapshot.MaterialColor;
        if (snapshot.StartColorTwoColors)
        {
            snapshot.StartColorMin *= snapshot.MaterialColor;
            snapshot.StartColorMax *= snapshot.MaterialColor;
        }

        if (snapshot.UsesCustomShader)
            ParticleToVfxShaderGraphMatcher.ResolveForSnapshot(snapshot);
    }

    static Gradient MultiplyGradientRgb(Gradient gradient, Color tint)
    {
        if (gradient == null)
            return null;

        var colorKeys = gradient.colorKeys;
        for (var i = 0; i < colorKeys.Length; i++)
        {
            var c = colorKeys[i].color;
            colorKeys[i].color = new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b, c.a);
        }

        var tinted = new Gradient { mode = gradient.mode };
        tinted.SetKeys(colorKeys, gradient.alphaKeys);
        return tinted;
    }

    static void CaptureTrigger(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var trigger = particleSystem.trigger;
        snapshot.TriggerEnabled = trigger.enabled;
        if (!trigger.enabled)
            return;

        snapshot.TriggerColliderCount = trigger.colliderCount;
        for (var i = 0; i < trigger.colliderCount; i++)
        {
            var collider = trigger.GetCollider(i);
            if (collider != null)
                snapshot.TriggerColliderNames.Add(collider.name);
        }

        snapshot.Warnings.Add(
            "Trigger module is not converted; approximate enter/exit behavior with VFX Collision Shape blocks using Behavior 'None'.");
    }

    static void CaptureLights(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var lights = particleSystem.lights;
        snapshot.LightsEnabled = lights.enabled;
        if (!lights.enabled)
            return;

        snapshot.LightRatio = lights.ratio;
        snapshot.UnsupportedFeatures.Add("Lights module (not converted)");
    }

    static void CaptureCustomData(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var customData = particleSystem.customData;
        snapshot.CustomDataEnabled = customData.enabled;
        if (!customData.enabled)
            return;

        snapshot.CustomDataChannelCount = 2;
        snapshot.Warnings.Add(
            "Custom Data channels are not converted; recreate logic with VFX attributes, operators, or custom HLSL blocks.");
    }

    static bool IsBuiltInParticleShader(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName))
            return true;

        return shaderName.Contains("Particles", StringComparison.OrdinalIgnoreCase)
               || shaderName.Contains("Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase)
               || shaderName.StartsWith("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase)
               || shaderName.StartsWith("Legacy Shaders/", StringComparison.OrdinalIgnoreCase);
    }

    static void FinalizeHierarchyMetadata(ParticleEffectHierarchySnapshot hierarchy)
    {
        if (hierarchy.Systems.Count == 0)
            return;

        hierarchy.PlaybackSpeed = hierarchy.Systems[0].SimulationSpeed;
        hierarchy.SortingOrder = hierarchy.Systems[0].SortingOrder;
        hierarchy.SortingLayerName = hierarchy.Systems[0].SortingLayerName;
        hierarchy.ShadowCastingMode = hierarchy.Systems[0].ShadowCastingMode;
        hierarchy.ReceiveShadows = hierarchy.Systems[0].ReceiveShadows;

        var playbackMismatch = false;
        for (var i = 1; i < hierarchy.Systems.Count; i++)
        {
            var system = hierarchy.Systems[i];
            if (!Mathf.Approximately(system.SimulationSpeed, hierarchy.PlaybackSpeed))
                playbackMismatch = true;

            if (system.SortingOrder > hierarchy.SortingOrder)
                hierarchy.SortingOrder = system.SortingOrder;
        }

        if (playbackMismatch)
            hierarchy.Warnings.Add("Systems use different simulation speeds; prefab playRate uses the first system's value.");
    }

    static void CaptureTextureSheet(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var sheet = particleSystem.textureSheetAnimation;
        snapshot.TextureSheetEnabled = sheet.enabled;
        if (!sheet.enabled)
            return;

        snapshot.TextureSheet = new TextureSheetSnapshot
        {
            TilesX = Mathf.Max(1, sheet.numTilesX),
            TilesY = Mathf.Max(1, sheet.numTilesY),
            AnimationType = sheet.animation,
            RowMode = sheet.rowMode,
            TimeMode = sheet.timeMode,
            FlipbookRow = Mathf.Max(0, sheet.rowIndex),
            Cycles = Mathf.Max(1, sheet.cycleCount),
            Fps = Mathf.Max(1f, sheet.fps),
            StartFrame = GetCurveConstantInt(sheet.startFrame),
            EndFrame = Mathf.Max(0, sheet.numTilesX * sheet.numTilesY - 1)
        };

        var frameOverTime = sheet.frameOverTime;
        snapshot.TextureSheet.FrameOverTimeUsesCurve = frameOverTime.mode
            is ParticleSystemCurveMode.Curve
            or ParticleSystemCurveMode.TwoCurves;
        snapshot.TextureSheet.FrameOverTimeSpeed = frameOverTime.mode switch
        {
            ParticleSystemCurveMode.Constant => frameOverTime.constant,
            ParticleSystemCurveMode.TwoConstants => (frameOverTime.constantMin + frameOverTime.constantMax) * 0.5f,
            _ => 1f
        };

        if (snapshot.TextureSheet.FrameOverTimeUsesCurve
            && snapshot.TextureSheet.TimeMode != ParticleSystemAnimationTimeMode.Lifetime)
        {
            snapshot.Warnings.Add("Texture sheet frame-over-time curve approximated with FPS/cycles timing.");
        }
    }

    static void CaptureTrails(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var trails = particleSystem.trails;
        snapshot.TrailsEnabled = trails.enabled;
        if (!trails.enabled)
            return;

        snapshot.Trails = new TrailsSnapshot
        {
            Lifetime = trails.lifetime.constant,
            WidthMultiplier = trails.widthOverTrailMultiplier,
            WidthOverTrail = new AnimationCurveSnapshot(trails.widthOverTrail.curveMax),
            MinimumVertexDistance = trails.minVertexDistance,
            WorldSpace = trails.worldSpace,
            DieWithParticles = trails.dieWithParticles
        };

        if (trails.lifetime.mode != ParticleSystemCurveMode.Constant
            || (trails.widthOverTrail.mode != ParticleSystemCurveMode.Constant
                && trails.widthOverTrail.mode != ParticleSystemCurveMode.Curve)
            || trails.colorOverLifetime.mode != ParticleSystemGradientMode.Color
            || trails.colorOverTrail.mode != ParticleSystemGradientMode.Color)
        {
            snapshot.Warnings.Add("Trail curves/colors are approximated with constant values.");
        }
    }

    static void CaptureForceOverLifetime(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var force = particleSystem.forceOverLifetime;
        snapshot.ForceOverLifetimeEnabled = force.enabled;
        if (!force.enabled)
            return;

        snapshot.ForceX = MinMaxSnapshot.FromCurve(force.x, snapshot.Warnings, "Force Over Lifetime X");
        snapshot.ForceY = MinMaxSnapshot.FromCurve(force.y, snapshot.Warnings, "Force Over Lifetime Y");
        snapshot.ForceZ = MinMaxSnapshot.FromCurve(force.z, snapshot.Warnings, "Force Over Lifetime Z");

        if (force.randomized)
            snapshot.Warnings.Add("Randomized force over lifetime is approximated as averaged constants.");
    }

    static void CaptureBySpeedModules(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var sizeBySpeed = particleSystem.sizeBySpeed;
        snapshot.SizeBySpeedEnabled = sizeBySpeed.enabled;
        if (sizeBySpeed.enabled)
        {
            snapshot.SizeBySpeed = new AnimationCurveSnapshot(
                sizeBySpeed.separateAxes ? sizeBySpeed.x.curveMax : sizeBySpeed.size.curveMax);
            snapshot.SizeBySpeedRange = sizeBySpeed.range;
        }

        var rotationBySpeed = particleSystem.rotationBySpeed;
        snapshot.RotationBySpeedEnabled = rotationBySpeed.enabled;
        if (rotationBySpeed.enabled)
        {
            // Billboard rotation maps to the Z axis whether or not separate axes are used.
            snapshot.RotationBySpeed = new AnimationCurveSnapshot(rotationBySpeed.z.curveMax);
            snapshot.RotationBySpeedRange = rotationBySpeed.range;
        }

        var colorBySpeed = particleSystem.colorBySpeed;
        snapshot.ColorBySpeedEnabled = colorBySpeed.enabled;
        if (colorBySpeed.enabled)
        {
            snapshot.ColorBySpeed = ParticleToVfxGradientUtility.SimplifyForVfx(
                colorBySpeed.color.gradient,
                snapshot,
                "Color By Speed");
            snapshot.ColorBySpeedRange = colorBySpeed.range;
        }
    }

    static void CaptureLimitVelocity(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var limit = particleSystem.limitVelocityOverLifetime;
        snapshot.LimitVelocityEnabled = limit.enabled;
        if (!limit.enabled)
            return;

        snapshot.LimitVelocityMagnitude = GetCurveConstantFloat(limit.limit);
        snapshot.LimitVelocityDampen = limit.dampen;
        snapshot.LimitVelocityDrag = GetCurveConstantFloat(limit.drag);

        if (limit.limit.mode != ParticleSystemCurveMode.Constant
            || limit.drag.mode != ParticleSystemCurveMode.Constant)
            snapshot.Warnings.Add("Limit velocity curves approximated as constant values.");
    }

    static void CaptureCollision(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot, bool allowSceneQueries)
    {
        var collision = particleSystem.collision;
        snapshot.CollisionEnabled = collision.enabled;
        if (!collision.enabled)
            return;

        snapshot.CollisionType = collision.type;
        snapshot.CollisionRadiusScale = collision.radiusScale;
        snapshot.CollisionBounce = GetCurveConstantFloat(collision.bounceMultiplier);
        snapshot.CollisionFriction = GetCurveConstantFloat(collision.dampen);
        snapshot.CollisionLifetimeLoss = GetCurveConstantFloat(collision.lifetimeLoss);

        if (collision.type == ParticleSystemCollisionType.Planes)
        {
            for (var i = 0; i < MaxCollisionPlanes; i++)
            {
                var planeTransform = collision.GetPlane(i);
                if (planeTransform == null)
                    continue;

                snapshot.CollisionPlanes.Add(new CollisionPlaneSnapshot
                {
                    Position = particleSystem.transform.InverseTransformPoint(planeTransform.position),
                    Normal = particleSystem.transform.InverseTransformDirection(planeTransform.forward).normalized
                });
            }
        }
        else if (collision.type == ParticleSystemCollisionType.World)
        {
            if (allowSceneQueries)
            {
                CaptureNearbyCollisionColliders(particleSystem, snapshot);
                ParticleToVfxSdfCollisionMatcher.ResolveForSnapshot(snapshot);
                snapshot.Warnings.Add("World collision is approximated with a containment sphere until a Signed Distance Field is assigned.");
            }
            else
            {
                snapshot.Warnings.Add("World collision is approximated with a containment sphere; scene colliders/SDF were not scanned because conversion ran on an isolated prefab asset.");
            }
        }
    }

    const float NearbyColliderSearchRadius = 50f;
    const int MaxNearbyCollisionColliders = 4;

    static void CaptureNearbyCollisionColliders(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        var origin = particleSystem.transform.position;
        var colliders = UnityEngine.Object.FindObjectsByType<Collider>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        var candidates = new List<(Collider collider, float distance)>();
        foreach (var collider in colliders)
        {
            if (collider == null || collider.isTrigger || !collider.enabled)
                continue;

            var closest = collider.ClosestPoint(origin);
            var distance = Vector3.Distance(origin, closest);
            if (distance > NearbyColliderSearchRadius)
                continue;

            candidates.Add((collider, distance));
        }

        foreach (var candidate in candidates
                     .OrderBy(pair => pair.distance)
                     .Take(MaxNearbyCollisionColliders))
        {
            snapshot.NearbyCollisionColliders.Add(new CollisionColliderHint
            {
                ColliderName = candidate.collider.name,
                ColliderType = candidate.collider.GetType().Name,
                Distance = candidate.distance
            });
        }
    }

    static float GetCurveConstantFloat(ParticleSystem.MinMaxCurve curve)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return curve.constant;
            case ParticleSystemCurveMode.TwoConstants:
                return (curve.constantMin + curve.constantMax) * 0.5f;
            default:
                return 0f;
        }
    }

    static int GetCurveConstantInt(ParticleSystem.MinMaxCurve curve)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return Mathf.RoundToInt(curve.constant);
            case ParticleSystemCurveMode.TwoConstants:
                return Mathf.RoundToInt((curve.constantMin + curve.constantMax) * 0.5f);
            default:
                return 0;
        }
    }

    static void CaptureBursts(ParticleSystem.EmissionModule emission, ParticleEffectSnapshot snapshot)
    {
        for (var i = 0; i < emission.burstCount; i++)
        {
            var burst = emission.GetBurst(i);
            snapshot.Bursts.Add(new EmissionBurstSnapshot
            {
                Time = burst.time,
                CountMin = burst.minCount,
                CountMax = burst.maxCount,
                Count = Mathf.Max(1, burst.maxCount > 0 ? burst.maxCount : burst.minCount),
                CycleCount = burst.cycleCount,
                RepeatInterval = burst.repeatInterval,
                Probability = burst.probability
            });
        }
    }

    static void CaptureSubEmitters(
        ParticleSystem particleSystem,
        ParticleEffectSnapshot snapshot,
        Transform rootTransform)
    {
        var subEmitters = particleSystem.subEmitters;
        if (!subEmitters.enabled)
            return;

        for (var i = 0; i < subEmitters.subEmittersCount; i++)
        {
            var subSystem = subEmitters.GetSubEmitterSystem(i);
            if (subSystem == null)
                continue;

            snapshot.SubEmitters.Add(new SubEmitterSnapshot
            {
                Type = subEmitters.GetSubEmitterType(i),
                TargetPath = BuildHierarchyPath(rootTransform, subSystem.transform),
                EmitProbability = subEmitters.GetSubEmitterEmitProbability(i)
            });
        }
    }

    static void ResolveSubEmitterTargets(
        List<ParticleEffectSnapshot> systems,
        Dictionary<ParticleSystem, string> pathLookup)
    {
        var indexByPath = new Dictionary<string, int>();
        for (var i = 0; i < systems.Count; i++)
            indexByPath[systems[i].HierarchyPath] = i;

        foreach (var system in systems)
        {
            foreach (var subEmitter in system.SubEmitters)
            {
                if (indexByPath.TryGetValue(subEmitter.TargetPath, out var targetIndex))
                    subEmitter.TargetSystemIndex = targetIndex;
                else
                    system.Warnings.Add($"Sub-emitter target '{subEmitter.TargetPath}' is outside the converted hierarchy.");
            }
        }
    }

    static float EvaluateEmissionRate(ParticleSystem.MinMaxCurve curve, List<string> warnings)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return curve.constant;
            case ParticleSystemCurveMode.TwoConstants:
                return (curve.constantMin + curve.constantMax) * 0.5f;
            default:
                warnings?.Add("Emission rate curve approximated as average value.");
                return MinMaxSnapshot.FromCurve(curve, warnings, "Emission Rate").Constant;
        }
    }

    static string BuildHierarchyPath(Transform root, Transform target)
    {
        if (root == target)
            return root.name;

        var segments = new Stack<string>();
        var current = target;
        while (current != null && current != root.parent)
        {
            segments.Push(current.name);
            if (current == root)
                break;
            current = current.parent;
        }

        return string.Join("/", segments);
    }

    static void CaptureExternalForces(
        ParticleSystem particleSystem,
        ParticleEffectSnapshot snapshot,
        Transform systemTransform,
        bool allowSceneQueries)
    {
        var externalForces = particleSystem.externalForces;
        snapshot.ExternalForcesEnabled = externalForces.enabled;
        if (!externalForces.enabled)
            return;

        snapshot.ExternalForcesMultiplier = MinMaxSnapshot.FromCurve(
            externalForces.multiplier,
            snapshot.Warnings,
            "External Forces Multiplier");

        if (!allowSceneQueries)
        {
            snapshot.Warnings.Add("External Forces enabled but scene Wind Zones were not scanned because conversion ran on an isolated prefab asset; assign forces manually.");
            return;
        }

        if (TryResolveDirectionalWindZone(systemTransform.position, out var windZone, out var force))
        {
            snapshot.HasSceneWindZone = true;
            snapshot.SceneWindZoneName = windZone.name;
            snapshot.WindZoneForce = force;
            return;
        }

        snapshot.Warnings.Add("External Forces enabled but no directional Wind Zone was found in the scene.");
    }

    static bool TryResolveDirectionalWindZone(Vector3 position, out WindZone windZone, out Vector3 force)
    {
        windZone = null;
        force = Vector3.zero;

        var windZones = UnityEngine.Object.FindObjectsByType<WindZone>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        var bestDistance = float.MaxValue;
        foreach (var candidate in windZones)
        {
            if (candidate == null || candidate.mode != WindZoneMode.Directional)
                continue;

            var distance = Vector3.Distance(position, candidate.transform.position);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            windZone = candidate;
            force = candidate.transform.forward * candidate.windMain;
        }

        return windZone != null && force.sqrMagnitude > 0.0001f;
    }

    static void RecordUnsupportedModules(ParticleSystem particleSystem, ParticleEffectSnapshot snapshot)
    {
        if (snapshot.InheritVelocityEnabled)
        {
            if (snapshot.InheritVelocityMultiplier.UsesCurve)
                snapshot.Warnings.Add("Inherit Velocity uses a curve; approximated as a constant multiplier.");

            switch (snapshot.InheritVelocityMode)
            {
                case ParticleSystemInheritVelocityMode.Initial:
                    snapshot.Warnings.Add(
                        $"Inherit Velocity (Initial, x{snapshot.InheritVelocityMultiplier.Constant:0.##}): parent motion is not baked. Keep the VisualEffect on the moving transform; GPU-event sub-emitters inherit source velocity in VFX.");
                    break;
                case ParticleSystemInheritVelocityMode.Current:
                    snapshot.Warnings.Add(
                        "Inherit Velocity (Current): no VFX equivalent. Parent motion affects particles only if the VisualEffect transform moves with the emitter.");
                    break;
                default:
                    snapshot.Warnings.Add(
                        $"Inherit Velocity mode '{snapshot.InheritVelocityMode}' is not converted; verify motion on the VisualEffect transform.");
                    break;
            }

            if (snapshot.SimulationSpace == ParticleSystemSimulationSpace.Local)
                snapshot.Warnings.Add("Inherit Velocity on local-space systems may differ; keep the VisualEffect on the moving transform.");
        }

        if (snapshot.ScalingMode != ParticleSystemScalingMode.Local)
            snapshot.Warnings.Add($"Scaling mode '{snapshot.ScalingMode}' may differ from VFX local transform scaling.");

        if (snapshot.RenderMode == ParticleSystemRenderMode.Mesh && snapshot.RenderMesh == null)
            snapshot.Warnings.Add("Mesh render mode has no persistent mesh asset; using billboard output.");

        if (snapshot.RenderMode == ParticleSystemRenderMode.Stretch
            && (!Mathf.Approximately(snapshot.LengthScale, 1f) || snapshot.VelocityScale > 0.001f))
            snapshot.Warnings.Add("Stretch billboard length/velocity scale is not converted; only Along Velocity orientation is applied.");

        if (snapshot.NoiseEnabled)
            snapshot.Warnings.Add("Noise/Turbulence is approximated; motion may differ from Shuriken.");

        if (IsMeshShape(snapshot.ShapeType) && !HasResolvableMeshShape(snapshot))
            snapshot.Warnings.Add($"Shape '{snapshot.ShapeType}' has no mesh assigned; using a box spawn volume.");
    }

    static Mesh ResolvePersistentRenderMesh(ParticleSystemRenderer renderer)
    {
        if (renderer == null)
            return null;

        var serializedRenderer = new SerializedObject(renderer);
        var meshProperty = serializedRenderer.FindProperty("m_Mesh");
        if (meshProperty?.objectReferenceValue is Mesh assignedMesh
            && EditorUtility.IsPersistent(assignedMesh))
            return assignedMesh;

        var mesh = renderer.mesh;
        if (mesh != null && EditorUtility.IsPersistent(mesh))
            return mesh;

        return null;
    }

    static Mesh ResolveMeshFromRenderer(MeshRenderer meshRenderer)
    {
        if (meshRenderer == null)
            return null;

        return meshRenderer.GetComponent<MeshFilter>()?.sharedMesh;
    }

    static bool IsMeshShape(ParticleSystemShapeType shapeType)
    {
        return shapeType == ParticleSystemShapeType.Mesh
               || shapeType == ParticleSystemShapeType.MeshRenderer
               || shapeType == ParticleSystemShapeType.SkinnedMeshRenderer
               || shapeType == ParticleSystemShapeType.Sprite
               || shapeType == ParticleSystemShapeType.SpriteRenderer;
    }

    static bool HasResolvableMeshShape(ParticleEffectSnapshot snapshot)
    {
        return snapshot.ShapeType switch
        {
            ParticleSystemShapeType.Mesh => snapshot.ShapeMesh != null,
            ParticleSystemShapeType.MeshRenderer => snapshot.ShapeRendererMesh != null,
            ParticleSystemShapeType.SkinnedMeshRenderer => snapshot.ShapeSkinnedMeshRenderer != null
                && snapshot.ShapeSkinnedMeshRenderer.sharedMesh != null,
            ParticleSystemShapeType.Sprite => snapshot.ShapeSprite != null,
            ParticleSystemShapeType.SpriteRenderer => snapshot.ShapeSpriteRenderer != null,
            _ => false
        };
    }
}
