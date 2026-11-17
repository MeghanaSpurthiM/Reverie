using UnityEngine;
using UnityEngine.UI;

/// Attach anywhere. Wires a UI Toggle to show/hide a target GameObject.
public class GameObjectToggle : MonoBehaviour
{
    public Toggle     toggle;
    public GameObject target;

    void Start()
    {
        if (toggle == null || target == null) return;
        toggle.SetIsOnWithoutNotify(target.activeSelf);
        toggle.onValueChanged.AddListener(v => target.SetActive(v));
    }

    void OnDestroy()
    {
        if (toggle != null) toggle.onValueChanged.RemoveAllListeners();
    }
}
