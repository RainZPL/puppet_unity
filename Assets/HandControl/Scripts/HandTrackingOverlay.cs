using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HandControl
{
  [DefaultExecutionOrder(-5)]
  public class HandTrackingOverlay : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private RectTransform previewRect;
    [SerializeField] private Color landmarkColor = Color.cyan;
    [SerializeField] private float landmarkSize = 6f;
    [SerializeField] private bool mirrorX = true;
    [SerializeField] private bool mirrorY = false;

    private readonly List<Image> _landmarkImages = new();
    private HandTrackingSource.HandFrameData _frameCache;

    private void Awake()
    {
      EnsureLandmarkImages(21);
    }

    private void OnEnable()
    {
      if (source != null)
      {
        source.OnHandFrame += OnHandFrame;
      }
    }

    private void OnDisable()
    {
      if (source != null)
      {
        source.OnHandFrame -= OnHandFrame;
      }
      SetActive(false);
    }

    private void OnHandFrame(HandTrackingSource.HandFrameData frame)
    {
      _frameCache = frame;
      UpdateOverlay();
    }

    private void UpdateOverlay()
    {
      if (previewRect == null)
      {
        return;
      }

      if (_frameCache == null || !_frameCache.tracked || _frameCache.landmarks == null || _frameCache.landmarks.Length == 0)
      {
        SetActive(false);
        return;
      }

      EnsureLandmarkImages(_frameCache.landmarks.Length);
      var rect = previewRect.rect;

      for (var i = 0; i < _landmarkImages.Count; i++)
      {
        var image = _landmarkImages[i];
        if (i >= _frameCache.landmarks.Length)
        {
          image.enabled = false;
          continue;
        }

        var landmark = _frameCache.landmarks[i];
        var normalizedX = landmark.x;
        var normalizedY = landmark.y;

        if (mirrorX)
        {
          normalizedX = 1f - normalizedX;
        }

        if (mirrorY)
        {
          normalizedY = 1f - normalizedY;
        }

        var anchoredPos = new Vector2(
          (normalizedX - 0.5f) * rect.width,
          (0.5f - normalizedY) * rect.height
        );

        var rt = image.rectTransform;
        rt.anchoredPosition = anchoredPos;
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, landmarkSize);
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, landmarkSize);
        image.enabled = true;
      }
    }

    private void SetActive(bool value)
    {
      foreach (var image in _landmarkImages)
      {
        if (image != null)
        {
          image.enabled = value;
        }
      }
    }

    private void EnsureLandmarkImages(int count)
    {
      if (previewRect == null)
      {
        return;
      }

      while (_landmarkImages.Count < count)
      {
        var go = new GameObject($"Landmark_{_landmarkImages.Count}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(previewRect, false);
        var image = go.GetComponent<Image>();
        image.color = landmarkColor;
        image.raycastTarget = false;
        _landmarkImages.Add(image);
      }
    }
  }
}
