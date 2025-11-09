using HandControl;
using UnityEngine;

public class Skip : MonoBehaviour
{
    public GameObject PauseUI;
    public GameLogic GameLogic;
    public GestureValidationControllerOnnx GestureValidationControllerOnnx;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void OnButtonClick() 
    {
        PauseUI.SetActive(false);
        Time.timeScale = 1.0f;
        GameLogic.PlayVideo = false;
        GestureValidationControllerOnnx.SkipCurrentGesture();
    }
}
