using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.InferenceEngine;
using UnityEngine;
using TMPro; // 添加TextMeshPro命名空间
using TensorFloat = Unity.InferenceEngine.Tensor<float>;

namespace HandControl
{
    public class GestureValidationControllerOnnx : MonoBehaviour
    {
        [Serializable]
        public class GestureInfo
        {
            public string label;
        }

        public HandTrackingSource handSource;
        public TextMeshProUGUI progressLabel; // 改为TextMeshProUGUI
        public TextMeshProUGUI resultLabel;    // 改为TextMeshProUGUI
        public List<GestureInfo> gestures = new List<GestureInfo>();
        public ModelAsset modelAsset;
        public TextAsset labelMapJson;
        public float captureSeconds = 5f;
        public int minimumFrames = 60;
        public float pretendFps = 30f;
        public bool allowEmptyFrames = true;
        public float passProbability = 0.75f;
        public float passConfidence = 0.5f;
        public int modelMaxLength = 100;
        public int featureCount = 20;
        public float timeoutSeconds = 15f; // 超时时间
        public bool autoStartOnEnable = true; // 新增：启用时自动开始
        public float DeltaTime;

        public Action<string> OnGestureMatched;
        public Action OnNewSessionStarted;
        public Action<int, bool, string> OnGestureResult; // 参数：动作索引, 是否成功, 动作标签
        public Action OnAllGesturesCompleted; // 所有手势完成回调

        private readonly List<float[]> frameBuffer = new List<float[]>();
        private readonly List<long> timeBuffer = new List<long>();
        private readonly Vector3[] rawPoints = new Vector3[21];
        private readonly Vector3[] normPoints = new Vector3[21];

        private Model model;
        private Worker worker;
        private readonly List<string> labels = new List<string>();
        private readonly Dictionary<string, int> labelLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 自动触发相关变量
        public int currentGestureIndex = -1;
        public bool isTesting = false;
        public bool isTimeout = false;
        public bool gesturePassed = false;
        private float gestureStartTime = 0f;

        private string activeLabel;
        private bool collecting;
        private string progressText = "Idle";
        private string resultText = "Result: --";

        private void Awake()
        {
            if (modelAsset == null)
            {
                Debug.LogError("GestureValidationControllerOnnx: missing model asset");
                enabled = false;
                return;
            }

            model = ModelLoader.Load(modelAsset);
            CreateWorker();
            LoadLabels();
            RefreshUi();
        }

        private void OnEnable()
        {
            if (handSource != null)
            {
                handSource.OnHandFrame += HandleHandFrame;
            }

            if (autoStartOnEnable && !isTesting && gestures.Count > 0)
            {
                StartAutoTesting();
            }
        }

        private void OnDisable()
        {
            if (handSource != null)
            {
                handSource.OnHandFrame -= HandleHandFrame;
            }
            StopTesting();
        }

        private void OnDestroy()
        {
            worker?.Dispose();
            worker = null;
            StopTesting();
        }

        private void Start()
        {
            if (autoStartOnEnable && !isTesting && gestures.Count > 0)
            {
                StartAutoTesting();
            }
        }

        private void Update()
        {
            RefreshUi();

            // 自动触发逻辑
            if (isTesting && collecting)
            {
                float currentTime = Time.time;
                DeltaTime = currentTime - gestureStartTime;

                // 检查超时
                if (!gesturePassed && currentTime - gestureStartTime > timeoutSeconds)
                {
                    isTimeout = true;
                    OnGestureTimeout();
                    return;
                }
            }
            else if (isTesting && !collecting && (gesturePassed || isTimeout))
            {
                // 手势完成或超时后立即开始下一个手势（无等待时间）
                StartNextGesture();
            }
        }

        private void CreateWorker()
        {
            try
            {
                worker = new Worker(model, BackendType.GPUCompute);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("GestureValidationControllerOnnx: GPU worker failed, using CPU. " + ex.Message);
                worker?.Dispose();
                worker = new Worker(model, BackendType.CPU);
            }
        }

