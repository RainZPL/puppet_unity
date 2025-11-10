using UnityEngine;
using UnityEngine.SceneManagement;

public class Back : MonoBehaviour
{
    public GameObject detailPannel;
    public GameObject frontPannel;

    public GameObject audiosource;
    public void OnButtonClick()
    {
        detailPannel.SetActive(false);
        frontPannel.SetActive(true);
        audiosource.SetActive(true);
        Time.timeScale = 1.0f;
    }
}
