using UnityEngine;
using UnityEngine.VFX;

namespace UnityEngine.VFX
{
    class SpawnAtCurveRate : VFXSpawnerCallbacks
    {
        public class InputProperties
        {
            public AnimationCurve RateCurve = AnimationCurve.Linear(0, 10, 1, 10);
            public AnimationCurve RateCurveMin = AnimationCurve.Linear(0, 0, 1, 0);
            public AnimationCurve RateCurveMax = AnimationCurve.Linear(0, 10, 1, 10);
            public bool UseRandomBetweenCurves;
            public float LoopDuration = 5f;
        }

        float m_SpawnAccumulator;

        static readonly int RateCurvePropertyId = Shader.PropertyToID("RateCurve");
        static readonly int RateCurveMinPropertyId = Shader.PropertyToID("RateCurveMin");
        static readonly int RateCurveMaxPropertyId = Shader.PropertyToID("RateCurveMax");
        static readonly int UseRandomBetweenCurvesPropertyId = Shader.PropertyToID("UseRandomBetweenCurves");
        static readonly int LoopDurationPropertyId = Shader.PropertyToID("LoopDuration");

        public sealed override void OnPlay(VFXSpawnerState state, VFXExpressionValues vfxValues, VisualEffect vfxComponent)
        {
            m_SpawnAccumulator = 0f;
        }

        public sealed override void OnUpdate(VFXSpawnerState state, VFXExpressionValues vfxValues, VisualEffect vfxComponent)
        {
            if (!state.playing || state.deltaTime <= 0f)
                return;

            var duration = Mathf.Max(vfxValues.GetFloat(LoopDurationPropertyId), 0.001f);
            var normalizedTime = Mathf.Clamp01(state.totalTime / duration);
            float rate;
            if (vfxValues.GetBool(UseRandomBetweenCurvesPropertyId))
            {
                var minCurve = vfxValues.GetAnimationCurve(RateCurveMinPropertyId);
                var maxCurve = vfxValues.GetAnimationCurve(RateCurveMaxPropertyId);
                var min = minCurve != null ? minCurve.Evaluate(normalizedTime) : 0f;
                var max = maxCurve != null ? maxCurve.Evaluate(normalizedTime) : 0f;
                rate = Random.Range(Mathf.Min(min, max), Mathf.Max(min, max));
            }
            else
            {
                var curve = vfxValues.GetAnimationCurve(RateCurvePropertyId);
                rate = curve != null ? curve.Evaluate(normalizedTime) : 0f;
            }

            m_SpawnAccumulator += Mathf.Max(rate, 0f) * state.deltaTime;
            var spawnCount = Mathf.FloorToInt(m_SpawnAccumulator);
            if (spawnCount <= 0)
                return;

            state.spawnCount += spawnCount;
            m_SpawnAccumulator -= spawnCount;
        }

        public sealed override void OnStop(VFXSpawnerState state, VFXExpressionValues vfxValues, VisualEffect vfxComponent)
        {
        }
    }
}
