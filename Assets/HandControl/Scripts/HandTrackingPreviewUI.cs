using UnityEngine;
using UnityEngine.UI;

using Mediapipe.Unity.Sample;

namespace HandControl
{
  public class HandTrackingPreviewUI : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private RawImage previewImage;
    [SerializeField] private Text statusText;
    [SerializeField] private bool mirrorPreview = true;

    private bool _handTracked;
    private bool _isRight;
    private float _handScore;

    private void Awake()
    {
      if (previewImage != null)
      {
        var scale = previewImage.rectTransform.localScale;
        scale.x = mirrorPreview ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
        previewImage.rectTransform.localScale = scale;
      }
    }

    private void OnEnable()
    {
      if (source != null)
      {
        source.OnHandFrame += HandleHandFrame;
      }
    }

    private void OnDisable()
    {
      if (source != null)
      {
        source.OnHandFrame -= HandleHandFrame;
      }
    }

    private void Update()
    {
      UpdatePreviewTexture();
      UpdateStatusLabel();
    }

    private void UpdatePreviewTexture()
    {
      if (previewImage == null)
      {
        return;
      }

      var imageSource = ImageSourceProvider.ImageSource;
      if (imageSource == null || !imageSource.isPrepared)
      {
        return;
      }

      var texture = imageSource.GetCurrentTexture();
      if (texture != null && previewImage.texture != texture)
      {
        previewImage.texture = texture;
      }
    }

    private void UpdateStatusLabel()
    {
      if (statusText == null)
      {
        return;
      }

      if (_handTracked)
      {
        statusText.text = $"Hand: {(_isRight ? "Right" : "Left")} ({_handScore:F2})";
        statusText.color = Color.green;
      }
      else
      {
        statusText.text = "Hand: None";
        statusText.color = Color.red;
      }
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      _handTracked = frame != null && frame.tracked;
      if (_handTracked)
      {
        _isRight = frame.isRight;
        _handScore = frame.handednessScore;
      }
    }
  }
}
