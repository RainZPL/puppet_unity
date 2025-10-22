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
  /// Bridges hand landmarks captured in Unity to an external Python inference server
  /// and displays the predicted gesture returned by that server.
  /// </summary>
  [DefaultExecutionOrder(-4)]
  public class HandInferenceBridge : MonoBehaviour
  {
    [Header("References")]
    [SerializeField] private HandTrackingSource source;
    [SerializeField] private Button toggleButton;
    [SerializeField] private Text progressText;
    [SerializeField] private Text resultText;

    [Header("Network")]
    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 50007;

    [Header("Window Settings")]
    [SerializeField, Tooltip("Seconds of data to collect before sending to Python.")]
    private float windowSeconds = 5f;

    [SerializeField, Tooltip("Minimum frames required before a window is sent.")]
    private int minimumFrames = 60;

    [SerializeField, Tooltip("Expected capture FPS used to estimate window duration.")]
    private float targetFps = 30f;

    [SerializeField, Tooltip("When tracking is lost, append a zero frame to keep timing consistent.")]
    private bool padMissingFrames = true;

    private readonly ConcurrentQueue<string> _sendQueue = new();
    private readonly ConcurrentQueue<(string message, bool resetPrediction)> _stopRequests = new();
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
    private string _progressStatus = "Press Start to begin detection";
    private string _predictionStatus = "Prediction: --";
    private readonly object _connectionLock = new();

    [Serializable]
    private class GestureResult
    {
      public string top1;
      public float prob;
      public float confidence;
    }

    private void Awake()
    {
      if (toggleButton != null)
      {
        toggleButton.onClick.AddListener(ToggleDetection);
        toggleButton.SetLabel("Start Detection");
      }

      ConfigureText(progressText);
      ConfigureText(resultText);

      UpdateStatusText();
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

      StopDetectionInternal("Detection stopped", true);
    }

    private void Update()
    {
      UpdateStatusText();

      if (_stopRequests.TryDequeue(out var request))
      {
        StopDetectionInternal(request.message, true, request.resetPrediction);
      }
    }

    private void UpdateStatusText()
    {
      if (progressText != null)
      {
        progressText.text = _progressStatus;
      }

      if (resultText != null)
      {
        resultText.text = _predictionStatus;
      }
    }

    private void ToggleDetection()
    {
      if (_isRunning)
      {
        RequestStop("Detection stopped", true);
      }
      else
      {
        StartDetection();
      }
    }

    private async void StartDetection()
    {
      if (_isRunning)
      {
        return;
      }

      _cts = new CancellationTokenSource();
      _sendQueue.Clear();
      _stopRequests.Clear();
      _windowFrames.Clear();
      _windowTimestamps.Clear();
      _progressStatus = "Connecting to Python server...";
      _predictionStatus = "Prediction: --";

      try
      {
        await ConnectAsync(_cts.Token);
      }
      catch (Exception ex)
      {
        Debug.LogError($"HandInferenceBridge: failed to connect - {ex.Message}");
        StopDetectionInternal($"Connection failed: {ex.Message}", true);
        return;
      }

      _isRunning = true;
      toggleButton?.SetLabel("Stop Detection");

      _senderTask = Task.Run(() => SenderLoop(_cts.Token), _cts.Token);
      _receiverTask = Task.Run(() => ReceiverLoop(_cts.Token), _cts.Token);
      _progressStatus = "Collecting window...";
      _predictionStatus = "Waiting for prediction...";
    }

    private async Task ConnectAsync(CancellationToken token)
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

    private void RequestStop(string message, bool resetPrediction = true)
    {
      _stopRequests.Enqueue((message, resetPrediction));
    }

    private void StopDetectionInternal(string message, bool updateStatus, bool resetPrediction = true)
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
      toggleButton?.SetLabel("Start Detection");

      try
      {
        if (_isConnected)
        {
          QueueMessage("{\"command\":\"stop\"}\n");
        }
      }
      catch (Exception ex)
      {
        Debug.LogWarning($"HandInferenceBridge: failed to send stop command - {ex.Message}");
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

      _windowFrames.Clear();
      _windowTimestamps.Clear();

      if (resetPrediction)
      {
        _predictionStatus = "Prediction: --";
      }

      if (updateStatus)
      {
        _progressStatus = message;
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
        Debug.Log($"HandInferenceBridge: sending window ({frameCount} frames, {duration:F2}s)");
        SendWindow();
        _windowFrames.Clear();
        _windowTimestamps.Clear();
        _progressStatus = $"Window sent ({duration:F1}s)";
      }
      else
      {
        _progressStatus = $"Collecting window: {duration:F1}s / {targetSeconds:F1}s";
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
      sb.Append("}\n");

      QueueMessage(sb.ToString());
    }

    private void QueueMessage(string message)
    {
      _sendQueue.Enqueue(message);
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
              Debug.LogError($"HandInferenceBridge: send failed - {ex.Message}");
              RequestStop("Send failed, stopping detection", true);
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
        Debug.LogError($"HandInferenceBridge: SenderLoop exception - {ex.Message}");
        RequestStop("Sender loop error, stopping detection", true);
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
            Debug.LogError($"HandInferenceBridge: receive failed - {ex.Message}");
            RequestStop("Connection lost, stopping detection", true);
            return;
          }

          if (line == null)
          {
            RequestStop("Python server closed the connection", true);
            return;
          }

          if (string.IsNullOrWhiteSpace(line))
          {
            continue;
          }

          Debug.Log($"HandInferenceBridge: received raw -> {line}");

          try
          {
            var result = JsonUtility.FromJson<GestureResult>(line);
            if (result != null && !string.IsNullOrEmpty(result.top1))
            {
              _predictionStatus = $"Prediction: {result.top1}\n" +
                                  $"Probability: {result.prob:P0}\n" +
                                  $"Confidence: {result.confidence:F2}";
            }
            else
            {
              _predictionStatus = "Waiting for a confident prediction...";
            }
          }
          catch (Exception ex)
          {
            Debug.LogWarning($"HandInferenceBridge: failed to parse result - {ex.Message}\nRaw: {line}");
          }
        }
      }
      catch (OperationCanceledException)
      {
        // expected on cancellation
      }
      catch (Exception ex)
      {
        Debug.LogError($"HandInferenceBridge: ReceiverLoop exception - {ex.Message}");
        RequestStop("Receiver loop error, stopping detection", true);
      }
    }
  }

  internal static class ButtonExtensions
  {
    public static void SetLabel(this Button button, string text)
    {
      if (button == null)
      {
        return;
      }

      var label = button.GetComponentInChildren<Text>();
      if (label != null)
      {
        label.text = text;
      }
    }
  }
}
