using UnityEngine;

public class Continue : MonoBehaviour
{
    public GameObject PauseUI;
    public GameLogic GameLogic;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void OnButtonClick()
    {
        PauseUI.SetActive(false);
        Time.timeScale = 1.0f;
        GameLogic.PlayVideo = false;
    }
}
