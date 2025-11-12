using HandControl;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Restart : MonoBehaviour

{
    public GestureValidationControllerOnnx GestureValidationControllerOnnx;
    public GameLogic GameLogic;
    public void OnButtonClick()
    {
        GestureValidationControllerOnnx.ResetAndStartTesting();
        GameLogic.Pause();
    }
}
