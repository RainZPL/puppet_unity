using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace HandControl
{
  /// <summary>
  /// Provides button-driven gesture validation. Each button corresponds to a target gesture.
  /// When a button is pressed the script streams hand landmarks to the Python inference server
  /// until the target gesture reaches the configured probability/ confidence threshold.
  /// </summary>
  [DefaultExecutionOrder(-4)]
  public class GestureValidationController : MonoBehaviour
  {
    [Serializable]
    private class GestureButtonConfig
    {
      [Tooltip("Label returned by the Python model for this gesture.")]
      public string gestureLabel;

      [Tooltip("UI button that triggers the validation for the gesture.")]
      public Button triggerButton;

      [Tooltip("Optional status text shown near the button.")]
      public Text statusLabel;
    }

    [Header("References")]
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private Text progressText;
    [SerializeField] private Text resultText;
    [SerializeField] private List<GestureButtonConfig> gestureButtons = new();

    [Header("Network")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 50007;

    [Header("Window Settings")]
    [SerializeField, Tooltip("Seconds of data to collect before sending to Python.")]
    private float windowSeconds = 5f;

    [SerializeField, Tooltip("Minimum frames required before sending a window.")]
    private int minimumFrames = 60;

    [SerializeField, Tooltip("Expected capture FPS used to estimate window duration.")]
    private float targetFps = 30f;

    [SerializeField, Tooltip("When tracking is lost, append a zero frame to keep timing consistent.")]
    private bool padMissingFrames = true;

    [Header("Success Criteria")]
    [SerializeField, Tooltip("Minimum top-1 probability required to validate the gesture.")]
    private float successProbabilityThreshold = 0.75f;

    [SerializeField, Tooltip("Minimum model confidence required to validate the gesture.")]
    private float successConfidenceThreshold = 0.5f;

    private readonly ConcurrentQueue<string> _sendQueue = new();
    private readonly ConcurrentQueue<(string message, bool resetResult)> _stopRequests = new();
    private readonly List<float[]> _windowFrames = new();
    private readonly List<long> _windowTimestamps = new();

    private CancellationTokenSource _cts;
    private TcpClient _client;
    private StreamWriter _writer;
    private StreamReader _reader;
    private Task _senderTask;
    private Task _receiverTask;
    private bool _isRunning;
    private bool _isConnected;
    private string _progressStatus = "Select a gesture to begin.";
    private string _resultStatus = "Result: --";
    private readonly object _connectionLock = new();

    private GestureButtonConfig _activeButton;
    private string _targetGesture;

    [Serializable]
    private class GestureResult
    {
      public string top1;
      public float top1_prob;
      public string target_label;
      public float prob;
      public float confidence;
      public bool match;
    }

    private void Awake()
    {
      foreach (var config in gestureButtons)
      {
        if (config == null || config.triggerButton == null)
        {
          continue;
        }

        var capturedConfig = config;
        config.triggerButton.onClick.AddListener(() => HandleGestureButton(capturedConfig));

        var label = string.IsNullOrWhiteSpace(config.gestureLabel)
          ? "Test Gesture"
          : $"Test {config.gestureLabel}";
        config.triggerButton.SetLabel(label);

        if (config.statusLabel != null)
        {
          config.statusLabel.text = "Not tested";
          config.statusLabel.color = Color.white;
        }
      }

      ConfigureText(progressText);
      ConfigureText(resultText);
      UpdateUiText();
    }

    private static void ConfigureText(Text target)
    {
      if (target == null)
      {
        return;
      }

      target.horizontalOverflow = HorizontalWrapMode.Overflow;
      target.verticalOverflow = VerticalWrapMode.Overflow;
      target.resizeTextForBestFit = false;
      target.alignment = TextAnchor.UpperLeft;
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

      StopGestureInternal("Validation stopped.", true);
    }

    private void Update()
    {
      UpdateUiText();

      if (_stopRequests.TryDequeue(out var request))
      {
        StopGestureInternal(request.message, true, request.resetResult);
      }
    }

    private void HandleGestureButton(GestureButtonConfig config)
    {
      if (config == null)
      {
        return;
      }

      if (_isRunning && _activeButton == config)
      {
        StopGestureInternal("Validation cancelled.", true);
        return;
      }

      StopGestureInternal("Preparing new validation...", false);
      StartGestureValidation(config);
    }

    private void StartGestureValidation(GestureButtonConfig config)
    {
      if (config == null || string.IsNullOrWhiteSpace(config.gestureLabel))
      {
        Debug.LogWarning("GestureValidationController: gesture label not configured.");
        return;
      }

      if (source == null)
      {
        Debug.LogWarning("GestureValidationController: HandTrackingSource is not assigned.");
        return;
      }

      _activeButton = config;
      _targetGesture = config.gestureLabel.Trim();

      SetButtonInteractable(false);
      config.triggerButton.interactable = true;
      config.triggerButton.SetLabel($"Stop {_targetGesture}");
      if (config.statusLabel != null)
      {
        config.statusLabel.text = $"Testing {_targetGesture}...";
        config.statusLabel.color = Color.yellow;
      }

      _windowFrames.Clear();
      _windowTimestamps.Clear();
      _progressStatus = $"Collecting {_targetGesture} window...";
      _resultStatus = $"Target: {_targetGesture}\nWaiting for prediction...";

      _cts = new CancellationTokenSource();
      _isRunning = true;

      EnsureConnectionAsync(_cts.Token).ContinueWith(task =>
      {
        if (_cts == null || !_isRunning)
        {
          return;
        }

        if (task.IsCanceled)
        {
          return;
        }

        if (task.Exception != null)
        {
          Debug.LogError($"GestureValidationController: connection failed - {task.Exception.InnerException?.Message}");
          RequestStop("Failed to connect to inference server.", true);
          return;
        }

        _senderTask = Task.Run(() => SenderLoop(_cts.Token), _cts.Token);
        _receiverTask = Task.Run(() => ReceiverLoop(_cts.Token), _cts.Token);
      }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetButtonInteractable(bool enabled)
    {
      foreach (var config in gestureButtons)
      {
        if (config?.triggerButton == null)
        {
          continue;
        }

        config.triggerButton.interactable = enabled;
        if (enabled)
        {
          var label = string.IsNullOrWhiteSpace(config.gestureLabel)
            ? "Test Gesture"
            : $"Test {config.gestureLabel}";
          config.triggerButton.SetLabel(label);
        }
      }
    }

    private void UpdateUiText()
    {
      if (progressText != null)
      {
        progressText.text = _progressStatus;
      }

      if (resultText != null)
      {
        resultText.text = _resultStatus;
      }
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData frame)
    {
      if (!_isRunning || !_isConnected)
      {
        return;
      }

      var tracked = frame != null && frame.tracked && frame.landmarks != null && frame.landmarks.Length >= 21;
      if (!tracked && !padMissingFrames)
      {
        return;
      }

      AppendFrame(frame, tracked);

      var frameCount = _windowFrames.Count;
      if (frameCount == 0)
      {
        return;
      }

      var duration = GetWindowDurationSeconds();
      var fps = Mathf.Max(1f, targetFps);
      var minFrames = Mathf.Max(1, Mathf.Max(minimumFrames, Mathf.CeilToInt(windowSeconds * fps)));
      var targetSeconds = Mathf.Max(windowSeconds, minFrames / fps);

      if (frameCount >= minFrames && duration >= windowSeconds)
      {
        Debug.Log($"GestureValidationController: sending window ({frameCount} frames, {duration:F2}s)");
        SendWindow();
        _windowFrames.Clear();
        _windowTimestamps.Clear();
        _progressStatus = $"Sent window for {_targetGesture} ({duration:F1}s)";
      }
      else
      {
        _progressStatus = $"Collecting {_targetGesture}: {duration:F1}s / {targetSeconds:F1}s";
      }
    }

    private void AppendFrame(HandTrackingSource.HandFrameData frame, bool tracked)
    {
      if (frame == null)
      {
        return;
      }

      var frameData = new float[63];
      if (tracked && frame.landmarks != null)
      {
        for (var i = 0; i < 21 && i < frame.landmarks.Length; i++)
        {
          var lm = frame.landmarks[i];
          var offset = i * 3;
          frameData[offset] = lm.x;
          frameData[offset + 1] = lm.y;
          frameData[offset + 2] = lm.z;
        }
      }

      _windowFrames.Add(frameData);
      _windowTimestamps.Add(frame.timestampMillisec);
    }

    private float GetWindowDurationSeconds()
    {
      if (_windowTimestamps.Count >= 2)
      {
        long first = _windowTimestamps[0];
        long last = _windowTimestamps[_windowTimestamps.Count - 1];
        if (last > first)
        {
          return (last - first) / 1000f;
        }
      }

      return _windowFrames.Count / Mathf.Max(1f, targetFps);
    }

    private void SendWindow()
    {
      var frameCount = _windowFrames.Count;
      if (frameCount == 0)
      {
        return;
      }

      var durationSeconds = GetWindowDurationSeconds();
      var sb = new StringBuilder(frameCount * 512);
      sb.Append("{\"sequence\":[");

      var inv = CultureInfo.InvariantCulture;
      for (var f = 0; f < frameCount; f++)
      {
        var frame = _windowFrames[f];
        sb.Append('[');
        for (var i = 0; i < 21; i++)
        {
          var idx = i * 3;
          sb.Append('[');
          sb.Append(frame[idx].ToString(inv));
          sb.Append(',');
          sb.Append(frame[idx + 1].ToString(inv));
          sb.Append(',');
          sb.Append(frame[idx + 2].ToString(inv));
          sb.Append(']');
          if (i < 20)
          {
            sb.Append(',');
          }
        }
        sb.Append(']');
        if (f < frameCount - 1)
        {
          sb.Append(',');
        }
      }

      sb.Append("],\"frame_count\":");
      sb.Append(frameCount);
      sb.Append(",\"duration\":");
      sb.Append(durationSeconds.ToString(inv));
      sb.Append(",\"target_label\":\"");
      sb.Append(EscapeJsonString(_targetGesture ?? string.Empty));
      sb.Append("\"}\n");

      QueueMessage(sb.ToString());
    }

    private static string EscapeJsonString(string value)
    {
      if (string.IsNullOrEmpty(value))
      {
        return string.Empty;
      }

      return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static float ClampProbability(float value)
    {
      if (float.IsNaN(value) || float.IsInfinity(value))
      {
        return 0f;
      }

      return Mathf.Clamp01(value);
    }

    private void QueueMessage(string message)
    {
      _sendQueue.Enqueue(message);
    }

    private async Task EnsureConnectionAsync(CancellationToken token)
    {
      lock (_connectionLock)
      {
        if (_client != null)
        {
          return;
        }
      }

      var client = new TcpClient();
      using (token.Register(() => client.Close()))
      {
        await client.ConnectAsync(host, port);
      }

      var stream = client.GetStream();
      var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
      var reader = new StreamReader(stream, Encoding.UTF8);

      lock (_connectionLock)
      {
        _client = client;
        _writer = writer;
        _reader = reader;
        _isConnected = true;
      }
    }

    private void RequestStop(string message, bool resetResult = true)
    {
      _stopRequests.Enqueue((message, resetResult));
    }

    private void StopGestureInternal(string message, bool updateStatus, bool resetResult = true)
    {
      if (!_isRunning && !_isConnected)
      {
        if (updateStatus)
        {
          _progressStatus = message;
        }
        return;
      }

      _isRunning = false;
      _targetGesture = null;

      if (_activeButton != null)
      {
        if (_activeButton.triggerButton != null)
        {
          var label = string.IsNullOrWhiteSpace(_activeButton.gestureLabel)
            ? "Test Gesture"
            : $"Test {_activeButton.gestureLabel}";
          _activeButton.triggerButton.SetLabel(label);
        }

        if (_activeButton.statusLabel != null)
        {
          _activeButton.statusLabel.text = message;
          _activeButton.statusLabel.color = message.Contains("reach", StringComparison.OrdinalIgnoreCase)
            ? Color.green
            : Color.white;
        }
      }

      SetButtonInteractable(true);

      try
      {
        if (_isConnected)
        {
          QueueMessage("{\"command\":\"stop\"}\n");
        }
      }
      catch (Exception ex)
      {
        Debug.LogWarning($"GestureValidationController: failed to send stop command - {ex.Message}");
      }

      _cts?.Cancel();

      try
      {
        _senderTask?.Wait(100);
        _receiverTask?.Wait(100);
      }
      catch (AggregateException) { }
      catch (Exception) { }

      lock (_connectionLock)
      {
        _writer?.Dispose();
        _reader?.Dispose();
        _client?.Close();
        _writer = null;
        _reader = null;
        _client = null;
        _isConnected = false;
      }

      _cts?.Dispose();
      _cts = null;
      _senderTask = null;
      _receiverTask = null;

      _windowFrames.Clear();
      _windowTimestamps.Clear();

      if (resetResult)
      {
        _resultStatus = "Result: --";
      }

      if (updateStatus)
      {
        _progressStatus = message;
      }

      _activeButton = null;
    }

    private async Task SenderLoop(CancellationToken token)
    {
      try
      {
        while (!token.IsCancellationRequested)
        {
          if (_sendQueue.TryDequeue(out var message))
          {
            StreamWriter writer;
            lock (_connectionLock)
            {
              writer = _writer;
            }

            if (writer == null)
            {
              await Task.Delay(10, token);
              continue;
            }

            try
            {
              await writer.WriteAsync(message);
            }
            catch (IOException ex)
            {
              Debug.LogError($"GestureValidationController: send failed - {ex.Message}");
              RequestStop("Send failed. Validation stopped.", true);
              return;
            }
          }
          else
          {
            await Task.Delay(5, token);
          }
        }
      }
      catch (OperationCanceledException)
      {
        // expected on cancellation
      }
      catch (Exception ex)
      {
        Debug.LogError($"GestureValidationController: SenderLoop exception - {ex.Message}");
        RequestStop("Sender loop error. Validation stopped.", true);
      }
    }

    private async Task ReceiverLoop(CancellationToken token)
    {
      try
      {
        while (!token.IsCancellationRequested)
        {
          StreamReader reader;
          lock (_connectionLock)
          {
            reader = _reader;
          }

          if (reader == null)
          {
            await Task.Delay(10, token);
            continue;
          }

          string line;
          try
          {
            line = await reader.ReadLineAsync();
          }
          catch (IOException ex)
          {
            Debug.LogError($"GestureValidationController: receive failed - {ex.Message}");
            RequestStop("Connection lost. Validation stopped.", true);
            return;
          }

          if (line == null)
          {
            RequestStop("Python server closed the connection.", true);
            return;
          }

          if (string.IsNullOrWhiteSpace(line))
          {
            continue;
          }

          Debug.Log($"GestureValidationController: received raw -> {line}");

          try
          {
            var result = JsonUtility.FromJson<GestureResult>(line);
            if (result == null)
            {
              continue;
            }

            if (!string.IsNullOrEmpty(_targetGesture))
            {
              var targetProb = ClampProbability(result.prob);
              var probText = $"{targetProb:P0}";
              var top1Matches = result.match;
              if (!top1Matches && !string.IsNullOrEmpty(result.top1))
              {
                top1Matches = string.Equals(result.top1, _targetGesture, StringComparison.OrdinalIgnoreCase);
              }

              var thresholdMet = targetProb >= successProbabilityThreshold &&
                                 result.confidence >= successConfidenceThreshold;

              var top1Display = string.IsNullOrEmpty(result.top1) ? "Unknown" : result.top1;
              _resultStatus = $"Target: {_targetGesture}\nProbability: {probText}\nConfidence: {result.confidence:F2}\nTop-1: {top1Display}";

              if (top1Matches && thresholdMet)
              {
                _resultStatus = $"Gesture reach the standard!\n{_targetGesture} ({probText})";
                RequestStop("Gesture reach the standard.", false);
              }
            }
            else
            {
              var topProb = result.top1_prob > 0f ? result.top1_prob : result.prob;
              var probText = $"{ClampProbability(topProb):P0}";
              var topLabel = string.IsNullOrEmpty(result.top1) ? "Unknown" : result.top1;
              _resultStatus = $"Prediction: {topLabel}\nProbability: {probText}\nConfidence: {result.confidence:F2}";
            }
          }
          catch (Exception ex)
          {
            Debug.LogWarning($"GestureValidationController: failed to parse result - {ex.Message}\nRaw: {line}");
          }
        }
      }
      catch (OperationCanceledException)
      {
        // expected on cancellation
      }
      catch (Exception ex)
      {
        Debug.LogError($"GestureValidationController: ReceiverLoop exception - {ex.Message}");
        RequestStop("Receiver loop error. Validation stopped.", true);
      }
    }
  }
}
