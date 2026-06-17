using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

public static class ParticleToVfxConversionReportWriter
{
    public static string Write(string vfxAssetPath, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrEmpty(vfxAssetPath) || lines == null || lines.Count == 0)
            return null;

        var directory = Path.GetDirectoryName(vfxAssetPath);
        var baseName = Path.GetFileNameWithoutExtension(vfxAssetPath);
        var reportPath = Path.Combine(directory ?? string.Empty, $"{baseName}_ConversionReport.txt");

        var builder = new StringBuilder();
        builder.AppendLine("Particle System → VFX Graph Conversion Report");
        builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"VFX Asset: {vfxAssetPath}");
        builder.AppendLine();

        for (var i = 0; i < lines.Count; i++)
            builder.AppendLine(lines[i]);

        File.WriteAllText(reportPath, builder.ToString());
        AssetDatabase.ImportAsset(reportPath);
        return reportPath;
    }
}
