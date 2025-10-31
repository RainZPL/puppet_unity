using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HandControl
{
  public class HandTrackingOverlay : MonoBehaviour
  {
    [SerializeField] private HandTrackingSource handSource;
    [SerializeField] private RectTransform drawArea;
    [SerializeField] private Color dotColor = Color.cyan;
    [SerializeField] private float dotSize = 6f;
    [SerializeField] private bool flipX = true;
    [SerializeField] private bool flipY = false;

    private readonly List<Image> dotImages = new();
    private HandTrackingSource.HandFrameData latestFrame;

    private void Awake()
    {
      MakeDots(21);
      TurnDots(false);
    }

    private void OnEnable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame += GotFrame;
      }
    }

    private void OnDisable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame -= GotFrame;
      }
      TurnDots(false);
    }

    private void GotFrame(HandTrackingSource.HandFrameData frame)
    {
      latestFrame = frame;
      DrawDots();
    }

    private void DrawDots()
    {
      if (drawArea == null)
      {
        return;
      }

      if (latestFrame == null || !latestFrame.tracked || latestFrame.landmarks == null)
      {
        TurnDots(false);
        return;
      }

      MakeDots(latestFrame.landmarks.Length);
      var rect = drawArea.rect;

      for (var i = 0; i < dotImages.Count; i++)
      {
        var image = dotImages[i];
        if (i >= latestFrame.landmarks.Length)
        {
          image.enabled = false;
          continue;
        }

        var lm = latestFrame.landmarks[i];
        var x = flipX ? 1f - lm.x : lm.x;
        var y = flipY ? 1f - lm.y : lm.y;

        var pos = new Vector2(
          (x - 0.5f) * rect.width,
          (0.5f - y) * rect.height
        );

        var rt = image.rectTransform;
        rt.anchoredPosition = pos;
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, dotSize);
        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, dotSize);
        image.enabled = true;
      }
    }

    private void MakeDots(int count)
    {
      if (drawArea == null)
      {
        return;
      }

      while (dotImages.Count < count)
      {
        var go = new GameObject($"dot_{dotImages.Count}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(drawArea, false);
        var img = go.GetComponent<Image>();
        img.color = dotColor;
        img.raycastTarget = false;
        dotImages.Add(img);
      }
    }

    private void TurnDots(bool value)
    {
      foreach (var img in dotImages)
      {
        if (img != null)
        {
          img.enabled = value;
        }
      }
    }
  }
}
