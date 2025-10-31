using UnityEngine;
using UnityEngine.UI;

public class UIControl : MonoBehaviour
{
    public GameObject HandArea;
    public GameObject Menu;
    public GameObject Archive;

    public Camera MainCamera;
    public Camera MenuCamera;

    public Button StartButton;
    public Button EndButton;
    public Button ArchiveButton;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        MenuCamera.enabled = true;
        MainCamera.enabled = false;

        HandArea.SetActive(false);  
        Menu.SetActive(true);
        Archive.SetActive(false);

        StartButton.onClick.AddListener(OnStartButtonClicked);
        EndButton.onClick.AddListener(OnEndButtonClicked);
        ArchiveButton.onClick.AddListener(OnArchiveClicked);
    }

    public void OnStartButtonClicked() 
    {
        MainCamera.enabled = true;
        MenuCamera.enabled = false;
        Archive.SetActive(false);
        HandArea.SetActive(true);
        Menu.SetActive(false);
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
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
