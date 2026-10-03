using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// Gives a control hover and press feedback: it rises toward the viewer,
    /// brightens, and presses back when clicked.
    ///
    /// On a flat screen the rise is a small flourish. For anything in the 3D world
    /// it is real depth: the control visibly leaves the surface, which is the
    /// clearest possible signal that the pointer is on it. Driven by pointer
    /// events, so mouse, XR ray and poke all behave the same way.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class VrHoverLift : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [Tooltip("How far toward the viewer it rises, in canvas pixels (1 px = 1 mm on world panels).")]
        [SerializeField] float liftPixels = 10f;
        [SerializeField] float hoverScale = 1.035f;
        [SerializeField] float pressedScale = 0.97f;
        [SerializeField] float sharpness = 18f;

        [Tooltip("Also brighten the control's colour when pointed at.")]
        [SerializeField] bool tintOnHover = true;

        RectTransform _rect;
        Graphic _graphic;
        Color _baseColor;
        float _amount;          // 0 = idle, 1 = hovered
        float _baseZ;
        Vector3 _baseScale;
        bool _hover;
        bool _pressed;

        void Awake()
        {
            _rect = (RectTransform)transform;
            _graphic = GetComponent<Graphic>();
            if (_graphic != null) _baseColor = _graphic.color;

            _baseZ = _rect.anchoredPosition3D.z;
            _baseScale = _rect.localScale;
        }

        void OnDisable()
        {
            _hover = _pressed = false;
            _amount = 0f;
            if (_rect == null) return;

            var p = _rect.anchoredPosition3D;
            _rect.anchoredPosition3D = new Vector3(p.x, p.y, _baseZ);
            _rect.localScale = _baseScale;
            if (_graphic != null && tintOnHover) _graphic.color = _baseColor;
        }

        /// <summary>Wide controls look odd growing by 3.5%, so they ask for less.</summary>
        public void Configure(float lift, float hover, bool tint = true)
        {
            liftPixels = lift;
            hoverScale = hover;
            tintOnHover = tint;
        }

        public void OnPointerEnter(PointerEventData eventData) => _hover = true;
        public void OnPointerExit(PointerEventData eventData) { _hover = false; _pressed = false; }
        public void OnPointerDown(PointerEventData eventData) => _pressed = true;
        public void OnPointerUp(PointerEventData eventData) => _pressed = false;

        /// <summary>
        /// Faint translucent controls brighten by gaining opacity; solid ones by
        /// moving toward white. One rule cannot do both, because a colour that is
        /// already white at low alpha has no brightness left to add.
        /// </summary>
        Color HoverColor()
        {
            if (_baseColor.a < 0.5f)
                return new Color(1f, 1f, 1f, Mathf.Min(0.30f, _baseColor.a * (_pressed ? 6f : 4f)));

            var target = Color.Lerp(_baseColor, _pressed ? Color.black : Color.white, _pressed ? 0.18f : 0.2f);
            target.a = _baseColor.a;
            return target;
        }

        void Update()
        {
            // A world canvas faces away from the viewer along +Z, so toward them is -Z.
            // On a screen canvas the same offset is harmless.
            var targetZ = _hover ? _baseZ - liftPixels : _baseZ;
            var targetScale = _pressed ? pressedScale : _hover ? hoverScale : 1f;

            var t = 1f - Mathf.Exp(-sharpness * Time.unscaledDeltaTime);

            var position = _rect.anchoredPosition3D;
            position.z = Mathf.Lerp(position.z, targetZ, t);
            _rect.anchoredPosition3D = position;

            _rect.localScale = Vector3.Lerp(_rect.localScale, _baseScale * targetScale, t);

            if (_graphic != null && tintOnHover)
            {
                _amount = Mathf.Lerp(_amount, _hover ? 1f : 0f, t);
                _graphic.color = Color.Lerp(_baseColor, HoverColor(), _amount);
            }
        }
    }
}
