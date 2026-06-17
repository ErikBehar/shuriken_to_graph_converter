using System.IO;

public static class ParticleToVfxOutputCleaner
{
    public static void CleanEffectOutput(string outputFolder, string rootName)
    {
        var safeName = ParticleToVfxConversionService.MakeSafeFileName(rootName);
        if (string.IsNullOrEmpty(outputFolder) || string.IsNullOrEmpty(safeName))
            return;

        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX.vfx");
        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX.vfx.meta");
        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX.prefab");
        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX.prefab.meta");
        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX_ConversionReport.txt");
        DeleteAbsolutePath($"{outputFolder}/{safeName}_VFX_ConversionReport.txt.meta");

        var subgraphFolder = $"{outputFolder}/{safeName}_Subgraphs";
        DeleteAbsoluteFolder(subgraphFolder);
        DeleteAbsolutePath($"{subgraphFolder}.meta");
    }

    static void DeleteAbsolutePath(string assetPath)
    {
        var absolutePath = ParticleToVfxAssetPaths.ToAbsolutePath(assetPath);
        if (File.Exists(absolutePath))
            File.Delete(absolutePath);
    }

    static void DeleteAbsoluteFolder(string assetFolderPath)
    {
        var absolutePath = ParticleToVfxAssetPaths.ToAbsolutePath(assetFolderPath);
        if (Directory.Exists(absolutePath))
            Directory.Delete(absolutePath, recursive: true);
    }
}
