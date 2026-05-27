using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceMayhem
{
    /// <summary>
    /// Reads SpaceshipInputActions and forwards a per-frame rotation delta (in degrees,
    /// already source-scaled) to SpaceshipController. Mouse delta and gamepad sticks
    /// have fundamentally different semantics:
    ///   - Mouse delta is in pixels-this-frame, frame-rate independent on its own.
    ///     Use sensitivity in degrees-per-pixel; DO NOT multiply by Time.deltaTime.
    ///   - Gamepad stick is normalized (-1..1) and represents a rate.
    ///     Convert to degrees-per-frame via × gamepadLookSpeed × Time.deltaTime.
    ///
    /// Gamepad layout
    ///   Left stick X      strafe left/right
    ///   Left stick Y      elevate up/down (hover)
    ///   Right stick X     yaw left/right
    ///   Right stick Y     pitch nose up/down (tilt)
    ///   Right trigger     forward thrust
    ///   Left trigger      reverse thrust
    ///   D-pad ←           barrel roll left  (single committed 360°)
    ///   D-pad →           barrel roll right (single committed 360°)
    ///   D-pad ↑           barrel roll up    (nose over the top)
    ///   D-pad ↓           barrel roll down  (nose goes under)
    ///   L1                brake
    ///
    /// Keyboard/Mouse
    ///   WASD              thrust + strafe
    ///   R/F               elevate
    ///   Q                 barrel roll left  (single committed 360°)
    ///   E                 barrel roll right (single committed 360°)
    ///   ↑ / ↓ arrows      barrel roll up / down
    ///   Mouse XY          yaw/pitch
    ///   Space             brake
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SpaceshipController))]
    public class SpaceshipInput : MonoBehaviour
    {
        [Tooltip("SpaceshipInputActions asset (Assets/InputActions/).")]
        public InputActionAsset inputActions;

        [Header("Mouse Look")]
        [Tooltip("Degrees rotated per pixel of mouse delta. Standard FPS-style sensitivity.")]
        public float mouseSensitivity = 0.12f;

        [Header("Gamepad Look")]
        [Tooltip("Maximum rotation rate at full stick deflection (degrees/second).")]
        public float gamepadLookSpeed = 140f;

        [Header("Safety")]
        [Tooltip("Hard cap on per-frame look magnitude (degrees). Prevents spikes from focus-change mouse jumps.")]
        public float maxLookDegreesPerFrame = 25f;

        [Tooltip("Number of frames at startup to discard all input (flushes any focus-change delta on Play Mode entry).")]
        public int settleFrames = 2;

        SpaceshipController _controller;

        InputAction _strafeX;
        InputAction _thrustZ;
        InputAction _lookYawMouse;
        InputAction _lookPitchMouse;
        InputAction _lookYawPad;
        InputAction _lookPitchPad;
        InputAction _barrelRollLeft;
        InputAction _barrelRollRight;
        InputAction _barrelRollUp;
        InputAction _barrelRollDown;
        InputAction _strafeVertical;
        InputAction _brake;

        int _framesUntilLive;

        void Awake()
        {
            _controller = GetComponent<SpaceshipController>();
            if (inputActions == null)
            {
                Debug.LogError("[SpaceshipInput] No InputActionAsset assigned.", this);
                enabled = false;
                return;
            }

            var map = inputActions.FindActionMap("Player", throwIfNotFound: true);
            _strafeX        = map.FindAction("StrafeX",        throwIfNotFound: true);
            _thrustZ        = map.FindAction("ThrustZ",        throwIfNotFound: true);
            _lookYawMouse   = map.FindAction("LookYawMouse",   throwIfNotFound: true);
            _lookPitchMouse = map.FindAction("LookPitchMouse", throwIfNotFound: true);
            _lookYawPad      = map.FindAction("LookYawPad",       throwIfNotFound: true);
            _lookPitchPad    = map.FindAction("LookPitchPad",     throwIfNotFound: true);
            _barrelRollLeft  = map.FindAction("BarrelRollLeft",   throwIfNotFound: true);
            _barrelRollRight = map.FindAction("BarrelRollRight",  throwIfNotFound: true);
            _barrelRollUp    = map.FindAction("BarrelRollUp",     throwIfNotFound: true);
            _barrelRollDown  = map.FindAction("BarrelRollDown",   throwIfNotFound: true);
            _strafeVertical  = map.FindAction("StrafeVertical",   throwIfNotFound: true);
            _brake          = map.FindAction("Brake",          throwIfNotFound: true);
        }

        void OnEnable()
        {
            if (inputActions != null) inputActions.Enable();
            _framesUntilLive = Mathf.Max(1, settleFrames);
        }

        void OnDisable()
        {
            if (inputActions != null) inputActions.Disable();
        }

        void Update()
        {
            if (_controller == null) return;
            float dt = Time.deltaTime;

            // Settle period — drain action state so focus-change mouse delta doesn't
            // surface as first-frame movement.
            if (_framesUntilLive > 0)
            {
                _framesUntilLive--;
                _strafeX.ReadValue<float>();
                _thrustZ.ReadValue<float>();
                _lookYawMouse.ReadValue<float>();
                _lookPitchMouse.ReadValue<float>();
                _lookYawPad.ReadValue<float>();
                _lookPitchPad.ReadValue<float>();
                _barrelRollLeft.IsPressed();
                _barrelRollRight.IsPressed();
                _barrelRollUp.IsPressed();
                _barrelRollDown.IsPressed();
                _strafeVertical.ReadValue<float>();
                _brake.IsPressed();
                _controller.ApplyInput(Vector3.zero, Vector3.zero, false);
                return;
            }

            float strafeX = _strafeX.ReadValue<float>();
            float thrustZ = _thrustZ.ReadValue<float>();
            float strafeV = _strafeVertical.ReadValue<float>();
            bool  braking = _brake.IsPressed();

            // Barrel roll — fires once per press, never on hold
            // Left/right: rotate around local Z (ship's nose axis)
            // Up/down:    rotate around local X (ship's wing axis)
            if (_barrelRollLeft.WasPressedThisFrame())  _controller.TriggerBarrelRoll( 1f, Vector3.forward);
            if (_barrelRollRight.WasPressedThisFrame()) _controller.TriggerBarrelRoll(-1f, Vector3.forward);
            if (_barrelRollUp.WasPressedThisFrame())    _controller.TriggerBarrelRoll( 1f, Vector3.right);
            if (_barrelRollDown.WasPressedThisFrame())  _controller.TriggerBarrelRoll(-1f, Vector3.right);

            // --- LOOK: combine mouse-delta + gamepad-rate into degrees-this-frame ---
            float mouseYawPx   = _lookYawMouse.ReadValue<float>();    // pixels this frame
            float mousePitchPx = _lookPitchMouse.ReadValue<float>();
            float padYaw       = _lookYawPad.ReadValue<float>();      // -1..1
            float padPitch     = _lookPitchPad.ReadValue<float>();

            float yawDeg   = mouseYawPx   * mouseSensitivity + padYaw   * gamepadLookSpeed * dt;
            float pitchDeg = mousePitchPx * mouseSensitivity + padPitch * gamepadLookSpeed * dt;

            // Safety clamp — prevents single-frame mouse delta spikes (focus return, etc.)
            // from causing huge instant rotations. Has no effect on gamepad (always small).
            yawDeg   = Mathf.Clamp(yawDeg,   -maxLookDegreesPerFrame, maxLookDegreesPerFrame);
            pitchDeg = Mathf.Clamp(pitchDeg, -maxLookDegreesPerFrame, maxLookDegreesPerFrame);

            // Local-space thrust: x = strafe, y = elevate, z = forward/back
            Vector3 thrust = new Vector3(strafeX, strafeV, thrustZ);

            // Rotation in absolute degrees this frame:
            //   x = pitch (negated so mouse-up / stick-up = nose UP)
            //   y = yaw   (positive = turn right)
            //   z = 0     (roll is now a triggered barrel roll, not a continuous axis)
            Vector3 rotation = new Vector3(-pitchDeg, yawDeg, 0f);

            _controller.ApplyInput(thrust, rotation, braking);
        }
    }
}
