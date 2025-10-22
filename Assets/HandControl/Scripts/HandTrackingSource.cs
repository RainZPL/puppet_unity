using System;
using System.Collections;
using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Tasks = Mediapipe.Tasks;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using Mediapipe.Unity.Sample;
using UnityEngine;
using UnityEngine.Rendering;

namespace HandControl
{
  [DefaultExecutionOrder(-20)]
  public class HandTrackingSource : BaseRunner
  {
    [Serializable]
    public class Settings
    {
      public Tasks.Core.BaseOptions.Delegate inferenceDelegate = Tasks.Core.BaseOptions.Delegate.CPU;
      public ImageReadMode imageReadMode = ImageReadMode.CPU;
      public Tasks.Vision.Core.RunningMode runningMode = Tasks.Vision.Core.RunningMode.LIVE_STREAM;
      [Range(1, 2)] public int numHands = 1;
      [Range(0f, 1f)] public float minHandDetectionConfidence = 0.5f;
      [Range(0f, 1f)] public float minHandPresenceConfidence = 0.5f;
      [Range(0f, 1f)] public float minTrackingConfidence = 0.5f;
      [Tooltip("Relative path under StreamingAssets")] public string modelAsset = "hand_landmarker.bytes";
      [Tooltip("Number of frames cached for CPU read")] public int texturePoolSize = 4;
    }

    [Serializable]
    public class HandFrameData
    {
      public bool tracked;
      public bool isRight;
      public float handednessScore;
      public long timestampMillisec;
      public Vector3[] landmarks = Array.Empty<Vector3>();

      public void EnsureCapacity(int count)
      {
        if (count <= 0)
        {
          landmarks = Array.Empty<Vector3>();
          return;
        }

        if (landmarks == null || landmarks.Length != count)
        {
          landmarks = new Vector3[count];
        }
      }

      public void CopyFrom(HandFrameData other)
      {
        tracked = other.tracked;
        isRight = other.isRight;
        handednessScore = other.handednessScore;
        timestampMillisec = other.timestampMillisec;
        EnsureCapacity(other.landmarks?.Length ?? 0);

        for (var i = 0; i < landmarks.Length; i++)
        {
          landmarks[i] = other.landmarks[i];
        }
      }
    }

    [SerializeField] private Settings settings = new Settings();

    public event Action<HandFrameData> OnHandFrame;

    private TextureFramePool _texturePool;
    private HandLandmarker _handLandmarker;
    private Coroutine _runner;
    private Tasks.Vision.Core.ImageProcessingOptions _imageProcessingOptions;
    private HandLandmarkerResult _syncResult;
    private bool _inputHorizontallyFlipped;

    private readonly object _frameLock = new();
    private readonly HandFrameData _latestFrame = new();
    private readonly HandFrameData _eventFrame = new();
    private bool _frameDirty;

    public override void Play()
    {
      base.Play();
      if (_runner == null)
      {
        _runner = StartCoroutine(Run());
      }
    }

    public override void Pause()
    {
      base.Pause();
      ImageSourceProvider.ImageSource.Pause();
    }

    public override void Resume()
    {
      base.Resume();
      var _ = StartCoroutine(ImageSourceProvider.ImageSource.Resume());
    }

    public override void Stop()
    {
      base.Stop();
      if (_runner != null)
      {
        StopCoroutine(_runner);
        _runner = null;
      }
      TearDown();
    }

    private void Update()
    {
      if (!_frameDirty)
      {
        return;
      }

      HandFrameData snapshot = null;
      lock (_frameLock)
      {
        if (!_frameDirty)
        {
          return;
        }

        _eventFrame.CopyFrom(_latestFrame);
        _frameDirty = false;
        snapshot = _eventFrame;
      }

      OnHandFrame?.Invoke(snapshot);
    }

    private IEnumerator Run()
    {
      yield return AssetLoader.PrepareAssetAsync(settings.modelAsset);

      var options = BuildOptions(settings.runningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnLiveStreamResult : null);
      _handLandmarker = HandLandmarker.CreateFromOptions(options, GpuManager.GpuResources);

      var imageSource = ImageSourceProvider.ImageSource;
      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("HandTrackingSource: failed to start ImageSource.");
        yield break;
      }

      _texturePool = new TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, Mathf.Max(settings.texturePoolSize, 2));

