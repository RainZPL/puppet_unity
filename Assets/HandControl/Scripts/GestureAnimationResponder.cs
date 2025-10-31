using System;
using System.Collections.Generic;
using UnityEngine;

namespace HandControl
{
  public class GestureAnimationResponder : MonoBehaviour
  {
    [System.Serializable]
    public class Item
    {
      public string gestureName;
      public string boolParameter;
    }

    public GestureValidationControllerOnnx controller;
    public Animator targetAnimator;
    public List<Item> mapping = new List<Item>();

    private readonly List<string> activeParams = new List<string>();

    private void OnEnable()
    {
      if (controller != null)
      {
        controller.OnGestureMatched += OnGestureHit;
        controller.OnNewSessionStarted += OnNewSession;
      }
    }

    private void OnDisable()
    {
      if (controller != null)
      {
        controller.OnGestureMatched -= OnGestureHit;
        controller.OnNewSessionStarted -= OnNewSession;
      }
    }

    private void OnNewSession()
    {
      if (targetAnimator == null)
      {
        activeParams.Clear();
        return;
      }

      for (int i = 0; i < activeParams.Count; i++)
      {
        targetAnimator.SetBool(activeParams[i], false);
      }

      activeParams.Clear();
    }

    private void OnGestureHit(string label)
    {
      if (targetAnimator == null || string.IsNullOrEmpty(label))
      {
        return;
      }

      for (int i = 0; i < mapping.Count; i++)
      {
        Item item = mapping[i];
        if (item == null || string.IsNullOrEmpty(item.gestureName))
        {
          continue;
        }

        if (string.Equals(item.gestureName, label, System.StringComparison.OrdinalIgnoreCase))
        {
          if (!string.IsNullOrEmpty(item.boolParameter))
          {
            targetAnimator.SetBool(item.boolParameter, true);
            if (!activeParams.Contains(item.boolParameter))
            {
              activeParams.Add(item.boolParameter);
            }
          }
          break;
        }
      }
    }
  }
}
