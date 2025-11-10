using UnityEngine;
using UnityEngine.Video;
using TMPro;
using System.Collections.Generic;
using HandControl;

public class MediaQueuePlayer : MonoBehaviour
{
    public GestureValidationControllerOnnx gestureValidation;
    [System.Serializable]
    public class MediaItem
    {
        public VideoClip videoClip;
        [TextArea(3, 5)]
        public string displayText;
    }

    [Header("外部组件引用")]
    [SerializeField] private VideoPlayer externalVideoPlayer;
    [SerializeField] private TextMeshProUGUI externalTextDisplay;

    [Header("媒体队列")]
    [SerializeField] private List<MediaItem> mediaQueue = new List<MediaItem>();

    [Header("播放设置")]
    [SerializeField] private bool autoPlay = true;
    [SerializeField] private bool loopVideo = false;
    [SerializeField] private bool validateOnStart = true;

    private int currentIndex = 0;
    private bool isInitialized = false;

    // 事件
    public System.Action<int> OnMediaChanged;
    public System.Action<int> OnPlaybackStarted;
    public System.Action<int> OnPlaybackEnded;
    public System.Action<string> OnErrorOccurred;

    void Start()
    {
        if (validateOnStart)
        {
            Initialize();
            currentIndex = gestureValidation.currentGestureIndex;
        }
    }

    public void Initialize()
    {
        if (isInitialized) return;

        // 验证必要组件
        if (externalVideoPlayer == null)
        {
            LogError("未分配外部VideoPlayer引用！");
            return;
        }

        if (externalTextDisplay == null)
        {
            LogError("未分配外部TextMeshPro引用！");
            return;
        }

        // 配置Video Player
        SetupVideoPlayer();

        isInitialized = true;

        if (autoPlay && mediaQueue.Count > 0)
        {
            PlayMedia(0);
        }
    }

    private void SetupVideoPlayer()
    {
        externalVideoPlayer.playOnAwake = true;
        externalVideoPlayer.isLooping = loopVideo;

        // 注册事件
        externalVideoPlayer.started += OnVideoStarted;
        externalVideoPlayer.loopPointReached += OnVideoEnded;
        externalVideoPlayer.errorReceived += OnVideoError;
    }

    #region 视频事件处理
    private void OnVideoStarted(VideoPlayer source)
    {
        OnPlaybackStarted?.Invoke(currentIndex);
    }

    private void OnVideoEnded(VideoPlayer source)
    {
        OnPlaybackEnded?.Invoke(currentIndex);

        if (!loopVideo && autoPlay)
        {
            PlayNext();
        }
    }

    private void OnVideoError(VideoPlayer source, string message)
    {
        LogError($"视频播放错误: {message}");
        OnErrorOccurred?.Invoke(message);
    }
    #endregion

    #region 主要播放控制方法
    public bool PlayMedia(int index)
    {
        if (!ValidateComponents()) return false;

        if (index < 0 || index >= mediaQueue.Count)
        {
            LogError($"索引 {index} 超出范围 (0-{mediaQueue.Count - 1})");
            return false;
        }

        currentIndex = index;
        MediaItem currentItem = mediaQueue[currentIndex];

        // 停止当前播放
        externalVideoPlayer.Stop();

        // 设置并播放视频
        if (currentItem.videoClip != null)
        {
            externalVideoPlayer.clip = currentItem.videoClip;
            externalVideoPlayer.Play();
        }
        else
        {
            LogError($"索引 {index} 的视频剪辑为空");
        }

        // 设置文本
        externalTextDisplay.text = currentItem.displayText ?? string.Empty;

        OnMediaChanged?.Invoke(currentIndex);
        return true;
    }

    public bool PlayMediaByIndex(object indexInput)
    {
        int index = -1;

        switch (indexInput)
        {
            case int i:
                index = i;
                break;
            case string s when int.TryParse(s, out int parsed):
                index = parsed;
                break;
            case float f:
                index = Mathf.RoundToInt(f);
                break;
            default:
                LogError($"不支持的索引类型: {indexInput.GetType()}");
                return false;
        }

        return PlayMedia(index);
    }
    #endregion