      var transformationOptions = imageSource.GetTransformationOptions();
      var flipHorizontally = transformationOptions.flipHorizontally;
      var flipVertically = transformationOptions.flipVertically;
      _inputHorizontallyFlipped = flipHorizontally;
      _imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformationOptions.rotationAngle);

      var waitForEndOfFrame = new WaitForEndOfFrame();
      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);
      _syncResult = HandLandmarkerResult.Alloc(settings.numHands);

      while (true)
      {
        if (isPaused)
        {
          yield return new WaitWhile(() => isPaused);
        }

        if (!_texturePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return waitForEndOfFrame;
          continue;
        }

        Image image = null;
        switch (settings.imageReadMode)
        {
        case ImageReadMode.CPU:
            yield return waitForEndOfFrame;
            textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            yield return waitUntilReqDone;
            if (req.hasError)
            {
              Debug.LogWarning("HandTrackingSource: failed to read texture from source.");
              continue;
            }
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          default:
            Debug.LogError($"HandTrackingSource: {settings.imageReadMode} is not supported in this script.");
            yield break;
        }

        ProcessImage(image);
      }
    }

    private HandLandmarkerOptions BuildOptions(HandLandmarkerOptions.ResultCallback callback)
    {
      return new HandLandmarkerOptions(
        new Tasks.Core.BaseOptions(settings.inferenceDelegate, modelAssetPath: settings.modelAsset),
        runningMode: settings.runningMode,
        numHands: settings.numHands,
        minHandDetectionConfidence: settings.minHandDetectionConfidence,
        minHandPresenceConfidence: settings.minHandPresenceConfidence,
        minTrackingConfidence: settings.minTrackingConfidence,
        resultCallback: callback
      );
    }

    private void ProcessImage(Image image)
    {
      var timestamp = GetCurrentTimestampMillisec();

      switch (settings.runningMode)
      {
        case Tasks.Vision.Core.RunningMode.IMAGE:
          if (_handLandmarker.TryDetect(image, _imageProcessingOptions, ref _syncResult))
          {
            PublishResult(_syncResult, timestamp);
          }
          else
          {
            PublishNoResult(timestamp);
          }
          break;

        case Tasks.Vision.Core.RunningMode.VIDEO:
          if (_handLandmarker.TryDetectForVideo(image, timestamp, _imageProcessingOptions, ref _syncResult))
          {
            PublishResult(_syncResult, timestamp);
          }
          else
          {
            PublishNoResult(timestamp);
          }
          break;

        case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
          _handLandmarker.DetectAsync(image, timestamp, _imageProcessingOptions);
          break;
      }
    }

    private void OnLiveStreamResult(HandLandmarkerResult result, Image image, long timestamp)
    {
      PublishResult(result, timestamp);
    }

    private void PublishResult(HandLandmarkerResult result, long timestamp)
    {
      lock (_frameLock)
      {
        if (result.handLandmarks == null || result.handLandmarks.Count == 0)
        {
          PublishNoResultLocked(timestamp);
          return;
        }

        var firstHand = result.handLandmarks[0].landmarks;
        _latestFrame.tracked = true;
        _latestFrame.timestampMillisec = timestamp;
        _latestFrame.EnsureCapacity(firstHand.Count);
        for (var i = 0; i < firstHand.Count; i++)
        {
          var landmark = firstHand[i];
          _latestFrame.landmarks[i] = new Vector3(landmark.x, landmark.y, landmark.z);
        }

        _latestFrame.isRight = DetermineHandedness(result.handedness, out var score);
        _latestFrame.handednessScore = score;

        _frameDirty = true;
      }
    }

    private void PublishNoResult(long timestamp)
    {
      lock (_frameLock)
      {
        PublishNoResultLocked(timestamp);
      }
    }

    private void PublishNoResultLocked(long timestamp)
    {
      _latestFrame.tracked = false;
      _latestFrame.timestampMillisec = timestamp;
      _latestFrame.handednessScore = 0f;
      _latestFrame.isRight = true;
      _latestFrame.EnsureCapacity(0);
      _frameDirty = true;
    }

    private bool DetermineHandedness(List<Classifications> handedness, out float score)
    {
      var isRight = InferHandedness(handedness, out score);
      if (_inputHorizontallyFlipped)
      {
        isRight = !isRight;
      }
      return isRight;
    }

    private static bool InferHandedness(List<Classifications> handedness, out float score)
    {
      score = 0f;
      if (handedness == null || handedness.Count == 0)
      {
        return true;
      }

      var categories = handedness[0].categories;
      if (categories == null || categories.Count == 0)
      {
        return true;
      }

      var category = categories[0];
      score = category.score;
      return !string.IsNullOrEmpty(category.categoryName) && category.categoryName.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void TearDown()
    {
      _texturePool?.Dispose();
      _texturePool = null;

      _handLandmarker?.Close();
      _handLandmarker = null;

      var imageSource = ImageSourceProvider.ImageSource;
      imageSource?.Stop();
    }
  }
}
