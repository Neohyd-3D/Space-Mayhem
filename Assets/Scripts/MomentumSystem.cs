using UnityEngine;

namespace SpaceMayhem
{
    [DisallowMultipleComponent]
    public class MomentumSystem : MonoBehaviour
    {
        public bool IsRedirecting { get; private set; }

        Vector3 _oldVelocity;
        Vector3 _targetVelocity;
        float _timer;
        float _duration;
        Vector3 _blended;

        // Smoothly accelerates from fromVelocity to an explicit toVelocity over duration
        // seconds (smoothstep curve). Used for brake boosts where the target is not a
        // direction-carryover calculation but a known desired velocity.
        public void StartBoost(Vector3 fromVelocity, Vector3 toVelocity, float duration)
        {
            _oldVelocity    = fromVelocity;
            _targetVelocity = toVelocity;
            _timer          = 0f;
            _duration       = Mathf.Max(0.001f, duration);
            _blended        = fromVelocity;
            IsRedirecting   = true;
        }

        // SpaceshipController calls this from FixedUpdate while IsRedirecting.
        public Vector3 Tick(float dt)
        {
            if (!IsRedirecting) return _blended;
            _timer += dt;
            float t = Mathf.Clamp01(_timer / _duration);
            float s = t * t * (3f - 2f * t);
            _blended = Vector3.Lerp(_oldVelocity, _targetVelocity, s);
            if (t >= 1f) IsRedirecting = false;
            return _blended;
        }

        public Vector3 GetBlendedVelocity() => _blended;
    }
}
