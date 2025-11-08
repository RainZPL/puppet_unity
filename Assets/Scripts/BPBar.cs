using Mediapipe;
using UnityEngine;
using UnityEngine.UI;

public class BPBar : MonoBehaviour
{
    public GameLogic GL;

    void Start()
    {

    }

    void Update()
    {
        gameObject.transform.localScale = new Vector3(GL.PlayerHP/100,1,1);
        if (GL.PlayerHP <= 0)
        {
            Destroy(gameObject);
        }
    }
}
