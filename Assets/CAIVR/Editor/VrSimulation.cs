using CAIVR.VR;
using UnityEditor;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// A switch for trying the headset experience without a headset.
    ///
    /// Ticked, pressing Play with no headset connected starts the scene in headset
    /// mode, with the XR Interaction Simulator standing in for the headset and
    /// hands: the XR rig, hand meshes, pinch and poke, rays and grabbing all run.
    /// Unticked (the default), no headset means the normal desktop view.
    ///
    /// A real headset always wins. If one is connected the simulator never starts.
    /// </summary>
    static class VrSimulation
    {
        const string MenuPath = "CAIVR/VR/Simulate Headset In Play Mode";

        [MenuItem(MenuPath, priority = 110)]
        static void Toggle() => EditorPrefs.SetBool(ExperienceRig.SimulateKey, !IsOn);

        [MenuItem(MenuPath, true)]
        static bool Validate()
        {
            UnityEditor.Menu.SetChecked(MenuPath, IsOn);
            return true;
        }

        public static bool IsOn => EditorPrefs.GetBool(ExperienceRig.SimulateKey, false);

        public static void Set(bool on) => EditorPrefs.SetBool(ExperienceRig.SimulateKey, on);
    }
}
