using UnityEngine;
using UnityEngine.Video;
using TMPro;
using UnityEngine.UI;
using System.Collections;
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
    [SerializeField] private RawImage videoDisplayRawImage;
    [SerializeField] private RenderTexture renderTexture;

    [Header("媒体队列")]
    [SerializeField] private List<MediaItem> mediaQueue = new List<MediaItem>();

    [Header("播放设置")]
    [SerializeField] private bool autoPlay = true;
    [SerializeField] private bool validateOnStart = true;
    [SerializeField] private bool useRenderTextureApproach = true;

    private int currentIndex = -1;
    private bool isInitialized = false;
    private Coroutine videoPlayCoroutine;
    private int targetIndex = -1;
    private bool shouldLoopCurrentVideo = true; // 控制是否循环当前视频

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
            if (gestureValidation != null)
            {
                targetIndex = gestureValidation.currentGestureIndex;
                PlayMedia(targetIndex);
            }
        }
    }

    void Update()
    {
        // 监听外部index变化
        if (gestureValidation != null && gestureValidation.currentGestureIndex != targetIndex)
        {
            if (gestureValidation.currentGestureIndex < 5) 
            {
                targetIndex = gestureValidation.currentGestureIndex;
            }

            PlayMedia(targetIndex);
        }
    }

    public void Initialize()
    {
        if (isInitialized) return;

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

        SetupVideoPlayer();
        isInitialized = true;
    }

    private void SetupVideoPlayer()
    {
        externalVideoPlayer.playOnAwake = false;

        if (useRenderTextureApproach && renderTexture != null && videoDisplayRawImage != null)
        {
            externalVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
            externalVideoPlayer.targetTexture = renderTexture;
            videoDisplayRawImage.texture = renderTexture;
            externalVideoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        }

        externalVideoPlayer.errorReceived += OnVideoError;
        externalVideoPlayer.loopPointReached += OnVideoLoopPointReached;
    }

    private void OnVideoError(VideoPlayer source, string message)
    {
        LogError($"视频播放错误: {message}");
        OnErrorOccurred?.Invoke(message);
    }

    private void OnVideoLoopPointReached(VideoPlayer source)
    {
        // 视频播放完成，检查是否需要循环
        if (shouldLoopCurrentVideo && externalVideoPlayer.isPlaying)
        {
            // 重新开始播放当前视频
            externalVideoPlayer.frame = 0;
            externalVideoPlayer.Play();
            Debug.Log("视频循环播放");
        }
    }

    #region 主要播放控制方法
    public bool PlayMedia(int index)
    {
        if (!ValidateComponents()) return false;

        if (index < 0 || index >= mediaQueue.Count)
        {
            LogError($"索引 {index} 超出范围 (0-{mediaQueue.Count - 1})");
            return false;
        }

        // 如果索引没有变化，且正在播放，则继续循环当前视频
        if (index == currentIndex && externalVideoPlayer.isPlaying)
        {
            Debug.Log($"索引未变化，继续播放当前视频: {index}");
            return true;
        }

        // 停止当前播放
        StopCurrentPlayback();

        currentIndex = index;
        targetIndex = index;
        MediaItem currentItem = mediaQueue[currentIndex];

        if (currentItem.videoClip == null)
        {
            LogError($"索引 {index} 的视频剪辑为空");
            return false;
        }

        // 设置文本
        externalTextDisplay.text = currentItem.displayText ?? string.Empty;

        // 开始播放
        if (useRenderTextureApproach)
        {
            return PlayMediaWithCoroutine(currentItem.videoClip);
        }
        else
        {
            return PlayMediaStandard(currentItem.videoClip);
        }
    }

    private bool PlayMediaStandard(VideoClip clip)
    {
        externalVideoPlayer.clip = clip;
        externalVideoPlayer.isLooping = true; // 启用VideoPlayer内置循环
        externalVideoPlayer.Play();

        OnMediaChanged?.Invoke(currentIndex);
        OnPlaybackStarted?.Invoke(currentIndex);

        Debug.Log($"开始播放视频: {clip.name}, 启用循环");
        return true;
    }

    private bool PlayMediaWithCoroutine(VideoClip clip)
    {
        if (videoPlayCoroutine != null)
            StopCoroutine(videoPlayCoroutine);

        videoPlayCoroutine = StartCoroutine(PlayVideoCoroutine(clip));
        return true;
    }

    private IEnumerator PlayVideoCoroutine(VideoClip clip)
    {
        externalVideoPlayer.clip = clip;
        externalVideoPlayer.isLooping = false; // 禁用内置循环，由我们控制
        externalVideoPlayer.Prepare();

        // 等待视频准备完成
        while (!externalVideoPlayer.isPrepared)
        {
            yield return null;
        }

        externalVideoPlayer.Play();
        OnMediaChanged?.Invoke(currentIndex);
        OnPlaybackStarted?.Invoke(currentIndex);

        Debug.Log($"开始播放视频: {clip.name}, 使用协程控制循环");

        int currentPlayIndex = currentIndex; // 保存当前播放索引
        shouldLoopCurrentVideo = true; // 允许循环

        while (shouldLoopCurrentVideo)
        {
            // 检查外部索引是否变化
            if (gestureValidation != null && gestureValidation.currentGestureIndex != currentPlayIndex)
            {
                Debug.Log($"检测到索引变化，停止循环: {currentPlayIndex} -> {gestureValidation.currentGestureIndex}");
                break;
            }

            // 检查是否播放完成
            if (externalVideoPlayer.isPlaying &&
                externalVideoPlayer.frame > 0 &&
                externalVideoPlayer.frame >= (long)externalVideoPlayer.frameCount - 5)
            {
                Debug.Log("视频播放完成，重新开始循环");
                // 重新开始播放
                externalVideoPlayer.frame = 0;
                externalVideoPlayer.Play();

                // 短暂延迟，避免频繁检测
                yield return new WaitForSeconds(0.1f);
            }

            yield return null;
        }

        // 如果是因为索引变化而退出，播放新视频
        if (gestureValidation != null && gestureValidation.currentGestureIndex != currentPlayIndex)
        {
            Debug.Log($"切换到新视频: {gestureValidation.currentGestureIndex}");
            PlayMedia(gestureValidation.currentGestureIndex);
        }
    }

    private void StopCurrentPlayback()
    {
        shouldLoopCurrentVideo = false; // 停止循环

        if (videoPlayCoroutine != null)
        {
            StopCoroutine(videoPlayCoroutine);
            videoPlayCoroutine = null;
        }

        if (externalVideoPlayer.isPlaying || externalVideoPlayer.isPrepared)
        {
            externalVideoPlayer.Stop();
            OnPlaybackEnded?.Invoke(currentIndex);
        }
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

    public void SetTargetIndex(int newIndex)
    {
        targetIndex = newIndex;
        PlayMedia(targetIndex);
    }
    #endregion

    #region 播放控制方法
    public void Play()
    {
        if (ValidateComponents() && !externalVideoPlayer.isPlaying && currentIndex >= 0)
        {
            externalVideoPlayer.Play();
            shouldLoopCurrentVideo = true;
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
        StopCurrentPlayback();
        currentIndex = -1;
        targetIndex = -1;
    }

    public void TogglePlayPause()
    {
        if (!ValidateComponents()) return;

        if (externalVideoPlayer.isPlaying)
        {
            externalVideoPlayer.Pause();
            shouldLoopCurrentVideo = false;
        }
        else
        {
            externalVideoPlayer.Play();
            shouldLoopCurrentVideo = true;
        }
    }

    // 设置是否循环当前视频
    public void SetLoopCurrentVideo(bool loop)
    {
        shouldLoopCurrentVideo = loop;
        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.isLooping = loop && !useRenderTextureApproach;
        }
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

        if (useRenderTextureApproach && (renderTexture == null || videoDisplayRawImage == null))
        {
            LogError("RenderTexture方案需要RenderTexture和RawImage引用");
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
    public int TargetIndex => targetIndex;
    public int QueueCount => mediaQueue.Count;
    public bool IsLooping => shouldLoopCurrentVideo;
    public MediaItem CurrentMediaItem =>
        (currentIndex >= 0 && currentIndex < mediaQueue.Count) ? mediaQueue[currentIndex] : null;

    public string GetCurrentStatus()
    {
        if (!ValidateComponents()) return "组件未初始化";

        return $"播放状态: {(IsPlaying ? "播放中" : "停止")}, " +
               $"当前索引: {currentIndex}, " +
               $"目标索引: {targetIndex}, " +
               $"循环: {shouldLoopCurrentVideo}, " +
               $"帧: {externalVideoPlayer.frame}/{externalVideoPlayer.frameCount}";
    }
    #endregion

    void OnDestroy()
    {
        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.errorReceived -= OnVideoError;
            externalVideoPlayer.loopPointReached -= OnVideoLoopPointReached;
        }

        if (videoPlayCoroutine != null)
        {
            StopCoroutine(videoPlayCoroutine);
        }
    }
}