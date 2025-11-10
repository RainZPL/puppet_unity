using UnityEngine;
using UnityEngine.SceneManagement;

public class Back : MonoBehaviour
{
    public GameObject detailPannel;
    public GameObject frontPannel;
    public void OnButtonClick()
    {
        detailPannel.SetActive(false);
        frontPannel.SetActive(true);
    }
}