    #region 便捷播放方法
    public bool PlayNext()
    {
        if (mediaQueue.Count == 0) return false;
        int nextIndex = (currentIndex + 1) % mediaQueue.Count;
        return PlayMedia(nextIndex);
    }

    public bool PlayPrevious()
    {
        if (mediaQueue.Count == 0) return false;
        int prevIndex = (currentIndex - 1 + mediaQueue.Count) % mediaQueue.Count;
        return PlayMedia(prevIndex);
    }

    public bool PlayFirst()
    {
        return mediaQueue.Count > 0 && PlayMedia(0);
    }

    public bool PlayLast()
    {
        return mediaQueue.Count > 0 && PlayMedia(mediaQueue.Count - 1);
    }
    #endregion

    #region 播放控制方法
    public void Play()
    {
        if (ValidateComponents() && !externalVideoPlayer.isPlaying)
        {
            externalVideoPlayer.Play();
        }
    }

    public void Pause()
    {
        if (ValidateComponents() && externalVideoPlayer.isPlaying)
        {
            externalVideoPlayer.Pause();
        }
    }

    public void Stop()
    {
        if (ValidateComponents())
        {
            externalVideoPlayer.Stop();
        }
    }

    public void TogglePlayPause()
    {
        if (!ValidateComponents()) return;

        if (externalVideoPlayer.isPlaying)
            externalVideoPlayer.Pause();
        else
            externalVideoPlayer.Play();
    }

    public void SetLooping(bool looping)
    {
        if (ValidateComponents())
        {
            externalVideoPlayer.isLooping = looping;
        }
    }
    #endregion

    #region 队列管理方法
    public void AddMediaItem(VideoClip videoClip, string text, int insertIndex = -1)
    {
        var newItem = new MediaItem { videoClip = videoClip, displayText = text };

        if (insertIndex >= 0 && insertIndex < mediaQueue.Count)
        {
            mediaQueue.Insert(insertIndex, newItem);
        }
        else
        {
            mediaQueue.Add(newItem);
        }
    }

    public bool RemoveMediaItem(int index)
    {
        if (index < 0 || index >= mediaQueue.Count) return false;

        mediaQueue.RemoveAt(index);

        // 调整当前索引
        if (currentIndex >= index)
        {
            currentIndex = Mathf.Max(0, currentIndex - 1);
        }

        return true;
    }

    public void ClearQueue()
    {
        mediaQueue.Clear();
        Stop();
        currentIndex = -1;
    }
    #endregion

    #region 状态检查和工具方法
    private bool ValidateComponents()
    {
        if (externalVideoPlayer == null)
        {
            LogError("VideoPlayer引用丢失");
            return false;
        }

        if (externalTextDisplay == null)
        {
            LogError("TextMeshPro引用丢失");
            return false;
        }

        return true;
    }

    private void LogError(string message)
    {
        Debug.LogError($"[MediaQueuePlayer] {message}", this);
    }

    public bool IsValid => ValidateComponents() && isInitialized;
    public bool IsPlaying => ValidateComponents() && externalVideoPlayer.isPlaying;
    public int CurrentIndex => currentIndex;
    public int QueueCount => mediaQueue.Count;
    public MediaItem CurrentMediaItem =>
        (currentIndex >= 0 && currentIndex < mediaQueue.Count) ? mediaQueue[currentIndex] : null;

    public string GetCurrentStatus()
    {
        if (!ValidateComponents()) return "组件未初始化";

        return $"播放状态: {(IsPlaying ? "播放中" : "停止")}, " +
               $"当前索引: {currentIndex}/{mediaQueue.Count}, " +
               $"循环: {externalVideoPlayer.isLooping}";
    }
    #endregion

    void OnDestroy()
    {
        // 清理事件注册
        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.started -= OnVideoStarted;
            externalVideoPlayer.loopPointReached -= OnVideoEnded;
            externalVideoPlayer.errorReceived -= OnVideoError;
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // 编辑器验证
        if (externalVideoPlayer == null)
        {
            Debug.LogWarning("请分配外部VideoPlayer引用", this);
        }

        if (externalTextDisplay == null)
        {
            Debug.LogWarning("请分配外部TextMeshPro引用", this);
        }
    }
#endif
}