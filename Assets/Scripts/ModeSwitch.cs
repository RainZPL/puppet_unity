using HandControl;
using UnityEngine;
using UnityEngine.UI;

public class ModeSwitch : MonoBehaviour
{
    public Button SwitchButton;
    public GameLogic Logic;
    public GameObject pause;
    public int a = 1;
    public GameObject countdown;
    public GameObject hp;

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
            //gameObject.GetComponent<Animator>().enabled = true;
            hp.SetActive(true);
            countdown.SetActive(false);
        }
        else
        {
            gameObject.GetComponent<FingerRotationDriver>().enabled = true;
            //gameObject.GetComponent<Animator>().enabled = false;
            Time.timeScale = 1.0f;
            hp.SetActive(false);
            countdown.SetActive(true);
        }
    }
}
