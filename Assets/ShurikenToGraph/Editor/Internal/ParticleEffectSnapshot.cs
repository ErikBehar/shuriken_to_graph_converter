using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Normalized description of a Shuriken ParticleSystem used to build a VFX Graph.
/// </summary>
public sealed class ParticleEffectSnapshot
{
    public string SourceName;
    public string HierarchyPath;
    public int SystemIndex;

    public Vector3 LocalPosition;
    public Vector3 LocalRotation;
    public Vector3 LocalScale;

    public Vector3 SpawnOffset;
    public Vector3 SpawnRotation;

    public bool Looping;
    public float Duration;
    public float SimulationSpeed;
    public bool Prewarm;
    public int MaxParticles;
    public ParticleSystemSimulationSpace SimulationSpace;
    public ParticleSystemScalingMode ScalingMode;

    public float EmissionRate;
    public MinMaxSnapshot RateOverTime = new MinMaxSnapshot(0f);
    public float RateOverDistance;
    public readonly List<EmissionBurstSnapshot> Bursts = new();

    public MinMaxSnapshot StartLifetime;
    public MinMaxSnapshot StartSpeed;
    public MinMaxSnapshot StartSize;
    public bool StartSize3D;
    public MinMaxSnapshot StartSizeX;
    public MinMaxSnapshot StartSizeY;
    public MinMaxSnapshot StartSizeZ;
    public Color StartColor;
    public bool StartColorTwoColors;
    public Color StartColorMin;
    public Color StartColorMax;
    public MinMaxSnapshot StartRotation;

    public float GravityModifier;

    public bool ShapeEnabled;
    public ParticleSystemShapeType ShapeType;
    public Vector3 ShapePosition;
    public Vector3 ShapeRotation;
    public Vector3 ShapeScale;
    public float ShapeRadius;
    public float ShapeAngle;
    public float ShapeLength;
    public float ShapeDonutRadius;
    public float ShapeArc;
    public float ShapeRadiusThickness;
    public float ShapeRandomDirectionAmount;
    public float ShapeSphericalDirectionAmount;

    public bool VelocityOverLifetimeEnabled;
    public MinMaxSnapshot VelocityX;
    public MinMaxSnapshot VelocityY;
    public MinMaxSnapshot VelocityZ;
    public Vector3 OrbitalVelocity;
    public float RadialVelocity;
    public bool VelocityOverLifetimeWorldSpace;

    public bool SizeOverLifetimeEnabled;
    public AnimationCurveSnapshot SizeOverLifetime;
    public MinMaxSnapshot SizeOverLifetimeMultiplier = new MinMaxSnapshot(1f);

    public bool ColorOverLifetimeEnabled;
    public Gradient ColorOverLifetime;

    public bool RotationOverLifetimeEnabled;
    public MinMaxSnapshot AngularVelocityX;
    public MinMaxSnapshot AngularVelocityY;
    public MinMaxSnapshot AngularVelocityZ;

    public bool NoiseEnabled;
    public float NoiseStrength;
    public float NoiseStrengthMin;
    public float NoiseFrequency;
    public int NoiseOctaves;
    public float NoiseRoughness;
    public float NoiseLacunarity = 2f;
    public float NoiseScrollSpeed;

    public Texture2D MainTexture;
    public Color MaterialColor = Color.white;
    public bool UseAdditiveBlend;
    public bool SoftParticlesEnabled;
    public float SoftParticleNear;
    public float SoftParticleFar;

    public ParticleSystemRenderMode RenderMode;
    public ParticleSystemRenderSpace RenderAlignment;
    public float MaxParticleSize;
    public float LengthScale = 1f;
    public float VelocityScale = 0f;

    public bool InheritVelocityEnabled;
    public ParticleSystemInheritVelocityMode InheritVelocityMode;
    public MinMaxSnapshot InheritVelocityMultiplier = new MinMaxSnapshot(0f);

    public Mesh ShapeMesh;
    public MeshRenderer ShapeMeshRenderer;
    public Mesh ShapeRendererMesh;
    public SkinnedMeshRenderer ShapeSkinnedMeshRenderer;
    public Sprite ShapeSprite;
    public SpriteRenderer ShapeSpriteRenderer;

    public bool TextureSheetEnabled;
    public TextureSheetSnapshot TextureSheet;

    public bool TrailsEnabled;
    public TrailsSnapshot Trails;

    public bool ForceOverLifetimeEnabled;
    public MinMaxSnapshot ForceX;
    public MinMaxSnapshot ForceY;
    public MinMaxSnapshot ForceZ;

    public bool SizeBySpeedEnabled;
    public AnimationCurveSnapshot SizeBySpeed;
    public Vector2 SizeBySpeedRange = new Vector2(0f, 1f);

    public bool RotationBySpeedEnabled;
    public AnimationCurveSnapshot RotationBySpeed;
    public Vector2 RotationBySpeedRange = new Vector2(0f, 1f);

    public bool ColorBySpeedEnabled;
    public Gradient ColorBySpeed;
    public Vector2 ColorBySpeedRange = new Vector2(0f, 1f);

