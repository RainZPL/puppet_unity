using HandControl;
using UnityEngine;
using UnityEngine.UI;

public class ModeSwitch : MonoBehaviour
{
    public Button SwitchButton;
    public GameLogic Logic;
    public GameObject pause;
    public int a = 1;

    void OnSwitchButtonClicked() 
    {
        a= a + 1;
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        a= 1;
        SwitchButton.onClick.AddListener( OnSwitchButtonClicked );
    }

    // Update is called once per frame
    void Update()
    {
        if (a % 2 == 0)
        {
            gameObject.GetComponent<FingerRotationDriver>().enabled = false;
            gameObject.GetComponent<Animator>().enabled = true;
            if (Logic.PlayVideo)
            {
                pause.SetActive(true);
                Time.timeScale = 0f;
            }
        }
        else
        {
            gameObject.GetComponent<FingerRotationDriver>().enabled = true;
            gameObject.GetComponent<Animator>().enabled = false;
            pause.SetActive( false );
            Time.timeScale = 1.0f;
        }
    }
}
