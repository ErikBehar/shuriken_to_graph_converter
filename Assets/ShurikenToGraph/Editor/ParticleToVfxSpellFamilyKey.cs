using System;
using System.Collections.Generic;
using System.Linq;

public static class ParticleToVfxSpellFamilyKey
{
    static readonly string[] ColorSuffixes =
    {
        "_Red",
        "_Blue",
        "_Green",
        "_Yellow",
        "_Purple"
    };

    public static string GetFamilyKey(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return string.Empty;

        var key = displayName.Trim();
        while (key.Contains(" Variant", StringComparison.Ordinal))
            key = key.Replace(" Variant", string.Empty, StringComparison.Ordinal);

        foreach (var suffix in ColorSuffixes)
            key = key.Replace(suffix, string.Empty, StringComparison.Ordinal);

        return key.Trim();
    }

    public static bool HasColorVariant(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        foreach (var suffix in ColorSuffixes)
        {
            if (displayName.Contains(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static int GetRepresentativePriority(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return int.MinValue;

        if (!HasColorVariant(displayName))
            return 100;

        if (displayName.Contains("_Red", StringComparison.Ordinal))
            return 50;

        return 10;
    }

    public static List<ParticleToVfxBatchItem> SelectFamilyRepresentatives(IEnumerable<ParticleToVfxBatchItem> items)
    {
        if (items == null)
            return new List<ParticleToVfxBatchItem>();

        return items
            .Where(CanValidateItem)
            .GroupBy(item => GetFamilyKey(item.DisplayName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => GetRepresentativePriority(item.DisplayName))
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool CanValidateItem(ParticleToVfxBatchItem item)
    {
        if (item == null)
            return false;

        if (item.Status != ParticleToVfxBatchItemStatus.Success
            && item.Status != ParticleToVfxBatchItemStatus.AlreadyConverted)
            return false;

        if (item.Result == null || !item.Result.Success)
            return false;

        return !string.IsNullOrEmpty(item.Result.VfxAssetPath)
               || item.Result.VfxAsset != null
               || !string.IsNullOrEmpty(item.Result.PrefabPath)
               || item.Result.VfxPrefab != null;
    }
}