    public bool LimitVelocityEnabled;
    public float LimitVelocityMagnitude;
    public float LimitVelocityDampen;
    public float LimitVelocityDrag;

    public bool CollisionEnabled;
    public ParticleSystemCollisionType CollisionType;
    public float CollisionRadiusScale = 1f;
    public float CollisionBounce;
    public float CollisionFriction;
    public float CollisionLifetimeLoss;
    public readonly List<CollisionPlaneSnapshot> CollisionPlanes = new();
    public readonly List<CollisionColliderHint> NearbyCollisionColliders = new();

    public bool ExternalForcesEnabled;
    public MinMaxSnapshot ExternalForcesMultiplier = new MinMaxSnapshot(1f);
    public bool HasSceneWindZone;
    public string SceneWindZoneName;
    public Vector3 WindZoneForce;

    public string MaterialName;
    public string ShaderName;
    public bool UsesCustomShader;
    public string SuggestedShaderGraphPath;
    public bool UseAlphaClipping;
    public float AlphaCutoff = 0.5f;
    public bool UseDoubleSided;
    public bool CastShadows;
    public ShadowCastingMode ShadowCastingMode = ShadowCastingMode.Off;
    public bool ReceiveShadows = true;
    public int SortingOrder;
    public float SortingFudge;
    public string SortingLayerName;

    public bool TriggerEnabled;
    public int TriggerColliderCount;
    public readonly List<string> TriggerColliderNames = new();

    public bool LightsEnabled;
    public float LightRatio = 1f;

    public bool CustomDataEnabled;
    public int CustomDataChannelCount;

    public Mesh RenderMesh;

    public string SubgraphAssetPath;

    public readonly List<SubEmitterSnapshot> SubEmitters = new();
    public readonly List<string> Warnings = new();
    public readonly List<string> UnsupportedFeatures = new();
}

public sealed class ParticleEffectHierarchySnapshot
{
    public string RootName;
    public Vector3 RootLocalPosition;
    public Vector3 RootLocalRotation;
    public Vector3 RootLocalScale;
    public float PlaybackSpeed = 1f;
    public int SortingOrder;
    public string SortingLayerName;
    public ShadowCastingMode ShadowCastingMode = ShadowCastingMode.Off;
    public bool ReceiveShadows = true;
    public readonly List<ParticleEffectSnapshot> Systems = new();
    public readonly List<string> SubgraphAssetPaths = new();
    public readonly List<string> Warnings = new();
}

public sealed class TextureSheetSnapshot
{
    public int TilesX = 1;
    public int TilesY = 1;
    public ParticleSystemAnimationType AnimationType;
    public ParticleSystemAnimationRowMode RowMode;
    public ParticleSystemAnimationTimeMode TimeMode = ParticleSystemAnimationTimeMode.Lifetime;
    public int Cycles = 1;
    public float Fps = 30f;
    public float FrameOverTimeSpeed = 1f;
    public bool FrameOverTimeUsesCurve;
    public int StartFrame;
    public int EndFrame;
    public int FlipbookRow;
}

public sealed class TrailsSnapshot
{
    public float Lifetime = 1f;
    public float WidthMultiplier = 1f;
    public AnimationCurveSnapshot WidthOverTrail;
    public float MinimumVertexDistance = 0.1f;
    public bool WorldSpace = true;
    public bool DieWithParticles = true;
}

public sealed class CollisionPlaneSnapshot
{
    public Vector3 Position;
    public Vector3 Normal;
}

public sealed class CollisionColliderHint
{
    public string ColliderName;
    public string ColliderType;
    public float Distance;
    public string SuggestedSdfAssetPath;
}

public sealed class EmissionBurstSnapshot
{
    public float Time;
    public int Count;
    public int CountMin;
    public int CountMax;
    public int CycleCount;
    public float RepeatInterval;
    public float Probability = 1f;
}

public sealed class SubEmitterSnapshot
{
    public ParticleSystemSubEmitterType Type;
    public string TargetPath;
    public int TargetSystemIndex = -1;
    public float EmitProbability = 1f;
    public bool SkipAutoWire;
}

public enum MinMaxMode
{
    Constant,
    TwoConstants,
    Curve,
    TwoCurves
}

