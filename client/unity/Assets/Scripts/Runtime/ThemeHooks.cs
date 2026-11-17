using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The seam between the game (Reverie.Runtime) and theme generation (Reverie.Generation).
///
/// Runtime code never references generation code. Instead, runtime objects announce themselves
/// here, and generation tools — when they are compiled in and present in the scene — subscribe.
/// In a player-only build nothing subscribes, and every call below is a cheap no-op.
/// </summary>
public static class ThemeHooks
{
    static readonly HashSet<GameObject> _objects = new();

    /// Themeable objects currently alive (SceneObjectIdentity, spawned targets).
    public static IReadOnlyCollection<GameObject> Objects => _objects;

    public static event Action<GameObject> ObjectRegistered;
    public static event Action<GameObject> ObjectUnregistered;

    /// Set by a generation tool that restyles spawned targets. Null otherwise.
    public static Action<Transform> TargetVisual;

    public static void Register(GameObject go)
    {
        if (go != null && _objects.Add(go)) ObjectRegistered?.Invoke(go);
    }

    public static void Unregister(GameObject go)
    {
        if (go != null && _objects.Remove(go)) ObjectUnregistered?.Invoke(go);
    }

    /// Called by each spawned target (e.g. Bubble.Start) so an active generation tool can restyle it.
    public static void ApplyTargetVisual(Transform target) => TargetVisual?.Invoke(target);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()   // keeps "Enter Play Mode without domain reload" correct
    {
        _objects.Clear();
        ObjectRegistered = null;
        ObjectUnregistered = null;
        TargetVisual = null;
    }
}
