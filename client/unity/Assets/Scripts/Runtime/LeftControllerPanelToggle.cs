using UnityEngine;
using UnityEngine.UI;

/// Attach to the LeftControllerAnchor.
///
/// Y button        — shows / hides the settings panel.
/// L index trigger — clicks the hovered Toggle (axis-based, not button enum).
public class LeftControllerPanelToggle : MonoBehaviour
{
    [Header("Panel")]
    public Canvas settingsPanel;
    [Tooltip("Distance in front of the camera when the panel opens.")]
    public float panelDistance = 1.5f;
    [Tooltip("Height offset from camera position.")]
    public float panelHeightOffset = 0f;

    [Header("Visual")]
    public LineRenderer laserLine;
    public float        rayLength    = 5f;
    public Color        laserIdle    = new Color(0.4f, 0.8f, 1f, 1f);
    public Color        laserHover   = new Color(1f,   1f,   0f, 1f);
    public Color        laserClick   = new Color(1f,   0.4f, 0f, 1f);

    [Header("Input")]
    public OVRInput.Controller controller   = OVRInput.Controller.LTouch;
    public OVRInput.Button     toggleButton = OVRInput.Button.Two;   // Y
    [Range(0.1f, 0.9f)]
    public float clickThreshold = 0.5f;   // left index trigger axis threshold

    Toggle[]   _toggles;
    Button[]   _buttons;
    Toggle     _hovered;
    Button     _hoveredBtn;
    bool       _prevClick;
    bool       _prevToggle;

    void Start()
    {
        if (settingsPanel != null)
        {
            _toggles = settingsPanel.GetComponentsInChildren<Toggle>(true);
            _buttons = settingsPanel.GetComponentsInChildren<Button>(true);
        }

        if (settingsPanel != null)
            settingsPanel.gameObject.SetActive(false);

        if (laserLine != null)
        {
            laserLine.enabled = false;
            laserLine.startWidth = 0.004f;
            laserLine.endWidth   = 0.001f;
            SetLaserColor(laserIdle);
        }
    }

    void Update()
    {
        bool controllerActive = OVRInput.IsControllerConnected(controller);

        // ── Show / hide panel ─────────────────────────────────────────────────
        bool toggleHeld = controllerActive && OVRInput.Get(toggleButton, controller);
        if (toggleHeld && !_prevToggle && settingsPanel != null)
        {
            bool nowActive = !settingsPanel.gameObject.activeSelf;
            if (nowActive)
            {
                PositionPanelInFront();
                _toggles = settingsPanel.GetComponentsInChildren<Toggle>(true);
                _buttons = settingsPanel.GetComponentsInChildren<Button>(true);
            }
            settingsPanel.gameObject.SetActive(nowActive);
            if (!nowActive) _hovered = null;
        }
        _prevToggle = toggleHeld;

        // ── Raycast ───────────────────────────────────────────────────────────
        bool panelActive = settingsPanel != null && settingsPanel.gameObject.activeSelf;

        if (laserLine != null)
            laserLine.enabled = controllerActive && panelActive;

        if (!controllerActive || !panelActive) { _hovered = null; return; }

        Ray   ray   = new Ray(transform.position, transform.forward);
        Plane plane = new Plane(-settingsPanel.transform.forward, settingsPanel.transform.position);

        plane.Raycast(ray, out float dist);
        bool hits = dist > 0f;

        if (laserLine != null)
        {
            laserLine.SetPosition(0, ray.origin);
            laserLine.SetPosition(1, ray.GetPoint(hits ? Mathf.Min(dist, rayLength) : rayLength));
        }

        if (!hits) { _hovered = null; SetLaserColor(laserIdle); return; }

        Vector3 hitWorld = ray.GetPoint(dist);

        Toggle newHover    = null;
        Button newHoverBtn = null;

        foreach (var t in _toggles)
        {
            if (t == null || !t.gameObject.activeInHierarchy || !t.interactable) continue;
            var rt    = (RectTransform)t.transform;
            var local = rt.InverseTransformPoint(hitWorld);
            if (rt.rect.Contains(new Vector2(local.x, local.y))) { newHover = t; break; }
        }

        if (newHover == null && _buttons != null)
        {
            foreach (var b in _buttons)
            {
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                var rt    = (RectTransform)b.transform;
                var local = rt.InverseTransformPoint(hitWorld);
                if (rt.rect.Contains(new Vector2(local.x, local.y))) { newHoverBtn = b; break; }
            }
        }

        _hovered    = newHover;
        _hoveredBtn = newHoverBtn;

        // ── Click (index trigger as axis) ─────────────────────────────────────
        float triggerVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
        bool  click      = triggerVal >= clickThreshold;

        if (click && !_prevClick)
        {
            if (_hovered    != null && _hovered.interactable) _hovered.isOn = !_hovered.isOn;
            if (_hoveredBtn != null) _hoveredBtn.onClick.Invoke();
        }
        _prevClick = click;

        // ── Laser color feedback ──────────────────────────────────────────────
        bool anyHover = _hovered != null || _hoveredBtn != null;
        if (click)          SetLaserColor(laserClick);
        else if (anyHover)  SetLaserColor(laserHover);
        else                SetLaserColor(laserIdle);
    }

    void SetLaserColor(Color c)
    {
        if (laserLine == null) return;
        laserLine.startColor = c;
        laserLine.endColor   = new Color(c.r, c.g, c.b, 0f); // fade to transparent at tip
    }

    void PositionPanelInFront()
    {
        Camera cam = Camera.main;
        if (cam == null || settingsPanel == null) return;

        Vector3 flat = cam.transform.forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.001f) flat = Vector3.forward;
        flat.Normalize();

        settingsPanel.transform.position = cam.transform.position
                                         + flat * panelDistance
                                         + Vector3.up * panelHeightOffset;
        settingsPanel.transform.rotation = Quaternion.LookRotation(flat);
    }
}
