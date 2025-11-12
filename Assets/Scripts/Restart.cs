using HandControl;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Restart : MonoBehaviour

{
    public GestureValidationControllerOnnx GestureValidationControllerOnnx;
    public GameLogic GameLogic;
    public UIControl UIControl;
    public void OnButtonClick()
    {
        Time.timeScale = 1;
        GestureValidationControllerOnnx.ResetAndStartTesting();
        GameLogic.Pause();
        UIControl.Restart();
    }
}
