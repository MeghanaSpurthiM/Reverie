using TMPro;
using UnityEngine;

/// ScoreManager owns both score state and UI display.
/// ScoreDisplay.cs can be deleted — this replaces it.
/// globalScore is static so it survives AI-generated scene rebuilds.
public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }
    public static int globalScore = 0;

    [Header("UI")]
    public TMP_Text scoreText;   // accepts both TextMeshPro (3D) and TextMeshProUGUI

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        Refresh();
    }

    /// Call this from Bubble.Pop() or any gameplay event.
    public void AddScore(int points)
    {
        globalScore += points;
        Refresh();
        // NOTE: Do NOT call ArchitectBridge.AddScore here — that caused an
        // infinite loop (Bridge → ScoreManager.AddScore → Bridge → ...).
        // Bridge.AddScore is now only used for external/AI-driven score updates.
    }

    void Refresh()
    {
        if (scoreText != null) scoreText.text = $"Score: {globalScore}";
        // Debug.LogWarning($"Score: {globalScore}");
    }
}