using UnityEngine;

/// Replaces EnvIdentity, PropIdentity, ToolIdentity, TargetIdentity, and ThemeAnchor.
/// Drop this on any themeable GameObject and pick its role in the Inspector.
/// Runtime-only marker: it registers with ThemeHooks, so generation tools can find its role.
public class SceneObjectIdentity : MonoBehaviour
{
    public enum ObjectRole { Environment, Prop, Tool, Target }

    [Header("Scene Role")]
    public ObjectRole role = ObjectRole.Prop;

    // Re-registers after being re-activated (e.g. after an AI scene rebuild).
    void OnEnable()  => ThemeHooks.Register(gameObject);
    void OnDestroy() => ThemeHooks.Unregister(gameObject);
}