public readonly struct MinMaxSnapshot
{
    public readonly MinMaxMode Mode;
    public readonly bool IsRandom;
    public readonly float Constant;
    public readonly float Min;
    public readonly float Max;
    public readonly AnimationCurve Curve;
    public readonly AnimationCurve CurveMin;
    public readonly AnimationCurve CurveMax;

    public MinMaxSnapshot(float constant)
    {
        Mode = MinMaxMode.Constant;
        IsRandom = false;
        Constant = constant;
        Min = constant;
        Max = constant;
        Curve = null;
        CurveMin = null;
        CurveMax = null;
    }

    public MinMaxSnapshot(float min, float max)
    {
        Mode = MinMaxMode.TwoConstants;
        IsRandom = true;
        Constant = (min + max) * 0.5f;
        Min = min;
        Max = max;
        Curve = null;
        CurveMin = null;
        CurveMax = null;
    }

    MinMaxSnapshot(
        MinMaxMode mode,
        float constant,
        float min,
        float max,
        AnimationCurve curve,
        AnimationCurve curveMin,
        AnimationCurve curveMax)
    {
        Mode = mode;
        IsRandom = mode == MinMaxMode.TwoConstants || mode == MinMaxMode.TwoCurves;
        Constant = constant;
        Min = min;
        Max = max;
        Curve = curve;
        CurveMin = curveMin;
        CurveMax = curveMax;
    }

    public bool UsesCurve =>
        Mode == MinMaxMode.Curve || Mode == MinMaxMode.TwoCurves;

    public static MinMaxSnapshot FromCurve(ParticleSystem.MinMaxCurve curve, List<string> warnings, string propertyName)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return new MinMaxSnapshot(curve.constant);
            case ParticleSystemCurveMode.TwoConstants:
                return new MinMaxSnapshot(curve.constantMin, curve.constantMax);
            case ParticleSystemCurveMode.Curve:
                warnings?.Add($"{propertyName} uses an emission-time curve; approximated as random min/max.");
                return FromSingleCurve(curve.curve);
            case ParticleSystemCurveMode.TwoCurves:
                warnings?.Add($"{propertyName} uses dual emission-time curves; approximated as random min/max.");
                return FromTwoCurves(curve.curveMin, curve.curveMax);
            default:
                return new MinMaxSnapshot(curve.constant);
        }
    }

    public static MinMaxSnapshot FromEmissionRateCurve(ParticleSystem.MinMaxCurve curve, List<string> warnings)
    {
        switch (curve.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return new MinMaxSnapshot(curve.constant);
            case ParticleSystemCurveMode.TwoConstants:
                return new MinMaxSnapshot(curve.constantMin, curve.constantMax);
            case ParticleSystemCurveMode.Curve:
                return FromEmissionCurve(curve.curve);
            case ParticleSystemCurveMode.TwoCurves:
                return FromEmissionTwoCurves(curve.curveMin, curve.curveMax);
            default:
                return new MinMaxSnapshot(curve.constant);
        }
    }

    static MinMaxSnapshot FromEmissionCurve(AnimationCurve curve)
    {
        GetCurveRange(curve, out var min, out var max);
        return new MinMaxSnapshot(
            MinMaxMode.Curve,
            (min + max) * 0.5f,
            min,
            max,
            curve,
            null,
            null);
    }

    static MinMaxSnapshot FromEmissionTwoCurves(AnimationCurve curveMin, AnimationCurve curveMax)
    {
        GetCurveRange(curveMin, out var minA, out var maxA);
        GetCurveRange(curveMax, out var minB, out var maxB);
        var min = Mathf.Min(minA, minB);
        var max = Mathf.Max(maxA, maxB);
        return new MinMaxSnapshot(
            MinMaxMode.TwoCurves,
            (min + max) * 0.5f,
            min,
            max,
            null,
            curveMin,
            curveMax);
    }

    static MinMaxSnapshot FromSingleCurve(AnimationCurve curve)
    {
        GetCurveRange(curve, out var min, out var max);
        return new MinMaxSnapshot(min, max);
    }

    static MinMaxSnapshot FromTwoCurves(AnimationCurve curveMin, AnimationCurve curveMax)
    {
        GetCurveRange(curveMin, out var minA, out var maxA);
        GetCurveRange(curveMax, out var minB, out var maxB);
        return new MinMaxSnapshot(Mathf.Min(minA, minB), Mathf.Max(maxA, maxB));
    }

    static void GetCurveRange(AnimationCurve curve, out float min, out float max)
    {
        min = max = 0f;
        if (curve == null || curve.length == 0)
        {
            min = max = 1f;
            return;
        }

        min = max = curve.Evaluate(0f);
        for (var i = 0; i < curve.length; i++)
        {
            var value = curve.keys[i].value;
            min = Mathf.Min(min, value);
            max = Mathf.Max(max, value);
        }

        min = Mathf.Min(min, curve.Evaluate(1f));
        max = Mathf.Max(max, curve.Evaluate(1f));
    }
}

public readonly struct AnimationCurveSnapshot
{
    public readonly bool HasCurve;
    public readonly float Start;
    public readonly float End;
    public readonly AnimationCurve Curve;

    public AnimationCurveSnapshot(AnimationCurve curve)
    {
        if (curve == null || curve.length == 0)
        {
            HasCurve = false;
            Start = 1f;
            End = 1f;
            Curve = null;
            return;
        }

        HasCurve = true;
        Start = curve.Evaluate(0f);
        End = curve.Evaluate(1f);
        Curve = new AnimationCurve(curve.keys);
    }

    public AnimationCurve ToCurve()
    {
        if (!HasCurve || Curve == null)
            return AnimationCurve.Linear(0f, Start, 1f, End);

        return new AnimationCurve(Curve.keys);
    }
}
