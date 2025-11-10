using HandControl;
using UnityEngine;
using TMPro;

public class GameLogic : MonoBehaviour
{
    public GameObject MonkeyKing;
    public GameObject RedBoy;

    Animator MKAnim;
    Animator RBAnim;

    public GameObject GestureValidation;

    public TMP_Text tMP_Text1;
    
    public TMP_Text tMP_Text2;
    public ModeSwitch ModeSwitch;
    public UIControl UIControl;
    public bool Win;
    public bool Lost;
    public bool Draw;

    public bool FreeEnd;
    public bool PlayVideo;

    float Damage = 20;
    public float PlayerHP = 80;
    public float EnemyHP = 80;
    public float Timer;
    public float startTimer = 0.1f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        MonkeyKing.transform.position = new Vector3(46.3f, 13.2f, 36.9f);
        RedBoy.transform.position = new Vector3(41.6f, 13.1f, 36.8f);
        MKAnim = MonkeyKing.GetComponent<Animator>();
        RBAnim = RedBoy.GetComponent<Animator>();
        FreeEnd = false;
        Time.timeScale = 1;
        PlayVideo = true;
        Timer = 3f;
        startTimer = 0.1f;
    }

    // Update is called once per frame
    void Update()
    {
        if (UIControl.BegginPlay)
        {
            if (ModeSwitch.a % 2 == 0)
            {
                tMP_Text1.enabled = true;
                tMP_Text2.enabled = true;

                if (Input.GetKeyDown(KeyCode.P))
                {
                    PlayVideo = true;
                    Time.timeScale = 0;
                }

                FreeEnd = true;
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = true;
                if(startTimer!=0)
                startTimer = startTimer-Time.deltaTime;
                if (startTimer < 0) 
                {
                    Time.timeScale = 0;
                    startTimer = 0;
                }


                if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().isTimeout)
                {
                    //Debug.Log("rbtimeout");
                    if (Timer == 3f)
                    {
                        //Debug.Log("RBATTACK");
                        PlayerHP = PlayerHP - Damage;
                        RBAnim.SetTrigger("Attack");
                        RedBoy.GetComponent<AudioSource>().GetComponent<AudioSource>().Play();
                    }

                    Timer = Timer - Time.deltaTime;
                    if (Timer < 0)
                    {
                        PlayVideo = true;
                        Timer = 3f;
                        //Debug.Log("timeover");
                    }
                }


                    if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().gesturePassed)
                    {
                        if (Timer == 3f)
                        {
                            EnemyHP = EnemyHP - Damage;
                            //MKAnim.SetTrigger("fist");
                            MonkeyKing.GetComponent<AudioSource>().GetComponent<AudioSource>().Play();
                        }


                        if (Timer < 0)
                        {
                            PlayVideo = true;
                            Timer = 3f;
                            //Debug.Log("welldone");
                        }
                        Timer = Timer - Time.deltaTime;
                    }



                if (PlayerHP <= 0)
                {
                    Win = false;
                    Lost = true;
                    Draw = false;
                    Time.timeScale = 0;
                }
                
                if (EnemyHP <= 0)
                {
                    Win = true;
                    Lost = false;
                    Draw = false;
                    Time.timeScale = 0;
                }
                if (PlayerHP > 0 && EnemyHP > 0 && GestureValidation.GetComponent<GestureValidationControllerOnnx>().currentGestureIndex>4) 
                {
                    Win = false;
                    Lost = false;
                    Draw = true;
                    Time.timeScale = 0;
                }

            }
            else
            {
                //Debug.Log("free");
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = false;
                tMP_Text1.enabled = false;
                tMP_Text2.enabled = false;
                
            }
        }
        else
        {
            GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = false;
        }
    }
}