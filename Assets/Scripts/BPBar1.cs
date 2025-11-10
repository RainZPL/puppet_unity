using UnityEngine;
using UnityEngine.UI;

public class BPBar1 : MonoBehaviour
{
    public GameLogic GL1;

    void Start()
    {

    }

    void Update()
    {
        gameObject.transform.localScale = new Vector3(GL1.EnemyHP/100,1,1);
        if (GL1.EnemyHP<=0) 
        {
            Destroy(gameObject);
        }
    }
}
