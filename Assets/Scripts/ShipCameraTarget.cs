using UnityEngine;

namespace SpaceMayhem
{
    /// <summary>
    /// Deprecated placeholder.
    ///
    /// The custom camera-feel proxy that used to live here has been replaced by
    /// stock Cinemachine components on the VirtualCamera:
    ///
    ///   • CinemachineThirdPersonFollow  → position lag + behind-the-ship offset
    ///   • CinemachineRotationComposer   → aim damping + screen composition
    ///
    /// This file is kept as a stub so we have a place to hang the barrel-roll
    /// camera-freeze behaviour (and any other temporary CM-damping modulation)
    /// when we revisit it. For now it does nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShipCameraTarget : MonoBehaviour
    {
        // Intentionally empty. Re-implement barrel-roll freeze here by toggling
        // CinemachineThirdPersonFollow.Damping / CinemachineRotationComposer.Damping
        // when SpaceshipController.IsBarrelRolling flips.
    }
}
