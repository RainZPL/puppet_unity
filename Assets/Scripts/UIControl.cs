using HandControl;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

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
    public Button ArchiveButton;
    public GameObject CountDown;
    public GameObject InGameUI;
    public GameObject PauseUI;
    public GameObject HPbar;

    public GameLogic GameLogic;
    public GestureValidationControllerOnnx GestureValidation;

    public bool BegginPlay;

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
        ArchiveButton.onClick.AddListener(OnArchiveClicked);

        BegginPlay = false;
    }

    public void OnStartButtonClicked() 
    {
        //Debug.Log("hello");
        MainCamera.enabled = true;
        MenuCamera.enabled = false;
        Archive.SetActive(false);
        HandArea.SetActive(true);
        Menu.SetActive(false);
        BegginPlay = true;
        MonkeyKing.transform.position = new Vector3(44.91f, 6.53f, 22.68f);
        RedBoy.transform.position = new Vector3(38.86f, 6.58f, 23.85f);
    }

    public void OnEndButtonClicked()
    {
        Application.Quit();
    }

    public void OnArchiveClicked() 
    {
        HandArea.SetActive(false);
        Menu.SetActive(false);
        Archive.SetActive(true);
        BegginPlay = false;
    }

    // Update is called once per frame
    void Update()
    {
        //Debug.Log(CountDown.GetComponent<TextMeshProUGUI>());
        //Debug.Log(GameLogic);

        if (BegginPlay) 
        {
            InGameUI.SetActive(true);
            if (GameLogic.FreeEnd)
            {
                HPbar.SetActive(true);
                CountDown.GetComponent<TextMeshProUGUI>().text = "Attack Time" + " " + GestureValidation.DeltaTime.ToString();
                if (GameLogic.PlayVideo) 
                {
                    PauseUI.SetActive(true);
                    GameLogic.Timer = 3;
                }
            }
            else
            {
                CountDown.GetComponent<TextMeshProUGUI>().text = "FreeMode Time";
            }

            if (GameLogic.Win) 
            {
                Time.timeScale = 0;
                WinPannel.SetActive(true);
            }
            if (GameLogic.Lost) 
            {
                Time.timeScale = 0;
                LostPannel.SetActive(true);
            }
            if (GameLogic.Draw) 
            {
                Time.timeScale = 0;
                DrawPannel.SetActive(true);
            }
        }
    }
}
