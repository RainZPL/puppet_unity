using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Mediapipe.Unity.Sample;

namespace HandControl
{
  public class HandTrackingPreviewUI : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource handSource;
    [SerializeField] private RawImage screen;
    [SerializeField] private TMP_Text infoText;
    [SerializeField] private bool flipX = true;

    private bool lastTracked;
    private bool lastRight;
    private float lastScore;

    private void Awake()
    {
      if (screen != null)
      {
        var scale = screen.rectTransform.localScale;
        scale.x = flipX ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
        screen.rectTransform.localScale = scale;
      }
    }

    private void OnEnable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame += RememberHand;
      }
    }

    private void OnDisable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame -= RememberHand;
      }
    }

    private void Update()
    {
      ShowCameraTexture();
      ShowStatusText();
    }

    private void ShowCameraTexture()
    {
      if (screen == null)
      {
        return;
      }

      var cam = ImageSourceProvider.ImageSource;
      if (cam == null || !cam.isPrepared)
      {
        return;
      }

      var tex = cam.GetCurrentTexture();
      if (tex != null && screen.texture != tex)
      {
        screen.texture = tex;
      }
    }

    private void ShowStatusText()
    {
      if (infoText == null)
      {
        return;
      }

      if (!lastTracked)
      {
        infoText.text = "Hand: (none)";
        infoText.color = Color.red;
      }
      else
      {
        infoText.text = $"Hand: {(lastRight ? "Right" : "Left")} ({lastScore:F2})";
        infoText.color = Color.green;
      }
    }

    private void RememberHand(HandTrackingSource.HandFrameData frame)
    {
      if (frame == null)
      {
        lastTracked = false;
        return;
      }

      lastTracked = frame.tracked;
      if (lastTracked)
      {
        lastRight = frame.isRight;
        lastScore = frame.handednessScore;
      }
    }
  }
}
