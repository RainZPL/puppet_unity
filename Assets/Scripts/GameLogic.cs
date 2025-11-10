using HandControl;
using UnityEngine;

public class GameLogic : MonoBehaviour
{
    public GameObject MonkeyKing;
    public GameObject RedBoy;

    Animator MKAnim;
    Animator RBAnim;

    public GameObject GestureValidation;
    public ModeSwitch ModeSwitch;
    public UIControl UIControl;
    public bool Win;
    public bool Lost;

    public bool FreeEnd;
    public bool PlayVideo;

    float Damage = 20;
    public float PlayerHP = 100;
    public float EnemyHP = 100;    

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        MKAnim = MonkeyKing.GetComponent<Animator>();
        RBAnim = RedBoy.GetComponent<Animator>();
        FreeEnd = false;
        Time.timeScale = 1;
        PlayVideo = true;
    }

    // Update is called once per frame
    void Update()
    {
        if (UIControl.BegginPlay)
        {
            if (ModeSwitch.a % 2 == 0)
            {
                FreeEnd = true;
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = true;

                if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().isTimeout)
                {
                    PlayerHP = PlayerHP - Damage;
                    RBAnim.SetTrigger("Attack");
                    if (PlayerHP <= 0)
                    {
                        Win = false;
                        Lost = true;
                        PlayVideo = true;
                    }
                    Time.timeScale = 0;
                    //Debug.Log("timeover");
                }
                else
                {
                    if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().gesturePassed)
                    {
                        EnemyHP = PlayerHP - Damage;
                        MKAnim.SetTrigger("fist");
                        if (PlayerHP <= 0)
                        {
                            Win = true;
                            Lost = false;
                            PlayVideo = true;
                        }
                        //Debug.Log("welldone");
                    }
                }
            }
            else
            {
                //Debug.Log("free");
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = false;
            }
        }
        else 
        {
            GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = false;
        }
    }
}
