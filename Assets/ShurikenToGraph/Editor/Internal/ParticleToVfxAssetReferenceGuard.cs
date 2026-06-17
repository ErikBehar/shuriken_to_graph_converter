using UnityEditor;
using UnityEngine;

public static class ParticleToVfxAssetReferenceGuard
{
    public static bool IsSafeForVfxAsset(Object value)
    {
        if (value == null)
            return true;

        if (value is GameObject or Component or Transform)
            return false;

        if (value is Object unityObject)
            return EditorUtility.IsPersistent(unityObject);

        return true;
    }

    public static T SanitizeForVfxAsset<T>(T value, string context, ParticleEffectSnapshot snapshot = null)
        where T : Object
    {
        if (value == null || IsSafeForVfxAsset(value))
            return value;

        snapshot?.Warnings.Add(
            $"Skipped non-persistent {typeof(T).Name} reference in {context}; assign it manually on the VFX blackboard if needed.");
        return null;
    }
}
