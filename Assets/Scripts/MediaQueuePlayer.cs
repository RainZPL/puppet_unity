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
    private bool shouldLoopCurrentVideo = true;
    private bool isUIActive = true;
    private VideoClip currentVideoClip;
    private bool wasPlayingBeforeDeactivate = false;
    private bool needsReinitialization = false;
    private RenderTexture dynamicRenderTexture; // 动态创建的RenderTexture

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
        }
    }

    void OnEnable()
    {
        Debug.Log("MediaQueuePlayer OnEnable");

        if (isInitialized)
        {
            needsReinitialization = true;
            StartCoroutine(DelayedReinitialization());
        }
    }

    void OnDisable()
    {
        Debug.Log("MediaQueuePlayer OnDisable");

        if (externalVideoPlayer != null)
        {
            wasPlayingBeforeDeactivate = externalVideoPlayer.isPlaying;
            if (wasPlayingBeforeDeactivate)
            {
                Debug.Log("GameObject禁用，停止视频播放");
            }
            // 完全停止
            externalVideoPlayer.Stop();
        }

        // 停止所有协程
        if (videoPlayCoroutine != null)
        {
            StopCoroutine(videoPlayCoroutine);
            videoPlayCoroutine = null;
        }
    }

    void Update()
    {
        if (!isUIActive || !isInitialized) return;

        if (gestureValidation != null && gestureValidation.currentGestureIndex != targetIndex)
        {
            targetIndex = gestureValidation.currentGestureIndex;
            PlayMedia(targetIndex);
        }
    }

    private IEnumerator DelayedReinitialization()
    {
        // 等待一帧确保所有组件已激活
        yield return new WaitForEndOfFrame();

        if (!isUIActive || !isInitialized || !this.isActiveAndEnabled) yield break;

        Debug.Log("开始延迟重新初始化");

        // 重置视频播放器
        yield return StartCoroutine(ResetVideoPlayerCompletely());

        // 重新播放当前视频
        if (currentIndex >= 0 && currentVideoClip != null)
        {
            yield return StartCoroutine(ReplayCurrentVideo());
        }

        needsReinitialization = false;
    }

    private IEnumerator ResetVideoPlayerCompletely()
    {
        if (externalVideoPlayer == null) yield break;

        Debug.Log("完全重置视频播放器");

        // 停止播放
        externalVideoPlayer.Stop();
        yield return null;

        // 清除clip
        externalVideoPlayer.clip = null;
        yield return null;

        // 重新设置RenderTexture（不销毁原有资产）
        if (useRenderTextureApproach && videoDisplayRawImage != null)
        {
            yield return StartCoroutine(ResetRenderTexture());
        }

        // 重新订阅事件（确保没有丢失）
        SetupVideoPlayerEvents();

        Debug.Log("视频播放器重置完成");
    }

    private IEnumerator ResetRenderTexture()
    {
        Debug.Log("重置RenderTexture");

        // 如果之前有动态创建的RenderTexture，清理它
        if (dynamicRenderTexture != null)
        {
            dynamicRenderTexture.Release();
            dynamicRenderTexture = null;
        }

        // 使用现有的RenderTexture或创建新的动态RenderTexture
        if (renderTexture != null)
        {
            // 使用序列化字段中的RenderTexture
            externalVideoPlayer.targetTexture = renderTexture;
            videoDisplayRawImage.texture = renderTexture;

            // 清除RenderTexture内容
            RenderTexture.active = renderTexture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = null;
        }
        else
        {
            // 创建动态RenderTexture（不会被保存为资产）
            dynamicRenderTexture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            dynamicRenderTexture.name = "DynamicVideoRenderTexture";

            externalVideoPlayer.targetTexture = dynamicRenderTexture;
            videoDisplayRawImage.texture = dynamicRenderTexture;

            // 清除内容
            RenderTexture.active = dynamicRenderTexture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = null;
        }

        // 确保RawImage可见
        videoDisplayRawImage.enabled = true;
        videoDisplayRawImage.color = Color.white;

        yield return null;
    }

    private IEnumerator ReplayCurrentVideo()
    {
        if (externalVideoPlayer == null || currentVideoClip == null) yield break;

        Debug.Log($"重新播放当前视频: {currentVideoClip.name}");

        // 设置视频clip
        externalVideoPlayer.clip = currentVideoClip;
        externalVideoPlayer.isLooping = !useRenderTextureApproach;

        // 准备视频
        externalVideoPlayer.Prepare();
        Debug.Log("视频准备中...");

        // 等待准备完成
        float timeout = Time.realtimeSinceStartup + 5f;
        while (!externalVideoPlayer.isPrepared && Time.realtimeSinceStartup < timeout)
        {
            if (!isUIActive || !this.isActiveAndEnabled) yield break;
            yield return null;
        }

        if (!externalVideoPlayer.isPrepared)
        {
            Debug.LogError("视频准备超时");
            yield break;
        }

        if (!isUIActive || !this.isActiveAndEnabled) yield break;

        // 开始播放
        externalVideoPlayer.Play();
        Debug.Log("视频重新开始播放");

        // 等待一帧确保视频开始
        yield return null;

        // 检查是否真的在播放
        if (!externalVideoPlayer.isPlaying)
        {
            Debug.LogWarning("视频未开始播放，再次尝试");
            externalVideoPlayer.Stop();
            yield return null;
            externalVideoPlayer.Play();
        }

        OnPlaybackStarted?.Invoke(currentIndex);
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

        if (gestureValidation != null)
        {
            targetIndex = gestureValidation.currentGestureIndex;
            if (autoPlay && mediaQueue.Count > 0)
            {
                PlayMedia(targetIndex);
            }
        }
    }

    private void SetupVideoPlayer()
    {
        externalVideoPlayer.playOnAwake = false;
        externalVideoPlayer.waitForFirstFrame = true; // 重要：等待第一帧

        if (useRenderTextureApproach)
        {
            SetupRenderTextureApproach();
        }

        SetupVideoPlayerEvents();
    }

    private void SetupRenderTextureApproach()
    {
        if (renderTexture == null && videoDisplayRawImage != null)
        {
            Debug.LogWarning("RenderTexture未分配，使用动态创建的RenderTexture");
            // 将在播放时动态创建
        }

        if (videoDisplayRawImage != null)
        {
            externalVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoDisplayRawImage.color = Color.white;
            videoDisplayRawImage.enabled = true;
        }

        externalVideoPlayer.audioOutputMode = VideoAudioOutputMode.None;
    }

    private void SetupVideoPlayerEvents()
    {
        // 移除可能存在的旧事件
        externalVideoPlayer.errorReceived -= OnVideoError;
        externalVideoPlayer.loopPointReached -= OnVideoLoopPointReached;
        externalVideoPlayer.prepareCompleted -= OnVideoPrepareCompleted;
        externalVideoPlayer.started -= OnVideoStarted;

        // 添加事件
        externalVideoPlayer.errorReceived += OnVideoError;
        externalVideoPlayer.loopPointReached += OnVideoLoopPointReached;
        externalVideoPlayer.prepareCompleted += OnVideoPrepareCompleted;
        externalVideoPlayer.started += OnVideoStarted;
    }

    private void OnVideoError(VideoPlayer source, string message)
    {
        LogError($"视频播放错误: {message}");
        OnErrorOccurred?.Invoke(message);

        if (isUIActive && isInitialized && this.isActiveAndEnabled)
        {
            StartCoroutine(ReinitializeAfterError());
        }
    }

    private void OnVideoStarted(VideoPlayer source)
    {
        Debug.Log($"视频开始播放: {source.clip?.name}");

        // 确保RawImage可见
        if (videoDisplayRawImage != null)
        {
            videoDisplayRawImage.enabled = true;
            videoDisplayRawImage.color = Color.white;
        }
    }

    private IEnumerator ReinitializeAfterError()
    {
        yield return new WaitForSeconds(0.5f);

        if (isUIActive && isInitialized && currentIndex >= 0 && this.isActiveAndEnabled)
        {
            Debug.Log("错误后尝试重新初始化");
            yield return StartCoroutine(ResetVideoPlayerCompletely());

            if (this.isActiveAndEnabled)
            {
                yield return StartCoroutine(ReplayCurrentVideo());
            }
        }
    }

    private void OnVideoLoopPointReached(VideoPlayer source)
    {
        if (shouldLoopCurrentVideo && isUIActive && isInitialized && this.isActiveAndEnabled)
        {
            if (!useRenderTextureApproach)
            {
                // 标准模式使用内置循环
                return;
            }

            StartCoroutine(RestartVideoAfterDelay(0.1f));
        }
    }

    private IEnumerator RestartVideoAfterDelay(float delay)
    {
        yield return new WaitForSecondsRealtime(delay);

        if (shouldLoopCurrentVideo && isUIActive && isInitialized && this.isActiveAndEnabled)
        {
            if (externalVideoPlayer != null)
            {
                try
                {
                    externalVideoPlayer.Stop();
                    externalVideoPlayer.frame = 0;
                    externalVideoPlayer.Play();
                    Debug.Log("视频循环播放");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"循环播放出错: {e.Message}");
                }
            }
        }
    }

    private void OnVideoPrepareCompleted(VideoPlayer source)
    {
        Debug.Log($"视频准备完成: {source.clip?.name}");
    }

    #region UI激活/禁用控制
    public void SetUIActive(bool active)
    {
        if (isUIActive == active) return;

        isUIActive = active;

        if (active)
        {
            if (isInitialized && currentIndex >= 0 && this.isActiveAndEnabled)
            {
                Debug.Log("UI激活，恢复视频播放");
                StartCoroutine(RestorePlaybackAfterUIEnable());
            }
        }
        else
        {
            Debug.Log("UI禁用，停止视频播放");
            if (externalVideoPlayer != null)
            {
                externalVideoPlayer.Stop();
            }
        }
    }

    private IEnumerator RestorePlaybackAfterUIEnable()
    {
        yield return new WaitForEndOfFrame();

        if (isUIActive && isInitialized && this.isActiveAndEnabled && currentIndex >= 0)
        {
            yield return StartCoroutine(ResetVideoPlayerCompletely());
            yield return StartCoroutine(ReplayCurrentVideo());
        }
    }

    public void SetGameObjectActive(bool active)
    {
        if (this.gameObject.activeSelf == active) return;

        if (active)
        {
            this.gameObject.SetActive(true);
            // OnEnable会处理重新初始化
        }
        else
        {
            wasPlayingBeforeDeactivate = externalVideoPlayer != null && externalVideoPlayer.isPlaying;
            CompleteStopPlayback();
            this.gameObject.SetActive(false);
        }
    }
    #endregion

    private void CompleteStopPlayback()
    {
        shouldLoopCurrentVideo = false;

        if (videoPlayCoroutine != null)
        {
            StopCoroutine(videoPlayCoroutine);
            videoPlayCoroutine = null;
        }

        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.Stop();
        }
    }

    #region 主要播放控制方法
    public bool PlayMedia(int index)
    {
        if (!this.gameObject.activeInHierarchy)
        {
            Debug.LogWarning("GameObject未在层级中激活，无法播放视频");
            return false;
        }

        if (!isUIActive || !isInitialized || !this.isActiveAndEnabled)
        {
            Debug.LogWarning($"播放条件不满足: UI激活={isUIActive}, 初始化={isInitialized}, GameObject激活={this.isActiveAndEnabled}");
            return false;
        }

        if (!ValidateComponents())
        {
            Debug.LogError("组件验证失败");
            return false;
        }

        if (index < 0 || index >= mediaQueue.Count)
        {
            LogError($"索引 {index} 超出范围 (0-{mediaQueue.Count - 1})");
            return false;
        }

        if (index == currentIndex && externalVideoPlayer != null && externalVideoPlayer.isPlaying)
        {
            Debug.Log($"索引未变化，继续播放当前视频: {index}");
            return true;
        }

        CompleteStopPlayback();

        currentIndex = index;
        targetIndex = index;
        MediaItem currentItem = mediaQueue[currentIndex];

        if (currentItem.videoClip == null)
        {
            LogError($"索引 {index} 的视频剪辑为空");
            return false;
        }

        currentVideoClip = currentItem.videoClip;

        if (externalTextDisplay != null)
        {
            externalTextDisplay.text = currentItem.displayText ?? string.Empty;
        }

        shouldLoopCurrentVideo = true;

        Debug.Log($"开始播放视频: {currentVideoClip.name}, 索引: {currentIndex}");

        // 使用协程方式播放（更稳定）
        return PlayMediaWithCoroutine(currentVideoClip);
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
        if (!isUIActive || !isInitialized || !this.isActiveAndEnabled) yield break;

        Debug.Log("开始协程播放流程");

        // 重置播放器状态
        yield return StartCoroutine(ResetVideoPlayerCompletely());

        externalVideoPlayer.clip = clip;
        externalVideoPlayer.isLooping = !useRenderTextureApproach;
        externalVideoPlayer.Prepare();
        Debug.Log("视频准备中...");

        float prepareTimeout = Time.realtimeSinceStartup + 5f;
        while (!externalVideoPlayer.isPrepared && Time.realtimeSinceStartup < prepareTimeout)
        {
            if (!isUIActive || !this.isActiveAndEnabled) yield break;
            yield return null;
        }

        if (!externalVideoPlayer.isPrepared)
        {
            Debug.LogError("视频准备超时");
            yield break;
        }

        if (!isUIActive || !this.isActiveAndEnabled) yield break;

        // 开始播放
        externalVideoPlayer.Play();
        OnMediaChanged?.Invoke(currentIndex);
        OnPlaybackStarted?.Invoke(currentIndex);

        Debug.Log("视频开始播放");

        // 等待一帧确保视频开始
        yield return null;

        // 检查是否真的在播放
        if (!externalVideoPlayer.isPlaying)
        {
            Debug.LogWarning("视频未开始播放，尝试重新开始");
            externalVideoPlayer.Stop();
            yield return null;
            externalVideoPlayer.Play();
        }
    }
    #endregion

    #region 公共方法
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
        if (!isUIActive || !isInitialized) return;
        targetIndex = newIndex;
        PlayMedia(targetIndex);
    }

    public void SetLoopCurrentVideo(bool loop)
    {
        shouldLoopCurrentVideo = loop;
        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.isLooping = loop && !useRenderTextureApproach;
        }
    }

    // 新增：强制重新初始化方法
    public void ForceReinitialize()
    {
        if (isInitialized && this.isActiveAndEnabled)
        {
            StartCoroutine(ResetVideoPlayerCompletely());
            if (currentIndex >= 0)
            {
                StartCoroutine(ReplayCurrentVideo());
            }
        }
    }
    #endregion

    #region 状态检查
    private bool ValidateComponents()
    {
        if (externalVideoPlayer == null)
        {
            Debug.LogError("VideoPlayer为空");
            return false;
        }
        if (externalTextDisplay == null)
        {
            Debug.LogError("TextDisplay为空");
            return false;
        }
        if (useRenderTextureApproach && videoDisplayRawImage == null)
        {
            Debug.LogError("RawImage为空");
            return false;
        }
        return true;
    }

    private void LogError(string message)
    {
        Debug.LogError($"[MediaQueuePlayer] {message}", this);
    }

    public bool IsValid => ValidateComponents() && isInitialized;
    public bool IsPlaying => isUIActive && isInitialized && externalVideoPlayer != null && externalVideoPlayer.isPlaying;
    public int CurrentIndex => currentIndex;
    public bool IsUIActive => isUIActive;
    #endregion

    void OnDestroy()
    {
        CompleteStopPlayback();

        // 清理动态创建的RenderTexture
        if (dynamicRenderTexture != null)
        {
            dynamicRenderTexture.Release();
            dynamicRenderTexture = null;
        }

        if (externalVideoPlayer != null)
        {
            externalVideoPlayer.errorReceived -= OnVideoError;
            externalVideoPlayer.loopPointReached -= OnVideoLoopPointReached;
            externalVideoPlayer.prepareCompleted -= OnVideoPrepareCompleted;
            externalVideoPlayer.started -= OnVideoStarted;
        }
    }
}