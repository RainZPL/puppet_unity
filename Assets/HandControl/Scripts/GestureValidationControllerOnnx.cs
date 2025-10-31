using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;
using TensorFloat = Unity.InferenceEngine.Tensor<float>;

namespace HandControl
{
  // simple ONNX/Barracuda-based validation controller
  public class GestureValidationControllerOnnx : MonoBehaviour
  {
    [Serializable]
    public class ButtonRef
    {
      public string label;
      public Button button;
      public Text status;
    }

    public HandTrackingSource handSource;
    public Text progressText;
    public Text resultText;
    public List<ButtonRef> buttons = new List<ButtonRef>();

    public ModelAsset modelAsset;
    public TextAsset labelMapJson;

    public float windowSeconds = 5f;
    public int minFrames = 60;
    public float fakeFps = 30f;
    public bool fillMissing = true;
    public float successProb = 0.75f;
    public float successConf = 0.5f;
    public int modelMaxLen = 100;
    public int modelFeatureDim = 20;

    private readonly List<float[]> frameList = new List<float[]>();
    private readonly List<long> frameTimes = new List<long>();

    private readonly Vector3[] landmarkBuffer = new Vector3[21];
    private readonly Vector3[] normBuffer = new Vector3[21];

    private Model runtimeModel;
    private Worker worker;

    private readonly List<string> labelNames = new List<string>();
    private readonly Dictionary<string, int> labelLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private ButtonRef activeButton;
    private string activeLabel;
    private bool isCollecting;
    private string progressMsg = "Not started.";
    private string resultMsg = "Result: --";

    private void Awake()
    {
      if (modelAsset == null)
      {
        Debug.LogError("GestureValidationControllerOnnx: modelAsset is missing.");
        enabled = false;
        return;
      }

      runtimeModel = ModelLoader.Load(modelAsset);
      try
      {
        worker = new Worker(runtimeModel, BackendType.GPUCompute);
      }
      catch (Exception ex)
      {
        Debug.LogWarning($"GestureValidationControllerOnnx: GPU worker failed ({ex.Message}), falling back to CPU.");
        worker?.Dispose();
        worker = new Worker(runtimeModel, BackendType.CPU);
      }
      ReadLabels();

      foreach (var info in buttons)
      {
        if (info?.button == null)
        {
          continue;
        }

        var localInfo = info;
        info.button.onClick.AddListener(() => HandleButton(localInfo));
        SetButtonLabel(info, false);

        if (info.status != null)
        {
          info.status.text = "Not started";
          info.status.color = Color.white;
        }
      }

      UpdateUi();
    }

