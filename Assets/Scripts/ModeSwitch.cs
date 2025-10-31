using HandControl;
using UnityEngine;
using UnityEngine.UI;

public class ModeSwitch : MonoBehaviour
{
    public Button Button;
    int a = 0;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Button.onClick.AddListener(OnButtonClicked);
    }

    public void OnButtonClicked() 
    {
        a = a + 1;
    }

    // Update is called once per frame
    void Update()
    {
        if (a % 2 == 0)
        {
            gameObject.GetComponent<FingerRotationDriver>().enabled = false;
            gameObject.GetComponent<Animator>().enabled = true;
        }
        else
        {
            gameObject.GetComponent<FingerRotationDriver>().enabled = true;
            gameObject.GetComponent<Animator>().enabled = false;
        }
    }
}
