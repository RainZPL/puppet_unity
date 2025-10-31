using System;
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
  // quick-and-dirty version, pretty loose structure on purpose
  public class GestureValidationControllerBasic : MonoBehaviour
  {
    [Serializable]
    public class ButtonInfo
    {
      public string myLabel;
      public Button myButton;
      public Text myStatus;
    }

    public HandTrackingSource handSource;
    public Text progressText;
    public Text resultText;
    public List<ButtonInfo> buttons = new List<ButtonInfo>();

    public string serverHost = "127.0.0.1";
    public int serverPort = 50007;
    public float windowSeconds = 5f;
    public int minFrames = 60;
    public float fakeFps = 30f;
    public bool fillMissing = true;
    public float needProb = 0.75f;
    public float needConf = 0.5f;

    private readonly List<float[]> frameList = new List<float[]>();
    private readonly List<long> frameTimes = new List<long>();
    private TcpClient sock;
    private StreamReader sockReader;
    private StreamWriter sockWriter;
    private CancellationTokenSource listenStop;
    private Task listenJob;
    private string activeLabel;
    private ButtonInfo activeButton;
    private string progressMsg = "Not started.";
    private string resultMsg = "Result: --";
    private bool isCollecting;

    [Serializable]
    private class ModelAnswer
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
      foreach (var info in buttons)
      {
        if (info == null || info.myButton == null)
        {
          continue;
        }

        var localInfo = info;
        info.myButton.onClick.AddListener(() => OnButtonHit(localInfo));
        UpdateButtonLabel(info, false);

        if (info.myStatus != null)
        {
          info.myStatus.text = "Not started";
          info.myStatus.color = Color.white;
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

    private void Update()
    {
      UpdateUi();
    }

    private void OnButtonHit(ButtonInfo info)
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

    private void StartCollect(ButtonInfo info)
    {
      if (string.IsNullOrWhiteSpace(info.myLabel))
      {
        Debug.LogWarning("Target label is empty.");
        return;
      }

      if (handSource == null)
      {
        Debug.LogWarning("HandTrackingSource is missing.");
        return;
      }

      activeButton = info;
      activeLabel = info.myLabel.Trim();
      isCollecting = true;

      foreach (var item in buttons)
      {
        if (item?.myButton != null)
        {
          item.myButton.interactable = false;
        }
      }

      if (info.myButton != null)
      {
        info.myButton.interactable = true;
        UpdateButtonLabel(info, true);
      }

      if (info.myStatus != null)
      {
        info.myStatus.text = "Collecting...";
        info.myStatus.color = Color.yellow;
      }

      frameList.Clear();
      frameTimes.Clear();
      progressMsg = "Collecting window...";
      resultMsg = "Waiting for server...";

      if (!ConnectServer())
      {
        StopCollect("Connect failed", true);
        return;
      }

      StartListenLoop();
    }

    private bool ConnectServer()
    {
      try
      {
        sock = new TcpClient();
        sock.Connect(serverHost, serverPort);
        var net = sock.GetStream();
        sockWriter = new StreamWriter(net, new UTF8Encoding(false)) { AutoFlush = true };
        sockReader = new StreamReader(net, Encoding.UTF8);
        return true;
      }
      catch (Exception e)
      {
        Debug.LogError("Failed to reach Python: " + e.Message);
        return false;
      }
    }

    private void StartListenLoop()
    {
      listenStop = new CancellationTokenSource();
      listenJob = Task.Run(() => ListenLoop(listenStop.Token), listenStop.Token);
    }

    private void ListenLoop(CancellationToken token)
    {
      while (!token.IsCancellationRequested)
      {
        string line;
        try
        {
          line = sockReader?.ReadLine();
        }
        catch (IOException)
        {
          StopCollect("Connection lost", true);
          break;
        }

        if (string.IsNullOrEmpty(line))
        {
          Thread.Sleep(10);
          continue;
        }

        Debug.Log("Python says: " + line);

        try
        {
          var answer = JsonUtility.FromJson<ModelAnswer>(line);
          if (answer == null)
          {
            continue;
          }

          if (!string.IsNullOrEmpty(activeLabel))
          {
            var prob = ClampProb(answer.prob);
            var ok = prob >= needProb && answer.confidence >= needConf;
            var msg = new StringBuilder();
            msg.AppendLine("Target: " + activeLabel);
            msg.AppendLine("Probability: " + prob.ToString("P0", CultureInfo.InvariantCulture));
            msg.AppendLine("Confidence: " + answer.confidence.ToString("F2", CultureInfo.InvariantCulture));
            msg.Append("Top1: " + (string.IsNullOrEmpty(answer.top1) ? "Unknown" : answer.top1));
            resultMsg = msg.ToString();

            if ((answer.match || string.Equals(answer.top1, activeLabel, StringComparison.OrdinalIgnoreCase)) && ok)
            {
              resultMsg = "Gesture passed: " + activeLabel;
              StopCollect("Finished", false);
            }
          }
          else
          {
            var top = answer.top1_prob > 0 ? answer.top1_prob : answer.prob;
            resultMsg = "Prediction: " + answer.top1 + " Prob: " + ClampProb(top).ToString("P0", CultureInfo.InvariantCulture);
          }
        }
        catch (Exception ex)
        {
          Debug.LogWarning("Failed to parse: " + ex.Message);
        }
      }
    }

    private void HandleHandFrame(HandTrackingSource.HandFrameData data)
    {
      if (!isCollecting || sockWriter == null)
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
        SendWindowNow();
        frameList.Clear();
        frameTimes.Clear();
        progressMsg = "Sent window: " + activeLabel;
      }
      else
      {
        progressMsg = $"Collecting {activeLabel}: {seconds:F1}s / {windowSeconds:F1}s";
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

    private void SendWindowNow()
    {
      if (sockWriter == null)
      {
        return;
      }

      var count = frameList.Count;
      var sb = new StringBuilder();
      sb.Append("{\"sequence\":[");

      var culture = CultureInfo.InvariantCulture;
      for (var f = 0; f < count; f++)
      {
        var frm = frameList[f];
        sb.Append('[');
        for (var i = 0; i < 21; i++)
        {
          var idx = i * 3;
          sb.Append('[');
          sb.Append(frm[idx].ToString(culture));
          sb.Append(',');
          sb.Append(frm[idx + 1].ToString(culture));
          sb.Append(',');
          sb.Append(frm[idx + 2].ToString(culture));
          sb.Append(']');
          if (i < 20)
          {
            sb.Append(',');
          }
        }
        sb.Append(']');
        if (f < count - 1)
        {
          sb.Append(',');
        }
      }

      sb.Append("],\"frame_count\":");
      sb.Append(count);
      sb.Append(",\"duration\":");
      sb.Append(CalcSeconds().ToString(culture));
      sb.Append(",\"target_label\":\"");
      sb.Append(SimpleEscape(activeLabel ?? string.Empty));
      sb.Append("\"}\n");

      try
      {
        sockWriter.Write(sb.ToString());
      }
      catch (IOException ex)
      {
        Debug.LogError("Send failed: " + ex.Message);
        StopCollect("Send error", true);
      }
    }

    private void StopCollect(string info, bool resetResult)
    {
      if (!isCollecting && sock == null)
      {
        progressMsg = info;
        return;
      }

      isCollecting = false;

      if (activeButton != null && activeButton.myButton != null)
      {
        UpdateButtonLabel(activeButton, false);
      }

      if (activeButton != null && activeButton.myStatus != null)
      {
        activeButton.myStatus.text = info;
        activeButton.myStatus.color = info.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0 ? Color.green : Color.white;
      }

      foreach (var item in buttons)
      {
        if (item?.myButton != null)
        {
          item.myButton.interactable = true;
        }
      }

      try
      {
        sockWriter?.Write("{\"command\":\"stop\"}\n");
      }
      catch (Exception) { }

      listenStop?.Cancel();
      try
      {
        listenJob?.Wait(100);
      }
      catch (Exception) { }

      sockReader?.Dispose();
      sockWriter?.Dispose();
      sock?.Close();

      sockReader = null;
      sockWriter = null;
      sock = null;

      frameList.Clear();
      frameTimes.Clear();

      if (resetResult)
      {
        resultMsg = "Result: --";
      }

      progressMsg = info;
      activeLabel = null;
      activeButton = null;
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

    private static string SimpleEscape(string value)
    {
      if (string.IsNullOrEmpty(value))
      {
        return string.Empty;
      }

      return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static float ClampProb(float v)
    {
      if (float.IsNaN(v) || float.IsInfinity(v))
      {
        return 0f;
      }

      return Mathf.Clamp01(v);
    }

    private void UpdateButtonLabel(ButtonInfo info, bool running)
    {
      if (info?.myButton == null)
      {
        return;
      }

      var label = string.IsNullOrWhiteSpace(info.myLabel) ? "Test Gesture" : info.myLabel;
      var txt = info.myButton.GetComponentInChildren<Text>();
      if (txt != null)
      {
        txt.text = running ? ("Stop " + label) : ("Test " + label);
      }
    }
  }
}
