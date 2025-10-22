using System;
using System.IO;
using Mediapipe;
using UnityEngine;

[DefaultExecutionOrder(-100)]
public class HandLandmarkerModelRedirect : MonoBehaviour
{
  [SerializeField] private string sourceFileName = "hand_landmarker.bytes";
  [SerializeField] private string cacheFileName = "hand_landmarker_cache.bytes";
  [SerializeField] private bool overwriteCachedFile = false;

  private void Awake()
  {
    TryRedirectModel();
  }

  private void TryRedirectModel()
  {
    var streamingPath = Path.Combine(Application.streamingAssetsPath, sourceFileName);
    var cacheDirectory = Path.Combine(Application.persistentDataPath, "MediaPipeModels");
    var cachePath = Path.Combine(cacheDirectory, cacheFileName);

    try
    {
      if (!File.Exists(streamingPath))
      {
        Debug.LogError($"HandLandmarkerModelRedirect: source file not found at {streamingPath}");
        return;
      }

      if (!Directory.Exists(cacheDirectory))
      {
        Directory.CreateDirectory(cacheDirectory);
      }

      if (overwriteCachedFile || !File.Exists(cachePath) || new FileInfo(cachePath).Length == 0)
      {
        File.Copy(streamingPath, cachePath, true);
      }

      ResourceUtil.SetAssetPath(sourceFileName, cachePath);
    }
    catch (Exception ex)
    {
      Debug.LogError($"HandLandmarkerModelRedirect: failed to cache model. {ex}");
    }
  }
}