        private void RefreshUi()
        {
            if (progressLabel != null)
            {
                progressText = GetProgressText();
                progressLabel.text = progressText;
            }

            if (resultLabel != null)
            {
                resultLabel.text = resultText;
            }
        }

        private string GetProgressText()
        {
            if (!isTesting) return "Idle";
            if (currentGestureIndex < 0 || currentGestureIndex >= gestures.Count) return "Testing...";

            var gesture = gestures[currentGestureIndex];
            float elapsed = Time.time - gestureStartTime;
            float remaining = Mathf.Max(0f, timeoutSeconds - elapsed);

            if (gesturePassed) return $"Gesture {gesture.label} passed! Starting next...";
            if (isTimeout) return $"Gesture {gesture.label} timeout! Starting next...";

            return $"Testing {gesture.label}: {remaining:F1}s remaining";
        }

        // 开始自动测试序列
        public void StartAutoTesting()
        {
            if (isTesting)
            {
                Debug.LogWarning("Auto testing is already running");
                return;
            }

            if (gestures.Count == 0)
            {
                Debug.LogError("No gestures configured for testing");
                return;
            }

            if (worker == null)
            {
                Debug.LogError("Worker is not initialized");
                return;
            }

            isTesting = true;
            currentGestureIndex = -1;
            gesturePassed = false;
            isTimeout = false;

            Debug.Log($"Starting auto testing with {gestures.Count} gestures");
            StartNextGesture();
        }

        // 停止自动测试
        public void StopTesting()
        {
            if (isTesting)
            {
                Debug.Log("Auto testing stopped");
            }

            isTesting = false;
            currentGestureIndex = -1;
            gesturePassed = false;
            isTimeout = false;
            StopCollecting("Stopped", true);
        }

        // 重新开始测试
        public void RestartTesting()
        {
            StopTesting();
            StartAutoTesting();
        }

        // 开始下一个手势测试
        private void StartNextGesture()
        {
            // 重置所有状态标志
            gesturePassed = false;
            isTimeout = false;

            currentGestureIndex++;

            // 检查是否所有手势都测试完成
            if (currentGestureIndex >= gestures.Count)
            {
                Debug.Log("All gestures tested!");
                isTesting = false;
                OnAllGesturesCompleted?.Invoke();
                return;
            }

            var nextGesture = gestures[currentGestureIndex];
            if (string.IsNullOrWhiteSpace(nextGesture.label))
            {
                Debug.LogWarning($"Skipping gesture at index {currentGestureIndex} due to empty label");
                StartNextGesture(); // 跳过无效手势
                return;
            }

            // 设置开始时间
            gestureStartTime = Time.time;

            // 开始收集手势数据
            StartCollecting(nextGesture);
        }

        private void StartCollecting(GestureInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.label))
            {
                Debug.LogWarning("GestureValidationControllerOnnx: gesture missing label");
                return;
            }

            if (handSource == null)
            {
                Debug.LogWarning("GestureValidationControllerOnnx: no hand source set");
                return;
            }

            collecting = true;
            activeLabel = info.label.Trim();

            OnNewSessionStarted?.Invoke();
            frameBuffer.Clear();
            timeBuffer.Clear();

            progressText = $"Collecting {activeLabel}...";
            resultText = "Waiting...";

