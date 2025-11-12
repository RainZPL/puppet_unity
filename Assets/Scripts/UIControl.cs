using HandControl;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

public class UIControl : MonoBehaviour
{
    public GameObject HandArea;
    public GameObject Menu;
    public GameObject Archive;
    public GameObject WinPannel;
    public GameObject LostPannel;
    public GameObject DrawPannel;
    public GameObject MonkeyKing;
    public GameObject RedBoy;

    public Camera MainCamera;
    public Camera MenuCamera;

    public Button StartButton;
    public Button EndButton;
    public GameObject CountDown;
    public GameObject InGameUI;
    public GameObject PauseUI;
    public GameObject HPbar;

    public GameLogic GameLogic;
    public GestureValidationControllerOnnx GestureValidation;

    public bool BegginPlay;
    bool isskiped = false;
    public float skiptimer = 0.1f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        MenuCamera.enabled = true;
        MainCamera.enabled = false;
        CountDown.SetActive(true);

        HandArea.SetActive(false);  
        Menu.SetActive(true);
        Archive.SetActive(false);
        WinPannel.SetActive(false);
        LostPannel.SetActive(false);
        DrawPannel.SetActive(false);
        InGameUI.SetActive(false);
        PauseUI.SetActive(false);
        HPbar.SetActive(false);

        StartButton.onClick.AddListener(OnStartButtonClicked);
        EndButton.onClick.AddListener(OnEndButtonClicked);

        BegginPlay = false;
    }

    public void OnStartButtonClicked() 
    {
        //Debug.Log("hello");
        MainCamera.enabled = true;
        MenuCamera.enabled = false;
        Archive.SetActive(true);
        HandArea.SetActive(true);
        Menu.SetActive(false);
        InGameUI.SetActive(false);
        BegginPlay = true;
        MonkeyKing.transform.position = new Vector3(44.91f, 6.53f, 22.68f);
        RedBoy.transform.position = new Vector3(38.86f, 6.58f, 23.85f);
        Time.timeScale = 0;
    }

    public void OnEndButtonClicked()
    {
        Application.Quit();
    }

    public void OnSkip() 
    {
        isskiped = true;
    }

    public void Restart() 
    {
        WinPannel.SetActive(false);
        LostPannel.SetActive(false);
        DrawPannel.SetActive(false);
    }

    public void Win() 
    {
        Time.timeScale = 0;
        WinPannel.SetActive(true);
        PauseUI.SetActive(false);
    }
    public void Lost()
    {
        Time.timeScale = 0;
        LostPannel.SetActive(true);
        PauseUI.SetActive(false);
    }
    public void Draw()
    {
        Time.timeScale = 0;
        DrawPannel.SetActive(true);
        PauseUI.SetActive(false);
    }

    // Update is called once per frame
    void Update()
    {
        //Debug.Log(CountDown.GetComponent<TextMeshProUGUI>());
        //Debug.Log(GameLogic);

        if (BegginPlay) 
        {
            if (GameLogic.FreeEnd)
            {
                HPbar.SetActive(true);
                CountDown.GetComponent<TextMeshProUGUI>().text = "Gesture Codex";
                if (GameLogic.PlayVideo) 
                {
                    //PauseUI.GetComponent<VideoPlayer>().playOnAwake = true;
                    PauseUI.SetActive(true);

                    GameLogic.Timer = 3;
                    Time.timeScale = 0;
                }
            }
            else
            {
                CountDown.GetComponent<TextMeshProUGUI>().text = "Freeflow Play";
            }
        }
    }
}
