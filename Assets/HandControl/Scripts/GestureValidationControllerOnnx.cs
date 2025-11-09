using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Unity.InferenceEngine;
using UnityEngine;
using TMPro;
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
        public TextMeshProUGUI progressLabel;
        public TextMeshProUGUI resultLabel;
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
        public float timeoutSeconds = 15f;
        public bool autoStartOnEnable = true;
        public float animationWaitTime = 3f;
        public float DeltaTime;

        // Failure retry parameters
        public bool retryOnFailure = true;
        public int maxRetryCount = 3;
        private int currentRetryCount = 0;

        public Action<string> OnGestureMatched;
        public Action OnNewSessionStarted;
        public Action<int, bool, string> OnGestureResult;
        public Action OnAllGesturesCompleted;
        public Action<int, int, string> OnGestureRetry;
        public Action<string> OnAnimationPlaying;

        private readonly List<float[]> frameBuffer = new List<float[]>();
        private readonly List<long> timeBuffer = new List<long>();
        private readonly Vector3[] rawPoints = new Vector3[21];
        private readonly Vector3[] normPoints = new Vector3[21];

        private Model model;
        private Worker worker;
        private readonly List<string> labels = new List<string>();
        private readonly Dictionary<string, int> labelLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // State variables
        public int currentGestureIndex = -1;
        public bool isTesting = false;
        public bool isTimeout = false;
        public bool gesturePassed = false;
        public bool skipCurrentGesture = false;
        public bool isPlayingAnimation = false;
        public bool collecting = false;
        public bool hasRecognizedCurrentGesture = false;
        public bool isRetryingGesture = false;

        private string activeLabel;
        private string progressText = "Idle";
        private string resultText = "Result: --";
        private float gestureStartTime = 0f;
        private float animationStartTime = 0f;
        private float lastModelRunTime = 0f;
        private float modelRunInterval = 2f;
        private float currentGestureElapsedTime = 0f;

        private Coroutine animationCoroutine;

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
            LoadLabels(); // 修复：添加了LoadLabels方法
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

            if (animationCoroutine != null)
            {
                StopCoroutine(animationCoroutine);
            }
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

            if (!isTesting) return;

            // Update elapsed time
            currentGestureElapsedTime = Time.time - gestureStartTime;
            DeltaTime = currentGestureElapsedTime;

            // Handle different states
            if (collecting && !hasRecognizedCurrentGesture)
            {
                // Check timeout during collection
                if (currentGestureElapsedTime > timeoutSeconds)
                {
                    Debug.Log($"Gesture {CurrentGestureLabel} timeout after {currentGestureElapsedTime:F1}s");
                    HandleGestureTimeout();
                    return;
                }
            }
            else if (gesturePassed && !isPlayingAnimation)
            {
                // Gesture passed - start animation
                Debug.Log($"Gesture passed, starting animation for {CurrentGestureLabel}");
                StartAnimationPlayback(CurrentGestureLabel);
            }
            else if (isPlayingAnimation)
            {
                // Check if animation completed
                if (Time.time - animationStartTime >= animationWaitTime)
                {
                    Debug.Log("Animation completed");
                    CompleteAnimationPlayback();

                    // After animation, decide what to do next
                    if (gesturePassed)
                    {
                        // Gesture passed - move to next gesture
                        Debug.Log("Moving to next gesture after successful animation");
                        StartNextGesture();
                    }
                    else if (isRetryingGesture)
                    {
                        // Retry animation completed - restart current gesture
                        Debug.Log("Retry animation completed, restarting current gesture");
                        isRetryingGesture = false;
                        StartCurrentGesture();
                    }
                }
            }
            else if (isTimeout && !collecting && !isPlayingAnimation)
            {
                // Timeout - start retry animation
                Debug.Log($"Gesture timeout, starting retry animation for: {CurrentGestureLabel}");
                StartRetryAnimation();
            }
            else if (skipCurrentGesture && !collecting && !isPlayingAnimation)
            {
                // Skip - move to next gesture
                Debug.Log($"Gesture skipped, moving to next gesture");
                StartNextGesture();
            }
        }

        private void HandleGestureTimeout()
        {
            if (currentGestureIndex >= 0 && currentGestureIndex < gestures.Count && collecting)
            {
                var gesture = gestures[currentGestureIndex];
                Debug.Log($"Gesture {gesture.label} timeout after {currentGestureElapsedTime:F1}s");

                // Trigger result callback (failure)
                OnGestureResult?.Invoke(currentGestureIndex, false, gesture.label);

                // Stop collection and set timeout flag
                collecting = false;
                isTimeout = true;
                gesturePassed = false;
                hasRecognizedCurrentGesture = false;

                frameBuffer.Clear();
                timeBuffer.Clear();
                activeLabel = null;

                Debug.Log($"Timeout handling completed for {gesture.label}");
            }
        }

        private void StartRetryAnimation()
        {
            if (isPlayingAnimation) return;

            // Check retry count
            if (retryOnFailure && currentRetryCount < maxRetryCount)
            {
                currentRetryCount++;
                Debug.Log($"Starting retry animation for {CurrentGestureLabel} (attempt {currentRetryCount}/{maxRetryCount})");

                // Trigger retry callback
                OnGestureRetry?.Invoke(currentGestureIndex, currentRetryCount, CurrentGestureLabel);

                // Start retry animation
                isPlayingAnimation = true;
                isRetryingGesture = true;
                animationStartTime = Time.time;

                // Trigger animation playback callback for retry
                OnAnimationPlaying?.Invoke($"Retry_{CurrentGestureLabel}");

                Debug.Log($"Started retry animation for {CurrentGestureLabel}, duration: {animationWaitTime}s");
            }
            else
            {
                // Max retries reached, move to next gesture
                Debug.Log($"Max retries reached for {CurrentGestureLabel}, moving to next gesture");
                currentRetryCount = 0;
                isTimeout = false;
                isRetryingGesture = false;
                StartNextGesture();
            }
        }

        private void StartCurrentGesture()
        {
            if (currentGestureIndex < 0 || currentGestureIndex >= gestures.Count) return;

            Debug.Log($"Restarting current gesture: {CurrentGestureLabel}");

            // Reset state flags for current gesture
            gesturePassed = false;
            isTimeout = false;
            skipCurrentGesture = false;
            isPlayingAnimation = false;
            collecting = false;
            hasRecognizedCurrentGesture = false;
            isRetryingGesture = false;

            frameBuffer.Clear();
            timeBuffer.Clear();

            var currentGesture = gestures[currentGestureIndex];
            if (string.IsNullOrWhiteSpace(currentGesture.label))
            {
                Debug.LogWarning($"Skipping gesture at index {currentGestureIndex} due to empty label");
                StartNextGesture(); // Skip invalid gesture
                return;
            }

            // Start current gesture session
            gestureStartTime = Time.time;
            currentGestureElapsedTime = 0f;
            lastModelRunTime = 0f;

            StartCollecting(currentGesture);
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
            float remaining = Mathf.Max(0f, timeoutSeconds - currentGestureElapsedTime);

            if (isPlayingAnimation)
            {
                float animationElapsed = Time.time - animationStartTime;
                float animationRemaining = Mathf.Max(0f, animationWaitTime - animationElapsed);

                if (isRetryingGesture)
                {
                    return $"Retry animation... {animationRemaining:F1}s (Attempt {currentRetryCount}/{maxRetryCount})";
                }
                else
                {
                    return $"Playing animation... {animationRemaining:F1}s";
                }
            }

            if (gesturePassed) return $"Gesture passed! Starting animation...";
            if (isTimeout) return $"Timeout! Preparing retry... (Attempt {currentRetryCount + 1}/{maxRetryCount + 1})";
            if (skipCurrentGesture) return $"Skipped! Preparing next gesture...";
            if (hasRecognizedCurrentGesture) return $"Processing result...";

            return $"Testing {gesture.label}: {remaining:F1}s remaining, Frames: {frameBuffer.Count}";
        }

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

            // Reset all state variables
            isTesting = true;
            currentGestureIndex = -1;
            gesturePassed = false;
            isTimeout = false;
            skipCurrentGesture = false;
            isPlayingAnimation = false;
            collecting = false;
            hasRecognizedCurrentGesture = false;
            isRetryingGesture = false;
            currentRetryCount = 0;
            lastModelRunTime = 0f;
            currentGestureElapsedTime = 0f;

            Debug.Log($"Starting auto testing with {gestures.Count} gestures");
            StartNextGesture();
        }

        public void StopTesting()
        {
            if (isTesting)
            {
                Debug.Log("Auto testing stopped");
            }

            isTesting = false;
            collecting = false;
            isPlayingAnimation = false;

            // Reset all state variables
            currentGestureIndex = -1;
            gesturePassed = false;
            isTimeout = false;
            skipCurrentGesture = false;
            hasRecognizedCurrentGesture = false;
            isRetryingGesture = false;
            currentRetryCount = 0;

            if (animationCoroutine != null)
            {
                StopCoroutine(animationCoroutine);
                animationCoroutine = null;
            }

            frameBuffer.Clear();
            timeBuffer.Clear();
            activeLabel = null;

            progressText = "Idle";
            resultText = "Result: --";
        }

        public void RestartTesting()
        {
            StopTesting();
            StartAutoTesting();
        }

        public void SkipCurrentGesture()
        {
            if (!isTesting || !collecting) return;

            Debug.Log($"Skipping gesture {CurrentGestureLabel}");
            skipCurrentGesture = true;
            collecting = false;
            hasRecognizedCurrentGesture = false;

            frameBuffer.Clear();
            timeBuffer.Clear();
        }

        private void StartNextGesture()
        {
            Debug.Log("Starting next gesture...");

            // Reset state flags for new gesture
            gesturePassed = false;
            isTimeout = false;
            skipCurrentGesture = false;
            isPlayingAnimation = false;
            collecting = false;
            hasRecognizedCurrentGesture = false;
            isRetryingGesture = false;
            currentRetryCount = 0; // Reset retry count for new gesture

            frameBuffer.Clear();
            timeBuffer.Clear();

            currentGestureIndex++;

            // Check if all gestures completed
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
                StartNextGesture(); // Skip invalid gesture
                return;
            }

            // Start new gesture session
            gestureStartTime = Time.time;
            currentGestureElapsedTime = 0f;
            lastModelRunTime = 0f;

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
            gesturePassed = false;
            isTimeout = false;
            skipCurrentGesture = false;
            hasRecognizedCurrentGesture = false;
            isRetryingGesture = false;

            OnNewSessionStarted?.Invoke();

            progressText = $"Collecting {activeLabel}...";
            resultText = "Waiting...";

            Debug.Log($"Started collecting gesture: {activeLabel}, timeout: {timeoutSeconds}s");
        }

        private void HandleHandFrame(HandTrackingSource.HandFrameData data)
        {
            if (!collecting || worker == null || isPlayingAnimation || hasRecognizedCurrentGesture) return;

            bool tracked = data != null && data.tracked && data.landmarks != null && data.landmarks.Length >= 21;
            if (!tracked && !allowEmptyFrames) return;

            SaveFrame(data, tracked);
            int count = frameBuffer.Count;
            if (count == 0) return;

            float seconds = GetWindowSeconds();
            int needFrames = Mathf.Max(minimumFrames, Mathf.CeilToInt(captureSeconds * Mathf.Max(1f, pretendFps)));

            // Check time interval to prevent frequent model runs
            float currentTime = Time.time;
            if (currentTime - lastModelRunTime < modelRunInterval)
            {
                progressText = $"Collecting {activeLabel}: {seconds:F1}s/{captureSeconds:F1}s, Frames: {count}/{needFrames}";
                return;
            }

            // Run model when enough data is collected
            if (count >= needFrames && seconds >= captureSeconds)
            {
                lastModelRunTime = currentTime;
                hasRecognizedCurrentGesture = true;

                Debug.Log($"Running model with {count} frames, {seconds:F1}s of data");
                RunModel();
            }
            else
            {
                progressText = $"Collecting {activeLabel}: {seconds:F1}s/{captureSeconds:F1}s, Frames: {count}/{needFrames}";
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
            else
            {
                for (int i = 0; i < 63; i++)
                {
                    row[i] = 0f;
                }
            }

            frameBuffer.Add(row);
            timeBuffer.Add((long)(Time.time * 1000f));
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
            if (worker == null || frameBuffer.Count == 0)
            {
                hasRecognizedCurrentGesture = false;
                return;
            }

            List<float[]> features = BuildFeatureList(frameBuffer);
            if (features.Count == 0)
            {
                hasRecognizedCurrentGesture = false;
                return;
            }

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

                for (int i = usableFrames; i < modelMaxLength; i++)
                {
                    for (int j = 0; j < featureCount; j++)
                    {
                        input[0, i, j] = 0f;
                    }
                    mask[0, i] = 0f;
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
                        hasRecognizedCurrentGesture = false;
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
                        resultText = $"Target {activeLabel} probability = {probText}, Confidence = {confidence:F2}";

                        if (targetProb >= passProbability && confidence >= passConfidence)
                        {
                            resultText = $"Target {activeLabel} probability = {probText} (PASSED)";

                            // Set gesture passed flag
                            gesturePassed = true;
                            isTimeout = false;
                            skipCurrentGesture = false;
                            collecting = false;
                            hasRecognizedCurrentGesture = true;
                            currentRetryCount = 0; // Reset retry count on success

                            // Trigger events
                            OnGestureMatched?.Invoke(activeLabel);
                            OnGestureResult?.Invoke(currentGestureIndex, true, activeLabel);

                            Debug.Log($"Gesture {activeLabel} PASSED! Probability: {probText}, Confidence: {confidence:F2}");
                        }
                        else
                        {
                            resultText = $"Target {activeLabel} probability = {probText} (FAILED)";
                            Debug.Log($"Gesture {activeLabel} failed. Probability: {probText} (needs {passProbability}), Confidence: {confidence:F2} (needs {passConfidence})");

                            // Gesture failed, continue collecting
                            hasRecognizedCurrentGesture = false;
                        }
                    }
                    else
                    {
                        string bestName = bestIndex >= 0 && bestIndex < labels.Count ? labels[bestIndex] : "Unknown";
                        resultText = $"Prediction: {bestName} probability = {bestProb:F2}";
                        hasRecognizedCurrentGesture = false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("GestureValidationControllerOnnx: inference failed - " + ex.Message);
                    hasRecognizedCurrentGesture = false;
                }
            }
        }

        private void StartAnimationPlayback(string gestureLabel)
        {
            if (isPlayingAnimation) return;

            isPlayingAnimation = true;
            isRetryingGesture = false;
            animationStartTime = Time.time;

            // Trigger animation playback callback
            OnAnimationPlaying?.Invoke(gestureLabel);

            Debug.Log($"Started animation playback for {gestureLabel}, duration: {animationWaitTime}s");
        }

        private void CompleteAnimationPlayback()
        {
            isPlayingAnimation = false;
            Debug.Log("Animation playback completed");
        }

        // 修复：添加缺失的LoadLabels方法
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

        // 修复：添加缺失的BuildFeatureList方法
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

        // 修复：添加缺失的ReadTensor方法
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

        // 修复：添加缺失的ComputeSoftmax方法
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

        // 修复：添加缺失的ExtractFeatures方法
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

        // 修复：添加缺失的NormaliseHand方法
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

        // Public property accessors
        public bool IsTesting => isTesting;
        public int CurrentGestureIndex => currentGestureIndex;
        public string CurrentGestureLabel => currentGestureIndex >= 0 && currentGestureIndex < gestures.Count ? gestures[currentGestureIndex].label : "";
        public int TotalGestureCount => gestures.Count;
        public float TimeRemaining => isTesting ? Mathf.Max(0f, timeoutSeconds - currentGestureElapsedTime) : 0f;
        public bool IsPlayingAnimation => isPlayingAnimation;
        public float AnimationTimeRemaining => isPlayingAnimation ? Mathf.Max(0f, animationWaitTime - (Time.time - animationStartTime)) : 0f;
        public float CurrentElapsedTime => currentGestureElapsedTime;
        public int CurrentRetryCount => currentRetryCount;
        public int MaxRetryCount => maxRetryCount;
        public bool IsRetryingGesture => isRetryingGesture;
    }
}