            Debug.Log($"Started collecting gesture: {activeLabel}");
        }

        private void StopCollecting(string message, bool resetResult)
        {
            if (!collecting)
            {
                progressText = message;
                if (resetResult)
                {
                    resultText = "Result: --";
                }
                return;
            }

            collecting = false;
            progressText = message;

            frameBuffer.Clear();
            timeBuffer.Clear();

            if (resetResult)
            {
                resultText = "Result: --";
            }

            activeLabel = null;
            Debug.Log($"Stopped collecting: {message}");
        }

        private void HandleHandFrame(HandTrackingSource.HandFrameData data)
        {
            if (!collecting || worker == null) return;

            bool tracked = data != null && data.tracked && data.landmarks != null && data.landmarks.Length >= 21;
            if (!tracked && !allowEmptyFrames) return;

            SaveFrame(data, tracked);
            int count = frameBuffer.Count;
            if (count == 0) return;

            float seconds = GetWindowSeconds();
            int needFrames = Mathf.Max(minimumFrames, Mathf.CeilToInt(captureSeconds * Mathf.Max(1f, pretendFps)));

            if (count >= needFrames && seconds >= captureSeconds)
            {
                RunModel();
                frameBuffer.Clear();
                timeBuffer.Clear();
                progressText = "Window sent";
            }
            else
            {
                progressText = $"Collecting {activeLabel}: {seconds:F1}s / {captureSeconds:F1}s";
            }
        }

        private void SaveFrame(HandTrackingSource.HandFrameData data, bool tracked)
        {
            float[] row = new float[63];
            if (tracked && data != null && data.landmarks != null)
            {
                for (int i = 0; i < 21 && i < data.landmarks.Length; i++)
                {
                    var point = data.landmarks[i];
                    int id = i * 3;
                    row[id] = point.x;
                    row[id + 1] = point.y;
                    row[id + 2] = point.z;
                }
            }

            frameBuffer.Add(row);
            timeBuffer.Add(data != null ? data.timestampMillisec : 0);
        }

        private float GetWindowSeconds()
        {
            if (timeBuffer.Count >= 2)
            {
                long first = timeBuffer[0];
                long last = timeBuffer[timeBuffer.Count - 1];
                if (last > first)
                {
                    return (last - first) / 1000f;
                }
            }

            float frames = frameBuffer.Count;
            float fps = Mathf.Max(1f, pretendFps);
            return frames / fps;
        }

        private void RunModel()
        {
            if (worker == null || frameBuffer.Count == 0) return;

            List<float[]> features = BuildFeatureList(frameBuffer);
            if (features.Count == 0) return;

            int usableFrames = Mathf.Min(features.Count, modelMaxLength);
            int startIndex = Mathf.Max(0, features.Count - modelMaxLength);

            using (var input = new TensorFloat(new TensorShape(1, modelMaxLength, featureCount)))
            using (var mask = new TensorFloat(new TensorShape(1, modelMaxLength)))
            {
                for (int i = 0; i < usableFrames; i++)
                {
                    float[] feat = features[startIndex + i];
                    for (int j = 0; j < featureCount; j++)
                    {
                        float value = j < feat.Length ? feat[j] : 0f;
                        input[0, i, j] = value;
                    }
                    mask[0, i] = 1f;
                }

                try
                {
                    worker.SetInput("input_seq", input);
                    worker.SetInput("input_mask", mask);
                    worker.Schedule();

                    float[] logits = ReadTensor(worker.PeekOutput("logits"));
                    if (logits == null || logits.Length == 0)
                    {
                        Debug.LogWarning("GestureValidationControllerOnnx: logits missing");
                        return;
                    }

                    float confidence = 0f;
                    float[] confTensor = ReadTensor(worker.PeekOutput("confidence"));
                    if (confTensor != null && confTensor.Length > 0)
                    {
                        confidence = confTensor[0];
                    }

                    float[] probs = ComputeSoftmax(logits);
                    int bestIndex = 0;
                    float bestProb = 0f;

                    for (int i = 0; i < probs.Length; i++)
                    {
                        if (probs[i] > bestProb)
                        {
                            bestProb = probs[i];
                            bestIndex = i;
                        }
                    }

                    float targetProb = bestProb;
                    if (!string.IsNullOrEmpty(activeLabel) && labelLookup.TryGetValue(activeLabel, out int wanted) && wanted < probs.Length)
                    {
                        targetProb = probs[wanted];
                    }

                    string probText = targetProb.ToString("F2", CultureInfo.InvariantCulture);

                    if (!string.IsNullOrEmpty(activeLabel))
                    {
                        resultText = $"Target {activeLabel} prob = {probText}";

                        if (targetProb >= passProbability && confidence >= passConfidence)
                        {
                            resultText = $"Target {activeLabel} prob = {probText} (passed)";
                            gesturePassed = true;
                            OnGestureMatched?.Invoke(activeLabel);

                            // 触发结果回调
                            OnGestureResult?.Invoke(currentGestureIndex, true, activeLabel);

                            // 立即停止收集，准备开始下一个手势
                            StopCollecting("Finished", false);

                            Debug.Log($"Gesture {activeLabel} PASSED with probability {probText}");
                        }
                        else
                        {
                            Debug.Log($"Gesture {activeLabel} probability: {probText} (needs {passProbability})");
                        }
                    }
                    else
                    {
                        string bestName = bestIndex >= 0 && bestIndex < labels.Count ? labels[bestIndex] : "Unknown";
                        resultText = $"Prediction: {bestName} prob = {bestProb:F2}";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("GestureValidationControllerOnnx: inference failed - " + ex.Message);
                }
            }
        }

        // 手势超时处理
        private void OnGestureTimeout()
        {
            if (currentGestureIndex >= 0 && currentGestureIndex < gestures.Count)
            {
                var gesture = gestures[currentGestureIndex];
                Debug.Log($"Gesture {gesture.label} timeout!");

                // 触发结果回调（失败）
                OnGestureResult?.Invoke(currentGestureIndex, false, gesture.label);

                // 立即停止收集，准备开始下一个手势
                StopCollecting("Timeout", true);
            }
        }

        private float[] ReadTensor(Tensor tensor)
        {
            if (tensor == null) return null;

            try
            {
                using (var cpu = tensor.ReadbackAndClone() as TensorFloat)
                {
                    if (cpu == null || cpu.count == 0) return null;

                    float[] arr = new float[cpu.count];
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = cpu[i];
                    }
                    return arr;
                }
            }
            finally
            {
                tensor.Dispose();
            }
        }

        private List<float[]> BuildFeatureList(List<float[]> frames)
        {
            List<float[]> list = new List<float[]>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                float[] features = ExtractFeatures(frames[i]);
                if (features != null)
                {
                    list.Add(features);
                }
            }

            float[] speed = new float[list.Count];
            for (int i = 1; i < list.Count; i++)
            {
                float diff = 0f;
                float[] now = list[i];
                float[] prev = list[i - 1];
                for (int j = 0; j < 5 && j < now.Length && j < prev.Length; j++)
                {
                    float delta = now[j] - prev[j];
                    diff += delta * delta;
                }
                speed[i] = Mathf.Sqrt(diff);
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Length > 0)
                {
                    list[i][list[i].Length - 1] = speed[i];
                }
            }

            return list;
        }

        private float[] ExtractFeatures(float[] frame)
        {
            if (frame == null || frame.Length < 63)
            {
                return new float[featureCount];
            }

            for (int i = 0; i < 21; i++)
            {
                int idx = i * 3;
                rawPoints[i] = new Vector3(frame[idx], frame[idx + 1], frame[idx + 2]);
            }

            NormaliseHand();
            float[] data = new float[featureCount];
            int cursor = 0;

            int[] palmIds = { 0, 5, 9, 13, 17 };
            Vector2 palmCenter = Vector2.zero;
            foreach (int id in palmIds)
            {
                palmCenter += new Vector2(normPoints[id].x, normPoints[id].y);
            }
            palmCenter /= palmIds.Length;

            int[] tipIds = { 4, 8, 12, 16, 20 };
            foreach (int tip in tipIds)
            {
                Vector2 tipPos = new Vector2(normPoints[tip].x, normPoints[tip].y);
                data[cursor++] = Vector2.Distance(tipPos, palmCenter);
            }

            int[][] chains = {
                new [] { 1, 2, 3, 4 },
                new [] { 5, 6, 7, 8 },
                new [] { 9, 10, 11, 12 },
                new [] { 13, 14, 15, 16 },
                new [] { 17, 18, 19, 20 }
            };

            foreach (int[] chain in chains)
            {
                for (int i = 0; i < chain.Length - 2; i++)
                {
                    Vector3 a = normPoints[chain[i]] - normPoints[chain[i + 1]];
                    Vector3 b = normPoints[chain[i + 2]] - normPoints[chain[i + 1]];
                    float aLen = a.magnitude;
                    float bLen = b.magnitude;

                    if (aLen < 1e-6f || bLen < 1e-6f)
                    {
                        data[cursor++] = 0f;
                    }
                    else
                    {
                        a /= aLen;
                        b /= bLen;
                        float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
                        data[cursor++] = Mathf.Acos(dot);
                    }
                }
            }

            for (int i = 0; i < tipIds.Length - 1; i++)
            {
                data[cursor++] = Vector3.Distance(normPoints[tipIds[i]], normPoints[tipIds[i + 1]]);
            }

            if (cursor < featureCount)
            {
                data[cursor] = 0f;
            }

            return data;
        }

        private void NormaliseHand()
        {
            Vector3 wrist = rawPoints[0];
            for (int i = 0; i < rawPoints.Length; i++)
            {
                Vector3 v = rawPoints[i];
                normPoints[i] = new Vector3(v.x - wrist.x, v.y - wrist.y, v.z - wrist.z);
            }

            int[] baseIds = { 5, 9, 13, 17 };
            float sum = 0f;
            foreach (int id in baseIds)
            {
                sum += Vector3.Distance(normPoints[id], normPoints[0]);
            }

            float scale = (baseIds.Length > 0 ? sum / baseIds.Length : 1f) + 1e-6f;
            for (int i = 0; i < normPoints.Length; i++)
            {
                normPoints[i] /= scale;
            }
        }

        private float[] ComputeSoftmax(IReadOnlyList<float> logits)
        {
            float[] output = new float[logits.Count];
            if (logits.Count == 0) return output;

            float max = logits[0];
            for (int i = 1; i < logits.Count; i++)
            {
                if (logits[i] > max) max = logits[i];
            }

            float total = 0f;
            for (int i = 0; i < logits.Count; i++)
            {
                float value = Mathf.Exp(logits[i] - max);
                output[i] = value;
                total += value;
            }

            if (total <= 0f) return output;

            for (int i = 0; i < output.Length; i++)
            {
                output[i] /= total;
            }

            return output;
        }

        private void LoadLabels()
        {
            labels.Clear();
            labelLookup.Clear();

            if (labelMapJson == null || string.IsNullOrEmpty(labelMapJson.text)) return;

            try
            {
                string raw = labelMapJson.text.Trim();
                if (raw.StartsWith("{") && raw.EndsWith("}"))
                {
                    raw = raw.Substring(1, raw.Length - 2);
                }

                if (string.IsNullOrEmpty(raw)) return;

                string[] parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                SortedDictionary<int, string> sorted = new SortedDictionary<int, string>();

                foreach (string part in parts)
                {
                    string[] pair = part.Split(new[] { ':' }, 2);
                    if (pair.Length < 2) continue;

                    string keyText = pair[0].Replace("\"", string.Empty).Trim();
                    string valueText = pair[1].Replace("\"", string.Empty).Trim();

                    if (int.TryParse(keyText, out int key))
                    {
                        sorted[key] = valueText;
                    }
                }

                foreach (var kv in sorted)
                {
                    while (labels.Count <= kv.Key)
                    {
                        labels.Add(string.Empty);
                    }
                    labels[kv.Key] = kv.Value;

                    if (!labelLookup.ContainsKey(kv.Value))
                    {
                        labelLookup[kv.Value] = kv.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("GestureValidationControllerOnnx: failed to parse label map - " + ex.Message);
            }
        }

        // 公共属性访问器
        public bool IsTesting => isTesting;
        public int CurrentGestureIndex => currentGestureIndex;
        public string CurrentGestureLabel => currentGestureIndex >= 0 && currentGestureIndex < gestures.Count ? gestures[currentGestureIndex].label : "";
        public int TotalGestureCount => gestures.Count;
        public float TimeRemaining => isTesting ? Mathf.Max(0f, timeoutSeconds - (Time.time - gestureStartTime)) : 0f;
    }
}