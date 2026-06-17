using System;
using System.IO;
using UnityEngine;

public static class ParticleToVfxVisualCaptureUtility
{
    public const float PassDiffThreshold = 0.18f;
    public const float ReviewDiffThreshold = 0.35f;

    public static void CaptureCamera(Camera camera, string absolutePath, int width, int height)
    {
        if (camera == null)
            throw new ArgumentNullException(nameof(camera));

        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;

        try
        {
            camera.targetTexture = renderTexture;
            camera.Render();

            RenderTexture.active = renderTexture;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            texture.Apply();
            File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            UnityEngine.Object.Destroy(texture);
        }
        finally
        {
            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            renderTexture.Release();
            UnityEngine.Object.Destroy(renderTexture);
        }
    }

    public static float ComputeNormalizedDiff(string leftImagePath, string rightImagePath)
    {
        Texture2D left = null;
        Texture2D right = null;
        try
        {
            left = LoadTexture(leftImagePath);
            right = LoadTexture(rightImagePath);
            if (left == null || right == null)
                return 1f;

            var width = Mathf.Min(left.width, right.width);
            var height = Mathf.Min(left.height, right.height);
            if (width <= 0 || height <= 0)
                return 1f;

            double total = 0d;
            var count = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var leftColor = left.GetPixel(x, y);
                    var rightColor = right.GetPixel(x, y);

                    if (leftColor.a < 0.02f && rightColor.a < 0.02f)
                        continue;

                    var leftLuma = leftColor.grayscale;
                    var rightLuma = rightColor.grayscale;
                    total += Math.Abs(leftLuma - rightLuma);
                    count++;
                }
            }

            if (count == 0)
                return 1f;

            return (float)(total / count);
        }
        finally
        {
            if (left != null)
                UnityEngine.Object.Destroy(left);
            if (right != null)
                UnityEngine.Object.Destroy(right);
        }
    }

    public static ParticleToVfxVisualValidationStatus ClassifyResult(
        float diffScore,
        int shurikenAliveCount,
        int vfxAliveCount)
    {
        if (vfxAliveCount <= 0 && shurikenAliveCount > 0)
            return ParticleToVfxVisualValidationStatus.Failed;

        if (diffScore >= ReviewDiffThreshold)
            return ParticleToVfxVisualValidationStatus.Failed;

        if (diffScore >= PassDiffThreshold)
            return ParticleToVfxVisualValidationStatus.Review;

        return ParticleToVfxVisualValidationStatus.Passed;
    }

    static Texture2D LoadTexture(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        var bytes = File.ReadAllBytes(path);
        var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        if (!texture.LoadImage(bytes))
        {
            UnityEngine.Object.Destroy(texture);
            return null;
        }

        return texture;
    }
}
