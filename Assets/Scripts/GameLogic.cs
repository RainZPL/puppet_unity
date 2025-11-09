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
    public float PlayerHP = 80;
    public float EnemyHP = 80;
    float Timer = 3;

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
    }

    // Update is called once per frame
    void Update()
    {
        if (UIControl.BegginPlay)
        {
            if (ModeSwitch.a % 2 == 0)
            {
                if (Input.GetKeyDown(KeyCode.P))
                {
                    PlayVideo = true;
                    Time.timeScale = 0;
                }

                FreeEnd = true;
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = true;

                if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().isTimeout)
                {
                    PlayerHP = PlayerHP - Damage;
                    RBAnim.SetTrigger("Attack");

                        PlayVideo = true;

                    if (PlayerHP <= 0)
                    {
                        Win = false;
                        Lost = true;
                    }
                    Time.timeScale = 0;
                    //Debug.Log("timeover");
                }
                else
                {
                    if (GestureValidation.GetComponent<GestureValidationControllerOnnx>().gesturePassed)
                    {
                        EnemyHP = EnemyHP - Damage; // 修复：应该是EnemyHP而不是PlayerHP
                        MKAnim.SetTrigger("fist");

                            PlayVideo = true;

                        if (EnemyHP <= 0) // 修复：应该是检查EnemyHP而不是PlayerHP
                        {
                            Win = true;
                            Lost = false;
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