using UnityEngine;
using UnityEngine.UI;

/// Android Material Design-style toggle switch.
/// Attach to a Toggle that has child Image components for Track and Thumb.
[RequireComponent(typeof(Toggle))]
public class ToggleSliderAnim : MonoBehaviour
{
    [Header("References")]
    public RectTransform thumb;
    public Image         track;

    [Header("Thumb X positions")]
    public float offX = -22f;
    public float onX  =  22f;

    [Header("Material Colors")]
    public Color trackOff  = new Color(0.60f, 0.60f, 0.60f, 1f);   // grey
    public Color trackOn   = new Color(0.13f, 0.59f, 0.95f, 1f);   // Material Blue 500
    public Color thumbOff  = new Color(0.98f, 0.98f, 0.98f, 1f);   // near-white
    public Color thumbOn   = new Color(1.00f, 1.00f, 1.00f, 1f);   // white

    [Header("Animation")]
    public float slideSpeed  = 12f;
    public float colorSpeed  = 10f;
    // Thumb briefly scales up on press (like Material ripple substitute)
    public float pressScale  = 1.15f;
    public float scaleSpeed  = 16f;

    Toggle _toggle;
    Image  _thumbImage;

    float  _targetX;
    Color  _targetTrack;
    Color  _targetThumb;
    float  _targetScale = 1f;
    bool   _prevPressed;

    void Awake()
    {
        _toggle     = GetComponent<Toggle>();
        _thumbImage = thumb != null ? thumb.GetComponent<Image>() : null;

        Apply(_toggle.isOn, snap: true);
        _toggle.onValueChanged.AddListener(OnValueChanged);
    }

    void OnValueChanged(bool isOn) => Apply(isOn, snap: false);

    public void Snap(bool isOn) => Apply(isOn, snap: true);

    void Apply(bool isOn, bool snap)
    {
        _targetX     = isOn ? onX     : offX;
        _targetTrack = isOn ? trackOn : trackOff;
        _targetThumb = isOn ? thumbOn : thumbOff;

        if (snap)
        {
            if (thumb      != null) thumb.anchoredPosition = new Vector2(_targetX, thumb.anchoredPosition.y);
            if (track      != null) track.color            = _targetTrack;
            if (_thumbImage != null) _thumbImage.color     = _targetThumb;
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // Slide
        if (thumb != null)
        {
            var pos = thumb.anchoredPosition;
            pos.x = Mathf.Lerp(pos.x, _targetX, dt * slideSpeed);
            thumb.anchoredPosition = pos;
        }

        // Track color
        if (track != null)
            track.color = Color.Lerp(track.color, _targetTrack, dt * colorSpeed);

        // Thumb color
        if (_thumbImage != null)
            _thumbImage.color = Color.Lerp(_thumbImage.color, _targetThumb, dt * colorSpeed);

        // Thumb press scale (substitute for ripple)
        bool pressed = _toggle.interactable && OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
        if (pressed && !_prevPressed) _targetScale = pressScale;
        if (!pressed && _prevPressed) _targetScale = 1f;
        _prevPressed = pressed;

        if (thumb != null)
        {
            float s = Mathf.Lerp(thumb.localScale.x, _targetScale, dt * scaleSpeed);
            thumb.localScale = new Vector3(s, s, 1f);
        }
    }

    void OnDestroy() => _toggle.onValueChanged.RemoveListener(OnValueChanged);
}
