using System.Collections.Generic;
using UnityEngine;

public static class ParticleToVfxGradientUtility
{
    public const int MaxColorKeys = 8;
    public const int MaxAlphaKeys = 8;

    public static Gradient SimplifyForVfx(
        Gradient source,
        ParticleEffectSnapshot snapshot = null,
        string context = "Gradient")
    {
        if (source == null)
            return null;

        var colorCount = source.colorKeys?.Length ?? 0;
        var alphaCount = source.alphaKeys?.Length ?? 0;
        if (colorCount <= MaxColorKeys && alphaCount <= MaxAlphaKeys)
            return Duplicate(source);

        var simplified = Duplicate(source);
        simplified.SetKeys(
            ResampleColorKeys(source, MaxColorKeys),
            ResampleAlphaKeys(source, MaxAlphaKeys));

        if (colorCount > MaxColorKeys || alphaCount > MaxAlphaKeys)
        {
            snapshot?.Warnings.Add(
                $"{context} had {colorCount} color / {alphaCount} alpha keys; simplified to {MaxColorKeys} for VFX Graph.");
        }

        return simplified;
    }

    static Gradient Duplicate(Gradient source)
    {
        var copy = new Gradient();
        copy.SetKeys(source.colorKeys, source.alphaKeys);
        copy.mode = source.mode;
        copy.colorSpace = source.colorSpace;
        return copy;
    }

    static GradientColorKey[] ResampleColorKeys(Gradient source, int maxKeys)
    {
        if (maxKeys <= 1)
            return new[] { new GradientColorKey(source.Evaluate(0f), 0f) };

        var keys = new List<GradientColorKey>(maxKeys);
        for (var i = 0; i < maxKeys; i++)
        {
            var time = i / (maxKeys - 1f);
            keys.Add(new GradientColorKey(source.Evaluate(time), time));
        }

        return keys.ToArray();
    }

    static GradientAlphaKey[] ResampleAlphaKeys(Gradient source, int maxKeys)
    {
        var alphaKeys = source.alphaKeys;
        if (alphaKeys == null || alphaKeys.Length == 0)
            return new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };

        if (alphaKeys.Length <= maxKeys)
            return alphaKeys;

        if (maxKeys <= 1)
            return new[] { new GradientAlphaKey(alphaKeys[0].alpha, 0f) };

        var keys = new List<GradientAlphaKey>(maxKeys);
        for (var i = 0; i < maxKeys; i++)
        {
            var time = i / (maxKeys - 1f);
            keys.Add(new GradientAlphaKey(EvaluateAlpha(source, time), time));
        }

        return keys.ToArray();
    }

    static float EvaluateAlpha(Gradient source, float time)
    {
        var alphaKeys = source.alphaKeys;
        if (alphaKeys == null || alphaKeys.Length == 0)
            return 1f;

        time = Mathf.Clamp01(time);
        if (time <= alphaKeys[0].time)
            return alphaKeys[0].alpha;

        for (var i = 1; i < alphaKeys.Length; i++)
        {
            var previous = alphaKeys[i - 1];
            var current = alphaKeys[i];
            if (time > current.time)
                continue;

            var range = current.time - previous.time;
            if (range <= Mathf.Epsilon)
                return current.alpha;

            var t = (time - previous.time) / range;
            return Mathf.Lerp(previous.alpha, current.alpha, t);
        }

        return alphaKeys[alphaKeys.Length - 1].alpha;
    }
}
