using System;
using System.Collections;
using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using Mediapipe.Unity.Sample;
using UnityEngine;
using Tasks = Mediapipe.Tasks;

namespace HandControl
{
  [DefaultExecutionOrder(-20)]
  public class HandTrackingSource : BaseRunner
  {
    [Serializable]
    public class SimpleSettings
    {
      [Range(1, 2)] public int handCount = 1;
      [Range(0f, 1f)] public float detectScore = 0.5f;
      [Range(0f, 1f)] public float presenceScore = 0.5f;
      [Range(0f, 1f)] public float followScore = 0.5f;
      [Tooltip("Relative path under StreamingAssets")] public string modelFile = "hand_landmarker.bytes";
      [Tooltip("How many frames to keep around for CPU readback")] public int extraTextures = 4;
    }

    [Serializable]
    public class HandFrameData
    {
      public bool tracked;
      public bool isRight;
      public float handednessScore;
      public long timestampMillisec;
      public Vector3[] landmarks = Array.Empty<Vector3>();

      public void MakeRoom(int count)
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
        MakeRoom(other.landmarks?.Length ?? 0);
        for (var i = 0; i < landmarks.Length; i++)
        {
          landmarks[i] = other.landmarks[i];
        }
      }
    }

    [SerializeField] private SimpleSettings settings = new SimpleSettings();

    public event Action<HandFrameData> OnHandFrame;

    private TextureFramePool framePool;
    private HandLandmarker handDetector;
    private Coroutine loopRoutine;
    private Tasks.Vision.Core.ImageProcessingOptions imageOptions;
    private bool sourceWasFlipped;

    private readonly object frameLock = new();
    private readonly HandFrameData lastFrame = new();
    private readonly HandFrameData eventFrame = new();
    private bool frameReady;

    public override void Play()
    {
      base.Play();
      if (loopRoutine == null)
      {
        loopRoutine = StartCoroutine(RunCoroutine());
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
      if (loopRoutine != null)
      {
        StopCoroutine(loopRoutine);
        loopRoutine = null;
      }
      TearDown();
    }

    private void Update()
    {
      if (!frameReady)
      {
        return;
      }

      HandFrameData snapshot = null;
      lock (frameLock)
      {
        if (!frameReady)
        {
          return;
        }

        eventFrame.CopyFrom(lastFrame);
        frameReady = false;
        snapshot = eventFrame;
      }

      OnHandFrame?.Invoke(snapshot);
    }

    private IEnumerator RunCoroutine()
    {
      yield return AssetLoader.PrepareAssetAsync(settings.modelFile);

      var options = BuildOptions(OnLiveStreamResult);
      handDetector = HandLandmarker.CreateFromOptions(options, GpuManager.GpuResources);

      var imageSource = ImageSourceProvider.ImageSource;
      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("HandTrackingSource: camera refused to start.");
        yield break;
      }

      framePool = new TextureFramePool(
        imageSource.textureWidth,
        imageSource.textureHeight,
        TextureFormat.RGBA32,
        Mathf.Max(settings.extraTextures, 2)
      );

      var transformOpts = imageSource.GetTransformationOptions();
      sourceWasFlipped = transformOpts.flipHorizontally;
      imageOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformOpts.rotationAngle);

      var waitEOF = new WaitForEndOfFrame();

      while (true)
      {
        if (isPaused)
        {
          yield return new WaitWhile(() => isPaused);
        }

        if (!framePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return waitEOF;
          continue;
        }

        yield return waitEOF;
        textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), transformOpts.flipHorizontally, transformOpts.flipVertically);
        var cpuImage = textureFrame.BuildCPUImage();
        textureFrame.Release();

        PushImage(cpuImage);
      }
    }

    private HandLandmarkerOptions BuildOptions(HandLandmarkerOptions.ResultCallback callback)
    {
      return new HandLandmarkerOptions(
        new Tasks.Core.BaseOptions(Tasks.Core.BaseOptions.Delegate.CPU, modelAssetPath: settings.modelFile),
        runningMode: Tasks.Vision.Core.RunningMode.LIVE_STREAM,
        numHands: settings.handCount,
        minHandDetectionConfidence: settings.detectScore,
        minHandPresenceConfidence: settings.presenceScore,
        minTrackingConfidence: settings.followScore,
        resultCallback: callback
      );
    }

    private void PushImage(Image image)
    {
      var timestamp = GetCurrentTimestampMillisec();
      handDetector.DetectAsync(image, timestamp, imageOptions);
    }

    private void OnLiveStreamResult(HandLandmarkerResult result, Image image, long timestamp)
    {
      PublishResult(result, timestamp);
    }

    private void PublishResult(HandLandmarkerResult result, long timestamp)
    {
      lock (frameLock)
      {
        if (result.handLandmarks == null || result.handLandmarks.Count == 0)
        {
          PublishNoResultLocked(timestamp);
          return;
        }

        var firstHand = result.handLandmarks[0].landmarks;
        lastFrame.tracked = true;
        lastFrame.timestampMillisec = timestamp;
        lastFrame.MakeRoom(firstHand.Count);
        for (var i = 0; i < firstHand.Count; i++)
        {
          var landmark = firstHand[i];
          lastFrame.landmarks[i] = new Vector3(landmark.x, landmark.y, landmark.z);
        }

        lastFrame.isRight = CalculateHandedness(result.handedness, out var score);
        lastFrame.handednessScore = score;

        frameReady = true;
      }
    }

    private void PublishNoResult(long timestamp)
    {
      lock (frameLock)
      {
        PublishNoResultLocked(timestamp);
      }
    }

    private void PublishNoResultLocked(long timestamp)
    {
      lastFrame.tracked = false;
      lastFrame.timestampMillisec = timestamp;
      lastFrame.handednessScore = 0f;
      lastFrame.isRight = true;
      lastFrame.MakeRoom(0);
      frameReady = true;
    }

    private bool CalculateHandedness(List<Classifications> handedness, out float score)
    {
      var isRight = InferHandedness(handedness, out score);
      if (sourceWasFlipped)
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
      return !string.IsNullOrEmpty(category.categoryName) &&
             category.categoryName.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void TearDown()
    {
      framePool?.Dispose();
      framePool = null;

      handDetector?.Close();
      handDetector = null;

      var imageSource = ImageSourceProvider.ImageSource;
      imageSource?.Stop();
    }
  }
}
