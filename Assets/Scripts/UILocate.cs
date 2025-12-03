using UnityEngine;
using UnityEngine.UI;

public class UILocate : MonoBehaviour
{
    [SerializeField] private CanvasScaler canvasScaler;

    public Vector2 referenceResolution = new Vector2(1920, 1080);

    public float matchWidthOrHeight = 0f;

    void Start()
    {
        if (canvasScaler == null)
            canvasScaler = GetComponent<CanvasScaler>();

        SetupCanvasScaler();
        AdjustUIForAspectRatio();
    }

    void SetupCanvasScaler()
    {
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = referenceResolution;
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        canvasScaler.matchWidthOrHeight = matchWidthOrHeight;
    }

    void AdjustUIForAspectRatio()
    {
        float currentAspect = (float)Screen.width / Screen.height;
        float designAspect = referenceResolution.x / referenceResolution.y;

        if (currentAspect > designAspect)
        {
            canvasScaler.matchWidthOrHeight = 1f;
        }
        else
        {
            canvasScaler.matchWidthOrHeight = 0f;
        }
    }

    void Update()
    {
        if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
        {
            AdjustUIForAspectRatio();
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
        }
    }

    private int lastScreenWidth;
    private int lastScreenHeight;
}