using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// The "fade to black, show the background, then a Start button" beat from
    /// Workerbee #2.
    ///
    /// Two layers:
    ///   - a fade glued to the camera, because there is no screen to fade in 3D; it
    ///     has to be geometry right in front of the eyes
    ///   - a briefing card laid over the view like the subtitles are
    ///
    /// The card is deliberately NOT a floating object in the room. An earlier
    /// version hung it 1.4 m ahead of the head, and turning toward a wall closer
    /// than that put the card inside the wall. On the camera it can never clip.
    ///
    /// A progress bar shows the wait before Start appears, so the student can see
    /// something is coming instead of staring at a card with no way forward.
    /// </summary>
    public sealed class VrIntroOverlay : MonoBehaviour
    {
        static readonly Vector2 CardPixels = new Vector2(1240f, 700f);

        Camera _camera;
        Canvas _fadeCanvas;
        Canvas _cardCanvas;
        Image _fade;
        TextMeshProUGUI _title;
        TextMeshProUGUI _body;
        RectTransform _countdownRoot;
        RectTransform _countdownFill;
        Button _startButton;
        float _alpha;

        public float Alpha => _alpha;

        /// <summary>Builds the overlay on the given camera. Safe to call again with a new camera.</summary>
        public void Attach(Camera camera)
        {
            if (camera == null) return;

            _camera = camera;
            if (_fadeCanvas == null) Build();

            _cardCanvas.worldCamera = _camera;

            // Large and close: at 0.2 m, a 4 m canvas covers well over a field of view.
            _fadeCanvas.transform.SetParent(_camera.transform, false);
            _fadeCanvas.transform.localPosition = new Vector3(0f, 0f, 0.2f);
            _fadeCanvas.transform.localRotation = Quaternion.identity;
        }

        public void SetAlphaImmediate(float alpha)
        {
            _alpha = Mathf.Clamp01(alpha);
            if (_fade != null) _fade.color = new Color(0f, 0f, 0f, _alpha);
        }

        public IEnumerator FadeTo(float target, float seconds)
        {
            var start = _alpha;
            var elapsed = 0f;

            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                // Ease so the change does not start or stop abruptly; abrupt
                // brightness changes are the unpleasant part of a fade.
                SetAlphaImmediate(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, elapsed / seconds)));
                yield return null;
            }

            SetAlphaImmediate(target);
        }

        public void ShowCard(string title, string body)
        {
            if (_cardCanvas == null) return;

            _title.text = title;
            _body.text = body;
            _startButton.gameObject.SetActive(false);
            _countdownRoot.gameObject.SetActive(false);
            _cardCanvas.gameObject.SetActive(true);
        }

        public void HideCard()
        {
            if (_cardCanvas != null) _cardCanvas.gameObject.SetActive(false);
        }

        /// <summary>Fills a progress bar over <paramref name="seconds"/>, then reveals Start.</summary>
        public IEnumerator Countdown(float seconds, UnityAction onStart)
        {
            _startButton.gameObject.SetActive(false);
            _countdownRoot.gameObject.SetActive(true);

            var elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                SetFill(elapsed / Mathf.Max(0.01f, seconds));
                yield return null;
            }

            _countdownRoot.gameObject.SetActive(false);

            _startButton.onClick.RemoveAllListeners();
            if (onStart != null) _startButton.onClick.AddListener(onStart);
            _startButton.gameObject.SetActive(true);
        }

        void SetFill(float t)
        {
            if (_countdownFill != null) _countdownFill.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
        }

        void LateUpdate()
        {
            if (_cardCanvas != null && _cardCanvas.gameObject.activeSelf) VrUi.EnsureEventCamera(_cardCanvas);
        }

        // --- construction ----------------------------------------------------

        void Build()
        {
            _fadeCanvas = VrUi.CreateWorldCanvas("Fade", transform, new Vector2(2000f, 2000f), 4f);
            // Above everything else drawn in the transparent queue, so nothing
            // peeks through the black.
            _fadeCanvas.overrideSorting = true;
            _fadeCanvas.sortingOrder = 100;

            _fade = VrUi.Surface(_fadeCanvas.transform, "Black", Layout.Fill(), new Color(0f, 0f, 0f, _alpha), 1f);
            _fade.sprite = null;                       // a plain rectangle, not a rounded one
            _fade.type = Image.Type.Simple;

            // Sorted above the fade, so the card sits on top of the black.
            _cardCanvas = VrUi.CreateScreenCanvas("Card", transform, _camera, sortingOrder: 101);

            var card = VrUi.Card(_cardCanvas.transform, "Card", Layout.Center(CardPixels.x, CardPixels.y), 44f);
            var face = card.transform;

            var brand = VrUi.Surface(face, "Brand", Layout.TopLeft(56, 48, 158, 48), MonashTheme.Blue, 24f);
            VrUi.Text(brand.transform, "Label", Layout.Fill(), "CAIVR", 24, MonashTheme.Text,
                TextAlignmentOptions.Center, tracking: 5f);

            VrUi.Eyebrow(face, "Eyebrow", Layout.TopLeft(56, 128, 600, 32), "Your situation");

            _title = VrUi.Text(face, "Title", Layout.TopStretch(56, 162, 56, 66), "", 52,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);

            _body = VrUi.Text(face, "Body", Layout.TopStretch(56, 242, 56, 270), "", 42,
                MonashTheme.Text, TextAlignmentOptions.TopLeft, autoSize: true);

            // Countdown, then Start. They share the same spot at the bottom.
            _countdownRoot = VrUi.Group(face, "Countdown", Layout.BottomStretch(56, 44, 56, 90));

            VrUi.Text(_countdownRoot, "Caption", Layout.TopStretch(0, 0, 0, 36), "Take a moment to read this", 28,
                MonashTheme.TextDim, TextAlignmentOptions.MidlineLeft);

            var track = VrUi.Surface(_countdownRoot, "Track", Layout.BottomStretch(0, 8, 0, 14), MonashTheme.Border, 7f);
            _countdownFill = VrUi.Surface(track.transform, "Fill", Layout.Fill(), MonashTheme.BlueLight, 7f).rectTransform;
            SetFill(0f);

            _startButton = VrUi.PillButton(face, "StartButton", "Start consultation",
                Layout.BottomCenter(520, 96, 40), null, ButtonStyle.Primary, 40);
            _startButton.gameObject.SetActive(false);

            _cardCanvas.gameObject.SetActive(false);
        }
    }
}
