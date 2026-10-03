using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// An on/off switch, the modern replacement for a checkbox: a pill track with a
    /// knob that slides across. Clickable by mouse, ray or poke.
    ///
    /// Geometry, because it is easy to get wrong: the track is 120 wide, the knob is
    /// 52 across and inset 6 from each end. A UI element is positioned by its CENTRE,
    /// so the knob's centre travels from 6 + 26 = 32 (off) to 120 - 6 - 26 = 88 (on).
    /// </summary>
    public sealed class VrSwitch : MonoBehaviour, IPointerClickHandler
    {
        const float KnobCentreOff = 32f;
        const float KnobCentreOn = 88f;

        Image _track;
        RectTransform _knob;
        float _position;               // 0 = off, 1 = on, animated

        public bool IsOn { get; private set; }

        /// <summary>Raised only by a user click, never by <see cref="SetValue"/>.</summary>
        public event Action<bool> Changed;

        public void Initialise(Image track, RectTransform knob, bool on)
        {
            _track = track;
            _knob = knob;
            IsOn = on;
            _position = on ? 1f : 0f;
            Apply();

            // Hover feedback like every other control. Colour is owned by the
            // switch itself (it shows on/off), so the hover must not tint it.
            if (GetComponent<VrHoverLift>() == null)
                gameObject.AddComponent<VrHoverLift>().Configure(6f, 1.04f, tint: false);
        }

        public void SetValue(bool on) => IsOn = on;

        public void OnPointerClick(PointerEventData eventData)
        {
            IsOn = !IsOn;
            Changed?.Invoke(IsOn);
        }

        void Update()
        {
            var target = IsOn ? 1f : 0f;
            if (Mathf.Approximately(_position, target)) return;

            _position = Mathf.MoveTowards(_position, target, Time.unscaledDeltaTime * 7f);
            Apply();
        }

        void Apply()
        {
            if (_track == null || _knob == null) return;

            var eased = Mathf.SmoothStep(0f, 1f, _position);

            _track.color = Color.Lerp(MonashTheme.SwitchOff, MonashTheme.Blue, eased);
            _knob.anchoredPosition = new Vector2(Mathf.Lerp(KnobCentreOff, KnobCentreOn, eased), _knob.anchoredPosition.y);
        }
    }
}
