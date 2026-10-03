using System.Collections.Generic;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// XR Interaction Toolkit's raycaster for UI, with its one sharp edge filed off.
    ///
    /// The toolkit's raycaster registers itself in a static table when it wakes and removes itself when it is destroyed,
    /// but its OnDisable looks itself up in that table again. When Play mode ends the editor can destroy it first, and
    /// the lookup throws a KeyNotFoundException, once for every canvas (the briefing page, the fade). The exception
    /// is harmless but it fills the console with red every time Play stops. By then there is nothing left to tidy up,
    /// so this swallows exactly that error and nothing else.
    /// </summary>
    public sealed class SafeTrackedDeviceRaycaster : TrackedDeviceGraphicRaycaster
    {
        protected override void OnDisable()
        {
            try { base.OnDisable(); }
            catch (KeyNotFoundException) { /* already unregistered while the scene was being torn down */ }
        }
    }
}
