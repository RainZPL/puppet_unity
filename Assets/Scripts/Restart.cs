using UnityEngine;
using UnityEngine.SceneManagement;

public class Restart : MonoBehaviour
{    public void OnButtonClick()
    {
        SceneManager.LoadScene("test3");
    }
}
