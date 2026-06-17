using System;
using UnityEditor;
using UnityEngine;

public static class ParticleToVfxConversionContext
{
    public const int MaxSystemsPerEffect = 24;
    public const int BatchGcInterval = 10;
    public const int BatchCooldownFrames = 1;

    public static bool IsBatchMode { get; private set; }

    static int s_BatchItemCount;

    public static void BeginBatch()
    {
        IsBatchMode = true;
        s_BatchItemCount = 0;
    }

    public static void EndBatch()
    {
        IsBatchMode = false;
        s_BatchItemCount = 0;
    }

    public static void OnBatchItemCompleted()
    {
        s_BatchItemCount++;
        if (s_BatchItemCount % BatchGcInterval == 0)
            ReleaseEditorMemory();
    }

    public static void ReleaseEditorMemory()
    {
        Resources.UnloadUnusedAssets();
        GC.Collect();
    }
}
