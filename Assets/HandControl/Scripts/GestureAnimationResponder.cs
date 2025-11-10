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
                controller.OnGestureResult += OnGestureResult; // 新增：监听所有手势结果
                controller.OnAllGesturesCompleted += OnAllGesturesCompleted; // 新增：监听完成事件
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
            ClearAllParameters();
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
                    if (!string.IsNullOrEmpty(item.boolParameter))
                    {
                        // 先清除所有参数，然后设置当前参数
                        ClearAllParameters();
                        targetAnimator.SetBool(item.boolParameter, true);

                        if (!activeParams.Contains(item.boolParameter))
                        {
                            activeParams.Add(item.boolParameter);
                        }

                        Debug.Log($"Set animator parameter: {item.boolParameter} = true");
                    }
                    break;
                }
            }
        }

        // 新增：处理每个手势的结果
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
            }
        }

        // 新增：所有手势完成时的处理
        private void OnAllGesturesCompleted()
        {
            Debug.Log("All gestures completed!");

            // 可以在这里添加完成后的逻辑
            // 例如：播放完成动画、显示结果等

            // 可选：清除所有动画参数
            // ClearAllParameters();
        }

        // 清除所有动画参数
        private void ClearAllParameters()
        {
            if (targetAnimator == null) return;

            for (int i = 0; i < activeParams.Count; i++)
            {
                targetAnimator.SetBool(activeParams[i], false);
            }

            activeParams.Clear();
        }

        // 公共方法：手动触发手势动画
        public void TriggerGestureAnimation(string gestureName)
        {
            OnGestureHit(gestureName);
        }

        // 公共方法：清除动画状态
        public void ResetAnimator()
        {
            ClearAllParameters();
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