#if UNITY_EDITOR
using UnityEditor;
#endif

public static class ParticleToVfxVisualBatchSession
{
    public const float DefaultCaptureTimeSeconds = 1.5f;
    public const int CaptureWidth = 640;
    public const int CaptureHeight = 480;

    public const string RunningKey = "ParticleToVfxVisualBatch.Running";
    public const string CancelKey = "ParticleToVfxVisualBatch.Cancel";
    public const string PendingCompleteKey = "ParticleToVfxVisualBatch.PendingComplete";
    public const string StatusKey = "ParticleToVfxVisualBatch.Status";
    public const string IndexKey = "ParticleToVfxVisualBatch.Index";
    public const string TotalKey = "ParticleToVfxVisualBatch.Total";
    public const string OutputFolderKey = "ParticleToVfxVisualBatch.OutputFolder";

    public static void ReportStatus(string message, int index, int total)
    {
#if UNITY_EDITOR
        SessionState.SetString(StatusKey, message);
        SessionState.SetInt(IndexKey, index);
        SessionState.SetInt(TotalKey, UnityEngine.Mathf.Max(1, total));
#endif
    }

    public static void MarkComplete(string message)
    {
#if UNITY_EDITOR
        SessionState.SetString(StatusKey, message);
        SessionState.SetBool(PendingCompleteKey, true);
        SessionState.SetBool(RunningKey, false);
        SessionState.SetBool(CancelKey, false);
#endif
    }

    public static bool IsCancelRequested()
    {
#if UNITY_EDITOR
        return SessionState.GetBool(CancelKey, false);
#else
        return false;
#endif
    }
}