    private void OnEnable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame += HandleHandFrame;
      }
    }

    private void OnDisable()
    {
      if (handSource != null)
      {
        handSource.OnHandFrame -= HandleHandFrame;
      }

      StopCollect("Stopped", true);
    }

    private void OnDestroy()
    {
      worker?.Dispose();
      worker = null;
    }

    private void Update()
    {
      UpdateUi();
    }

    private void HandleButton(ButtonRef info)
    {
      if (info == null)
      {
        return;
      }

      if (isCollecting && activeButton == info)
      {
        StopCollect("Stopped by user", true);
        return;
      }

      StopCollect("Setting up...", false);
      StartCollect(info);
    }

    private void StartCollect(ButtonRef info)
    {
      if (string.IsNullOrWhiteSpace(info.label))
      {
        Debug.LogWarning("GestureValidationControllerOnnx: empty label.");
        return;
      }

      if (handSource == null)
      {
        Debug.LogWarning("GestureValidationControllerOnnx: handSource missing.");
        return;
      }

      activeButton = info;
      activeLabel = info.label.Trim();
      isCollecting = true;

      foreach (var item in buttons)
      {
        if (item?.button != null)
        {
          item.button.interactable = false;
        }
      }

      if (info.button != null)
      {
        info.button.interactable = true;
        SetButtonLabel(info, true);
      }

      if (info.status != null)
      {
        info.status.text = "Collecting...";
        info.status.color = Color.yellow;
      }

      frameList.Clear();
      frameTimes.Clear();
      progressMsg = "Collecting window...";
      resultMsg = "Waiting for model...";
    }

    private void StopCollect(string text, bool resetResult)
    {
      if (!isCollecting)
      {
        progressMsg = text;
        if (resetResult)
        {
          resultMsg = "Result: --";
        }
        return;
      }

      isCollecting = false;
      progressMsg = text;

      if (activeButton != null)
      {
        if (activeButton.button != null)
        {
          SetButtonLabel(activeButton, false);
        }

        if (activeButton.status != null)
        {
          activeButton.status.text = text;
          activeButton.status.color = text.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.green : Color.white;
        }
      }

      foreach (var item in buttons)
      {
        if (item?.button != null)
        {
          item.button.interactable = true;
        }
      }

      frameList.Clear();
      frameTimes.Clear();

      if (resetResult)
      {
        resultMsg = "Result: --";
      }

      activeButton = null;
      activeLabel = null;
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData data)
    {
      if (!isCollecting)
      {
        return;
      }

      var tracked = data != null && data.tracked && data.landmarks != null && data.landmarks.Length >= 21;
      if (!tracked && !fillMissing)
      {
        return;
      }

      AddFrame(data, tracked);
      var count = frameList.Count;
      if (count == 0)
      {
        return;
      }

      var seconds = CalcSeconds();
      var needFrames = Mathf.Max(minFrames, Mathf.CeilToInt(windowSeconds * Mathf.Max(1f, fakeFps)));

      if (count >= needFrames && seconds >= windowSeconds)
      {
        RunModel();
        frameList.Clear();
        frameTimes.Clear();
        progressMsg = "Window sent to model.";
      }
      else
      {
        progressMsg = $"Collecting {activeLabel}: {seconds:F1}s / {windowSeconds:F1}s";
      }
    }

    private void RunModel()
    {
      if (worker == null)
      {
        Debug.LogWarning("GestureValidationControllerOnnx: worker not ready.");
        return;
      }

      var features = BuildFeatureList(frameList);
      if (features.Count == 0)
      {
        return;
      }

      var seqLen = Mathf.Min(features.Count, modelMaxLen);
      var startIndex = Mathf.Max(0, features.Count - modelMaxLen);

      using (var inputTensor = new TensorFloat(new TensorShape(1, modelMaxLen, modelFeatureDim)))
      using (var maskTensor = new TensorFloat(new TensorShape(1, modelMaxLen)))
      {
        for (var i = 0; i < seqLen; i++)
        {
          var feat = features[startIndex + i];
          for (var j = 0; j < modelFeatureDim; j++)
          {
            inputTensor[0, i, j] = j < feat.Length ? feat[j] : 0f;
          }

          maskTensor[0, i] = 1f;
        }

        try
        {
          worker.SetInput("input_seq", inputTensor);
          worker.SetInput("input_mask", maskTensor);
          worker.Schedule();

          var logitsTensor = worker.PeekOutput("logits");
          if (logitsTensor == null)
          {
            Debug.LogWarning("GestureValidationControllerOnnx: logits output missing.");
            return;
          }

          float[] logitsValues;
          using (var logitsCpu = logitsTensor.ReadbackAndClone() as TensorFloat)
          {
            if (logitsCpu == null || logitsCpu.count == 0)
            {
              Debug.LogWarning("GestureValidationControllerOnnx: failed to read logits.");
              return;
            }

            logitsValues = new float[logitsCpu.count];
            for (var idx = 0; idx < logitsValues.Length; idx++)
            {
              logitsValues[idx] = logitsCpu[idx];
            }
          }

          var probs = Softmax(logitsValues);
          var topIndex = 0;
          var topProb = 0f;
          for (var i = 0; i < probs.Length; i++)
          {
            if (probs[i] > topProb)
            {
              topProb = probs[i];
              topIndex = i;
            }
          }

          var confValue = 0f;
          var confTensor = worker.PeekOutput("confidence");
          if (confTensor != null)
          {
            using (var confCpu = confTensor.ReadbackAndClone() as TensorFloat)
            {
              if (confCpu != null && confCpu.count > 0)
              {
                confValue = confCpu[0];
              }
            }
          }

          var targetProb = topProb;
          if (!string.IsNullOrEmpty(activeLabel) && labelLookup.TryGetValue(activeLabel, out var targetIndex) && targetIndex < probs.Length)
          {
            targetProb = probs[targetIndex];
          }

          var targetProbText = targetProb.ToString("F2", CultureInfo.InvariantCulture);

          if (!string.IsNullOrEmpty(activeLabel))
          {
            resultMsg = "Target " + activeLabel + " prob = " + targetProbText;
            if (targetProb >= successProb && confValue >= successConf)
            {
              resultMsg = "Target " + activeLabel + " prob = " + targetProbText + " (passed)";
              StopCollect("Finished", false);
            }
          }
          else
          {
            var topLabel = (topIndex >= 0 && topIndex < labelNames.Count) ? labelNames[topIndex] : "Unknown";
            resultMsg = "Prediction: " + topLabel + " prob = " + topProb.ToString("F2", CultureInfo.InvariantCulture);
          }

          logitsTensor.Dispose();
          confTensor?.Dispose();
        }
        catch (Exception ex)
        {
          Debug.LogError("GestureValidationControllerOnnx: inference failed - " + ex.Message);
        }
      }
    }

    private List<float[]> BuildFeatureList(List<float[]> frames)
    {
      var list = new List<float[]>(frames.Count);
      for (var i = 0; i < frames.Count; i++)
      {
        var frame = frames[i];
        var feat = ExtractFeatures(frame);
        if (feat != null)
        {
          list.Add(feat);
        }
      }

      var velocities = new float[list.Count];
      for (var i = 1; i < list.Count; i++)
      {
        var sum = 0f;
        for (var j = 0; j < 5 && j < list[i].Length && j < list[i - 1].Length; j++)
        {
          var diff = list[i][j] - list[i - 1][j];
          sum += diff * diff;
        }

        velocities[i] = Mathf.Sqrt(sum);
      }

      for (var i = 0; i < list.Count; i++)
      {
        if (list[i].Length > 0)
        {
          list[i][list[i].Length - 1] = velocities[i];
        }
      }

      return list;
    }

    private float[] ExtractFeatures(float[] frame)
    {
      if (frame == null || frame.Length < 63)
      {
        return new float[modelFeatureDim];
      }

      for (var i = 0; i < 21; i++)
      {
        var idx = i * 3;
        landmarkBuffer[i] = new Vector3(frame[idx], frame[idx + 1], frame[idx + 2]);
      }

      NormalizeLandmarks();
      var features = new float[modelFeatureDim];
      var write = 0;

      var palmIndices = new[] { 0, 5, 9, 13, 17 };
      var palm = Vector2.zero;
      foreach (var pi in palmIndices)
      {
        palm += new Vector2(normBuffer[pi].x, normBuffer[pi].y);
      }
      palm /= palmIndices.Length;

      var tips = new[] { 4, 8, 12, 16, 20 };
      foreach (var tip in tips)
      {
        var tipVec = new Vector2(normBuffer[tip].x, normBuffer[tip].y);
        features[write++] = Vector2.Distance(tipVec, palm);
      }

      var chains = new[]
      {
        new [] { 1, 2, 3, 4 },
        new [] { 5, 6, 7, 8 },
        new [] { 9, 10, 11, 12 },
        new [] { 13, 14, 15, 16 },
        new [] { 17, 18, 19, 20 }
      };

      foreach (var chain in chains)
      {
        for (var i = 0; i < chain.Length - 2; i++)
        {
          var a = normBuffer[chain[i]] - normBuffer[chain[i + 1]];
          var b = normBuffer[chain[i + 2]] - normBuffer[chain[i + 1]];
          var aLen = a.magnitude;
          var bLen = b.magnitude;
          if (aLen < 1e-6f || bLen < 1e-6f)
          {
            features[write++] = 0f;
            continue;
          }

          a /= aLen;
          b /= bLen;
          var dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
          features[write++] = Mathf.Acos(dot);
        }
      }

      for (var i = 0; i < tips.Length - 1; i++)
      {
        features[write++] = Vector3.Distance(normBuffer[tips[i]], normBuffer[tips[i + 1]]);
      }

      if (write < modelFeatureDim)
      {
        features[write] = 0f;
      }

      return features;
    }

    private void NormalizeLandmarks()
    {
      var wrist = landmarkBuffer[0];
      for (var i = 0; i < landmarkBuffer.Length; i++)
      {
        var v = landmarkBuffer[i];
        normBuffer[i] = new Vector3(v.x - wrist.x, v.y - wrist.y, v.z - wrist.z);
      }

      var fingerBases = new[] { 5, 9, 13, 17 };
      var sum = 0f;
      foreach (var idx in fingerBases)
      {
        sum += Vector3.Distance(normBuffer[idx], normBuffer[0]);
      }

      var handSize = (fingerBases.Length > 0 ? sum / fingerBases.Length : 1f) + 1e-6f;
      for (var i = 0; i < normBuffer.Length; i++)
      {
        normBuffer[i] /= handSize;
      }
    }

    private void AddFrame(HandTrackingSource.HandFrameData info, bool tracked)
    {
      var slot = new float[63];
      if (tracked && info != null && info.landmarks != null)
      {
        for (var i = 0; i < 21 && i < info.landmarks.Length; i++)
        {
          var lm = info.landmarks[i];
          var idx = i * 3;
          slot[idx] = lm.x;
          slot[idx + 1] = lm.y;
          slot[idx + 2] = lm.z;
        }
      }

      frameList.Add(slot);
      frameTimes.Add(info != null ? info.timestampMillisec : 0);
    }

    private float CalcSeconds()
    {
      if (frameTimes.Count >= 2)
      {
        var first = frameTimes[0];
        var last = frameTimes[frameTimes.Count - 1];
        if (last > first)
        {
          return (last - first) / 1000f;
        }
      }

      return frameList.Count / Mathf.Max(1f, fakeFps);
    }

    private void UpdateUi()
    {
      if (progressText != null)
      {
        progressText.text = progressMsg;
      }

      if (resultText != null)
      {
        resultText.text = resultMsg;
      }
    }

    private void ReadLabels()
    {
      labelNames.Clear();
      labelLookup.Clear();

      if (labelMapJson == null)
      {
        return;
      }

      try
      {
        var raw = labelMapJson.text.Trim();
        raw = raw.Trim('{', '}', '\n', '\r', '\t');
        if (string.IsNullOrEmpty(raw))
        {
          return;
        }

        var entries = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        var sorted = new SortedDictionary<int, string>();

        foreach (var entry in entries)
        {
          var pair = entry.Split(new[] { ':' }, 2);
          if (pair.Length < 2)
          {
            continue;
          }

          var keyText = pair[0].Replace("\"", string.Empty).Trim();
          var valueText = pair[1].Replace("\"", string.Empty).Trim();

          if (int.TryParse(keyText, out var key))
          {
            sorted[key] = valueText;
          }
        }

        foreach (var kv in sorted)
        {
          while (labelNames.Count <= kv.Key)
          {
            labelNames.Add(string.Empty);
          }

          labelNames[kv.Key] = kv.Value;
          if (!labelLookup.ContainsKey(kv.Value))
          {
            labelLookup.Add(kv.Value, kv.Key);
          }
        }
      }
      catch (Exception ex)
      {
        Debug.LogWarning("GestureValidationControllerOnnx: failed to read label map - " + ex.Message);
      }
    }

    private static float[] Softmax(IReadOnlyList<float> values)
    {
      var result = new float[values.Count];
      if (values.Count == 0)
      {
        return result;
      }

      var max = values[0];
      for (var i = 1; i < values.Count; i++)
      {
        if (values[i] > max)
        {
          max = values[i];
        }
      }

      float sum = 0f;
      for (var i = 0; i < values.Count; i++)
      {
        var exp = Mathf.Exp(values[i] - max);
        result[i] = exp;
        sum += exp;
      }

      if (sum <= 0f)
      {
        return result;
      }

      for (var i = 0; i < result.Length; i++)
      {
        result[i] /= sum;
      }

      return result;
    }

    private void SetButtonLabel(ButtonRef info, bool running)
    {
      if (info?.button == null)
      {
        return;
      }

      var label = string.IsNullOrWhiteSpace(info.label) ? "Test Gesture" : info.label;
      var textComp = info.button.GetComponentInChildren<Text>();
      if (textComp != null)
      {
        textComp.text = running ? ("Stop " + label) : ("Test " + label);
      }
    }
  }
}
