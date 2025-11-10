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
            public string triggerParameter; // 改为trigger参数名
        }

        public GestureValidationControllerOnnx controller;
        public Animator targetAnimator;
        public List<Item> mapping = new List<Item>();

        private readonly List<string> activeTriggers = new List<string>();

        private void OnEnable()
        {
            if (controller != null)
            {
                controller.OnGestureMatched += OnGestureHit;
                controller.OnNewSessionStarted += OnNewSession;
                controller.OnGestureResult += OnGestureResult;
                controller.OnAllGesturesCompleted += OnAllGesturesCompleted;
            }
        }

        private void OnDisable()
        {
            if (controller != null)
            {
                controller.OnGestureMatched -= OnGestureHit;
                controller.OnNewSessionStarted -= OnNewSession;
                controller.OnGestureResult -= OnGestureResult;
                controller.OnAllGesturesCompleted -= OnAllGesturesCompleted;
            }
        }

        private void OnNewSession()
        {
            ClearAllTriggers();
            Debug.Log("New gesture session started");
        }

        private void OnGestureHit(string label)
        {
            if (targetAnimator == null || string.IsNullOrEmpty(label))
            {
                return;
            }

            Debug.Log($"Gesture matched: {label}");

            for (int i = 0; i < mapping.Count; i++)
            {
                Item item = mapping[i];
                if (item == null || string.IsNullOrEmpty(item.gestureName))
                {
                    continue;
                }

                if (string.Equals(item.gestureName, label, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(item.triggerParameter))
                    {
                        // 触发对应的trigger
                        targetAnimator.SetTrigger(item.triggerParameter);

                        if (!activeTriggers.Contains(item.triggerParameter))
                        {
                            activeTriggers.Add(item.triggerParameter);
                        }

                        Debug.Log($"Set animator trigger: {item.triggerParameter}");
                    }
                    break;
                }
            }
        }

        // 处理每个手势的结果
        private void OnGestureResult(int index, bool success, string label)
        {
            Debug.Log($"Gesture result: {label} - {(success ? "SUCCESS" : "FAILED/TIMEOUT")}");

            if (success)
            {
                // 成功的手势已经在OnGestureHit中处理了
            }
            else
            {
                // 失败或超时的手势可以在这里处理
                // 例如：播放失败动画、显示提示等
                Debug.Log($"Gesture failed or timeout: {label}");

                // 可以触发一个失败动画的trigger
                // targetAnimator.SetTrigger("GestureFailed");
            }
        }

        // 所有手势完成时的处理
        private void OnAllGesturesCompleted()
        {
            Debug.Log("All gestures completed!");

            // 可以在这里添加完成后的逻辑
            // 例如：播放完成动画、显示结果等
            // targetAnimator.SetTrigger("AllGesturesCompleted");
        }

        // 清除所有trigger（如果需要）
        private void ClearAllTriggers()
        {
            if (targetAnimator == null) return;

            // 注意：Trigger不需要手动清除，它们会自动重置
            // 这个方法主要用于记录哪些trigger被触发过
            activeTriggers.Clear();

            // 如果需要重置所有trigger状态，可以调用ResetTrigger
            foreach (var trigger in activeTriggers)
            {
                targetAnimator.ResetTrigger(trigger);
            }
        }

        // 公共方法：手动触发手势动画
        public void TriggerGestureAnimation(string gestureName)
        {
            OnGestureHit(gestureName);
        }

        // 公共方法：重置特定trigger
        public void ResetTrigger(string triggerName)
        {
            if (targetAnimator != null)
            {
                targetAnimator.ResetTrigger(triggerName);
            }
        }

        // 公共方法：重置所有trigger
        public void ResetAllTriggers()
        {
            if (targetAnimator == null) return;

            foreach (var trigger in activeTriggers)
            {
                targetAnimator.ResetTrigger(trigger);
            }
            activeTriggers.Clear();
        }

        // 公共方法：检查手势是否在映射中
        public bool HasMappingForGesture(string gestureName)
        {
            foreach (var item in mapping)
            {
                if (string.Equals(item.gestureName, gestureName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}