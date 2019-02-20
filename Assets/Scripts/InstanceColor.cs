using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InstanceColor : MonoBehaviour
{
    static MaterialPropertyBlock propertyBlock;
    static int colorID = Shader.PropertyToID("_Color");
    [SerializeField]
	Color color = Color.white;
    // Start is called before the first frame update
    void Awake ()
    {
		OnValidate();
	}

    void OnValidate ()
    {
        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
		propertyBlock.SetColor(colorID, color);
		GetComponent<MeshRenderer>().SetPropertyBlock(propertyBlock);
	}
}
