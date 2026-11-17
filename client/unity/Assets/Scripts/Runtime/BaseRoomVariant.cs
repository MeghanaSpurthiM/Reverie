using UnityEngine;

/// Optional marker for a base room placed under the Room object, for example an Asset Store prefab.
/// Every direct child of Room is a room variant whether or not it has this component; add it to
/// give the room a display name or to leave it out of the cycle while you work on it.
[DisallowMultipleComponent]
public class BaseRoomVariant : MonoBehaviour
{
    [Tooltip("Name shown in logs. Defaults to the GameObject name.")]
    public string displayName;

    [Tooltip("Untick to keep this room in the scene but skip it when cycling.")]
    public bool includeInCycle = true;

    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;
}
