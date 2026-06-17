using System;
using System.Collections.Generic;

public enum ParticleToVfxVisualValidationStatus
{
    Pending,
    Passed,
    Review,
    Failed,
    Skipped,
    Error
}

[Serializable]
public sealed class ParticleToVfxVisualValidationEntry
{
    public string FamilyKey;
    public string DisplayName;
    public string SourcePrefabPath;
    public string VfxPrefabPath;
    public string VfxAssetPath;
    public int VariantCount;
    public string Status;
    public float DiffScore;
    public int ShurikenAliveCount;
    public int VfxAliveCount;
    public float CaptureTimeSeconds;
    public string CompareImagePath;
    public string ShurikenImagePath;
    public string VfxImagePath;
    public string Notes;
}

[Serializable]
public sealed class ParticleToVfxVisualValidationManifestFile
{
    public string OutputFolder;
    public string ValidationFolder;
    public long GeneratedUtcTicks;
    public int TotalFamilies;
    public int PassedCount;
    public int ReviewCount;
    public int FailedCount;
    public int ErrorCount;
    public List<ParticleToVfxVisualValidationEntry> Entries = new();
}

public static class ParticleToVfxVisualValidationPaths
{
    public const string ManifestFileName = "VisualValidationManifest.json";
    public const string ValidationSubfolder = "VisualValidation";

    public static string GetValidationFolder(string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
            return null;

        outputFolder = outputFolder.Replace('\\', '/').Trim();
        if (!outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            return null;

        return $"{outputFolder}/{ValidationSubfolder}";
    }

    public static string GetManifestPath(string outputFolder)
    {
        var validationFolder = GetValidationFolder(outputFolder);
        return string.IsNullOrEmpty(validationFolder) ? null : $"{validationFolder}/{ManifestFileName}";
    }

    public static string GetFamilyFolder(string outputFolder, string familyKey)
    {
        var validationFolder = GetValidationFolder(outputFolder);
        if (string.IsNullOrEmpty(validationFolder))
            return null;

        return $"{validationFolder}/{MakeSafeFolderName(familyKey)}";
    }

    public static string StatusToString(ParticleToVfxVisualValidationStatus status)
    {
        return status switch
        {
            ParticleToVfxVisualValidationStatus.Passed => "Passed",
            ParticleToVfxVisualValidationStatus.Review => "Review",
            ParticleToVfxVisualValidationStatus.Failed => "Failed",
            ParticleToVfxVisualValidationStatus.Skipped => "Skipped",
            ParticleToVfxVisualValidationStatus.Error => "Error",
            _ => "Pending"
        };
    }

    static string MakeSafeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value.Replace(' ', '_');
    }
}
