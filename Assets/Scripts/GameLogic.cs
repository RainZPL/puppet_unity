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
    public bool Draw;

    public bool FreeEnd;
    public bool PlayVideo;

    float Damage = 20;
    public float PlayerHP = 80;
    public float EnemyHP = 80;
    public float Timer;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        MonkeyKing.transform.position = new Vector3(45.8f, 13.2f, 36.9f);
        RedBoy.transform.position = new Vector3(42f, 13.1f, 36.8f);
        MKAnim = MonkeyKing.GetComponent<Animator>();
        RBAnim = RedBoy.GetComponent<Animator>();
        FreeEnd = false;
        Time.timeScale = 1;
        PlayVideo = true;
        Timer = 3f;
    }

    public void PlayerAttack() 
    {
        EnemyHP = EnemyHP - Damage;
        //MKAnim.SetTrigger("fist");
        MonkeyKing.GetComponent<AudioSource>().GetComponent<AudioSource>().Play();
    }

    public void EnemyAttack() 
    {
        PlayerHP = PlayerHP - Damage;
        RBAnim.SetTrigger("Attack");
        RedBoy.GetComponent<AudioSource>().GetComponent<AudioSource>().Play();
    }

    public void PlayerAnimEnd() 
    {
        PlayVideo = true;
    }

    public void EnemyAnimEnd() 
    {
        PlayVideo = true;
    }

    public void Pause() 
    {
        PlayVideo = true;
        Time.timeScale = 0;
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
                    Pause();
                }

                FreeEnd = true;
                GestureValidation.GetComponent<GestureValidationControllerOnnx>().enabled = true;

                if (PlayerHP <= 0)
                {
                    Win = false;
                    Lost = true;
                    Draw = false;
                    PlayVideo = false;
                    Time.timeScale = 0;
                }
                
                if (EnemyHP <= 0)
                {
                    Win = true;
                    Lost = false;
                    Draw = false;
                    PlayVideo = false;
                    Time.timeScale = 0;
                }
                if (PlayerHP > 0 && EnemyHP > 0 && GestureValidation.GetComponent<GestureValidationControllerOnnx>().currentGestureIndex>4) 
                {
                    Win = false;
                    Lost = false;
                    Draw = true;
                    PlayVideo = false;
                    Time.timeScale = 0;
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