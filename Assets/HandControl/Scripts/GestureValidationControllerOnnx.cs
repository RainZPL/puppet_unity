using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;
using TensorFloat = Unity.InferenceEngine.Tensor<float>;

namespace HandControl
{
  public class GestureValidationControllerOnnx : MonoBehaviour
  {
    [Serializable]
    public class ButtonInfo
    {
      public string label;
      public Button button;
      public Text statusText;
    }

    public HandTrackingSource handSource;
    public Text progressLabel;
    public Text resultLabel;
    public List<ButtonInfo> buttons = new List<ButtonInfo>();

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

    public Action<string> OnGestureMatched;
    public Action OnNewSessionStarted;

    private readonly List<float[]> frameBuffer = new List<float[]>();
    private readonly List<long> timeBuffer = new List<long>();
    private readonly Vector3[] rawPoints = new Vector3[21];
    private readonly Vector3[] normPoints = new Vector3[21];

    private Model model;
    private Worker worker;
    private readonly List<string> labels = new List<string>();
    private readonly Dictionary<string, int> labelLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private ButtonInfo activeButton;
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
      SetupButtons();
      RefreshUi();
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

      StopCollecting("Stopped", true);
    }

    private void OnDestroy()
    {
      worker?.Dispose();
      worker = null;
    }

    private void Update()
    {
      RefreshUi();
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

    private void SetupButtons()
    {
      foreach (var info in buttons)
      {
        if (info?.button == null)
        {
          continue;
        }

        var cached = info;
        info.button.onClick.AddListener(() => OnButtonClicked(cached));
        UpdateButtonLabel(info, false);

        if (info.statusText != null)
        {
          info.statusText.text = "Not tested";
          info.statusText.color = Color.white;
        }
      }
    }

    private void RefreshUi()
    {
      if (progressLabel != null)
      {
        progressLabel.text = progressText;
      }

      if (resultLabel != null)
      {
        resultLabel.text = resultText;
      }
    }

    private void OnButtonClicked(ButtonInfo info)
    {
      if (info == null)
      {
        return;
      }

      if (collecting && activeButton == info)
      {
        StopCollecting("Cancelled", true);
        return;
      }

      StopCollecting("Switching...", false);
      StartCollecting(info);
    }

    private void StartCollecting(ButtonInfo info)
    {
      if (string.IsNullOrWhiteSpace(info.label))
      {
        Debug.LogWarning("GestureValidationControllerOnnx: button missing label");
        return;
      }

      if (handSource == null)
      {
        Debug.LogWarning("GestureValidationControllerOnnx: no hand source set");
        return;
      }

      collecting = true;
      activeButton = info;
      activeLabel = info.label.Trim();

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
        UpdateButtonLabel(info, true);
      }

      if (info.statusText != null)
      {
        info.statusText.text = "Collecting...";
        info.statusText.color = Color.yellow;
      }

      OnNewSessionStarted?.Invoke();

      frameBuffer.Clear();
      timeBuffer.Clear();
      progressText = "Collecting window...";
      resultText = "Waiting...";
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

      if (activeButton != null)
      {
        UpdateButtonLabel(activeButton, false);
        if (activeButton.statusText != null)
        {
          activeButton.statusText.text = message;
          activeButton.statusText.color = message.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.green : Color.white;
        }
      }

      foreach (var item in buttons)
      {
        if (item?.button != null)
        {
          item.button.interactable = true;
        }
      }

      frameBuffer.Clear();
      timeBuffer.Clear();

      if (resetResult)
      {
        resultText = "Result: --";
      }

      activeButton = null;
      activeLabel = null;
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData data)
    {
      if (!collecting || worker == null)
      {
        return;
      }

      bool tracked = data != null && data.tracked && data.landmarks != null && data.landmarks.Length >= 21;
      if (!tracked && !allowEmptyFrames)
      {
        return;
      }

      SaveFrame(data, tracked);

      int count = frameBuffer.Count;
      if (count == 0)
      {
        return;
      }

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
        progressText = "Collecting " + activeLabel + ": " + seconds.ToString("F1") + "s / " + captureSeconds.ToString("F1") + "s";
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
      if (worker == null || frameBuffer.Count == 0)
      {
        return;
      }

      List<float[]> features = BuildFeatureList(frameBuffer);
      if (features.Count == 0)
      {
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
            resultText = "Target " + activeLabel + " prob = " + probText;
            if (targetProb >= passProbability && confidence >= passConfidence)
            {
              resultText = "Target " + activeLabel + " prob = " + probText + " (passed)";
              OnGestureMatched?.Invoke(activeLabel);
              StopCollecting("Finished", false);
            }
          }
          else
          {
            string bestName = bestIndex >= 0 && bestIndex < labels.Count ? labels[bestIndex] : "Unknown";
            resultText = "Prediction: " + bestName + " prob = " + bestProb.ToString("F2", CultureInfo.InvariantCulture);
          }
        }
        catch (Exception ex)
        {
          Debug.LogError("GestureValidationControllerOnnx: inference failed - " + ex.Message);
        }
      }
    }

    private float[] ReadTensor(Tensor tensor)
    {
      if (tensor == null)
      {
        return null;
      }

      try
      {
        using (var cpu = tensor.ReadbackAndClone() as TensorFloat)
        {
          if (cpu == null || cpu.count == 0)
          {
            return null;
          }

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

      int[][] chains =
      {
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
      if (logits.Count == 0)
      {
        return output;
      }

      float max = logits[0];
      for (int i = 1; i < logits.Count; i++)
      {
        if (logits[i] > max)
        {
          max = logits[i];
        }
      }

      float total = 0f;
      for (int i = 0; i < logits.Count; i++)
      {
        float value = Mathf.Exp(logits[i] - max);
        output[i] = value;
        total += value;
      }

      if (total <= 0f)
      {
        return output;
      }

      for (int i = 0; i < output.Length; i++)
      {
        output[i] /= total;
      }

      return output;
    }

    private void UpdateButtonLabel(ButtonInfo info, bool running)
    {
      if (info?.button == null)
      {
        return;
      }

      string niceName = string.IsNullOrWhiteSpace(info.label) ? "Gesture" : info.label;
      Text text = info.button.GetComponentInChildren<Text>();
      if (text != null)
      {
        text.text = running ? "Stop " + niceName : "Test " + niceName;
      }
    }

    private void LoadLabels()
    {
      labels.Clear();
      labelLookup.Clear();

      if (labelMapJson == null || string.IsNullOrEmpty(labelMapJson.text))
      {
        return;
      }

      try
      {
        string raw = labelMapJson.text.Trim();
        if (raw.StartsWith("{") && raw.EndsWith("}"))
        {
          raw = raw.Substring(1, raw.Length - 2);
        }

        if (string.IsNullOrEmpty(raw))
        {
          return;
        }

        string[] parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        SortedDictionary<int, string> sorted = new SortedDictionary<int, string>();

        foreach (string part in parts)
        {
          string[] pair = part.Split(new[] { ':' }, 2);
          if (pair.Length < 2)
          {
            continue;
          }

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
  }
}
