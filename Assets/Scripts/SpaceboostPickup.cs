using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpaceMayhem
{
    [DisallowMultipleComponent]
    public class SpaceboostPickup : MonoBehaviour
    {
        public enum BoostDirectionMode
        {
            ShipForward,
            BoostForward,
            CurrentVelocity,
            AwayFromBoost
        }

        [Header("Boost")]
        [Min(0f)]
        [Tooltip("How much faster than your NORMAL top speed this pad pushes you (m/s above maxSpeed). It " +
                 "kicks your velocity AND lifts the speed ceiling, so you briefly blow past your cap.")]
        public float boostEnergy = 60f;

        [Min(0.05f)]
        [Tooltip("How long the overspeed lasts before it has bled fully back to your normal top speed (s).")]
        public float boostHoldSeconds = 1.5f;

        [Min(0.001f)]
        [Tooltip("Seconds to snap (blend) into the boosted velocity on pickup.")]
        public float boostSnapDuration = 0.2f;

        [Min(0f)]
        [Tooltip("Ensures the velocity component along the boost direction reaches at least this speed after pickup. 0 = off.")]
        public float minimumExitSpeed = 0f;

        public BoostDirectionMode directionMode = BoostDirectionMode.ShipForward;

        [Header("Respawn")]
        [Min(0f)]
        public float cooldownSeconds = 4f;

        [Tooltip("Disable pickup trigger colliders while cooling down so the boost cannot retrigger.")]
        public bool disablePickupCollidersDuringCooldown = true;

        [Header("Scene References")]
        [Tooltip("The visual mesh/root to hide while the pickup is cooling down. If this is the same GameObject as the script, renderers are disabled instead.")]
        public GameObject visualMeshRoot;

        [Tooltip("Particle/effect GameObject named 'active'. It is active while the boost is ready.")]
        public GameObject activeEffectRoot;

        [Tooltip("Particle/effect GameObject named 'sphere-burst'. It plays when the ship collects the boost.")]

        public GameObject burstSphereEffectRoot;

        [Tooltip("Particle/effect GameObject named 'burst'. It plays when the ship collects the boost.")]
        public GameObject burstEffectRoot;

        [Tooltip("Optional explicit trigger colliders for the pickup volume. If empty, trigger colliders are found automatically or a sphere trigger is created at runtime.")]
        public Collider[] pickupColliders;

        [Header("Detection")]
        public LayerMask shipLayers = ~0;

        [Tooltip("Runtime fallback pickup radius when no trigger collider is assigned/found.")]
        [Min(0.01f)]
        public float triggerRadius = 8f;

        [Tooltip("Create a sphere trigger at runtime if the object has no pickup trigger collider.")]
        public bool autoCreateTriggerCollider = true;

        [Tooltip("Ensure this object has a kinematic Rigidbody so trigger callbacks fire without touching the ship.")]
        public bool ensureKinematicRigidbody = true;

        readonly List<Collider> _resolvedPickupColliders = new List<Collider>();
        Renderer[] _visualRenderers = new Renderer[0];
        Coroutine _cooldownRoutine;
        bool _ready = true;

        /// <summary>True when the pad is live (not cooling down) — so AI drivers only detour for boosts they
        /// can actually collect.</summary>
        public bool IsReady => _ready;

        void Reset()
        {
            AutoWireReferences();
        }

        void OnValidate()
        {
            boostEnergy = Mathf.Max(0f, boostEnergy);
            boostHoldSeconds = Mathf.Max(0.05f, boostHoldSeconds);
            boostSnapDuration = Mathf.Max(0.001f, boostSnapDuration);
            minimumExitSpeed = Mathf.Max(0f, minimumExitSpeed);
            cooldownSeconds = Mathf.Max(0f, cooldownSeconds);
            triggerRadius = Mathf.Max(0.01f, triggerRadius);

            if (!Application.isPlaying)
                AutoWireReferences();
        }

        void Awake()
        {
            AutoWireReferences();
            CacheVisualRenderers();
            ResolvePickupColliders();
            EnsurePickupPhysics();
            ShowReadyState();
        }

        void OnTriggerEnter(Collider other)
        {
            TryCollect(other);
        }

        [ContextMenu("Reset Spaceboost Pickup")]
        public void ResetPickup()
        {
            if (_cooldownRoutine != null)
            {
                StopCoroutine(_cooldownRoutine);
                _cooldownRoutine = null;
            }

            _ready = true;
            ResolvePickupColliders();
            EnablePickupColliders(true);
            ShowReadyState();
        }

        void TryCollect(Collider other)
        {
            if (!_ready || other == null)
                return;

            SpaceshipController ship = FindShip(other);
            if (ship == null || !LayerAllowed(other.gameObject.layer, ship.gameObject.layer))
                return;

            _ready = false;

            if (disablePickupCollidersDuringCooldown)
                EnablePickupColliders(false);

            HideCollectedVisuals();
            ApplyBoost(ship);

            if (cooldownSeconds <= 0f)
            {
                ResetPickup();
                return;
            }

            _cooldownRoutine = StartCoroutine(CooldownRoutine());
        }

        IEnumerator CooldownRoutine()
        {
            yield return new WaitForSeconds(cooldownSeconds);
            _cooldownRoutine = null;
            _ready = true;

            EnablePickupColliders(true);
            ShowReadyState();
        }

        void ApplyBoost(SpaceshipController ship)
        {
            MomentumSystem targetMomentum = ship.momentum != null
                ? ship.momentum
                : ship.GetComponent<MomentumSystem>();

            if (targetMomentum == null)
            {
                Debug.LogWarning($"{nameof(SpaceboostPickup)} on {name} could not find a MomentumSystem on {ship.name}.", this);
                return;
            }

            Vector3 fromVelocity = ship.currentVelocity;
            Vector3 direction = ResolveBoostDirection(ship, fromVelocity);
            Vector3 targetVelocity = fromVelocity + direction * boostEnergy;

            if (minimumExitSpeed > 0f)
            {
                float alongBoost = Vector3.Dot(targetVelocity, direction);
                if (alongBoost < minimumExitSpeed)
                    targetVelocity += direction * (minimumExitSpeed - alongBoost);
            }

            // Lift the speed ceiling first (so the kick isn't clamped back to maxSpeed), then snap into the
            // boosted velocity. The ceiling then decays over boostHoldSeconds, bleeding the overspeed away.
            targetMomentum.StartOverspeed(boostEnergy, boostHoldSeconds);
            targetMomentum.StartBoost(fromVelocity, targetVelocity, boostSnapDuration);
        }

        Vector3 ResolveBoostDirection(SpaceshipController ship, Vector3 currentVelocity)
        {
            Vector3 direction = Vector3.zero;
            switch (directionMode)
            {
                case BoostDirectionMode.BoostForward:
                    direction = transform.forward;
                    break;
                case BoostDirectionMode.CurrentVelocity:
                    direction = currentVelocity;
                    break;
                case BoostDirectionMode.AwayFromBoost:
                    direction = ship.transform.position - transform.position;
                    break;
                case BoostDirectionMode.ShipForward:
                default:
                    direction = ship.transform.forward;
                    break;
            }

            if (direction.sqrMagnitude < 1e-6f)
                direction = ship.transform.forward;

            if (direction.sqrMagnitude < 1e-6f)
                direction = transform.forward;

            return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.forward;
        }

        void HideCollectedVisuals()
        {
            SetVisualActive(false);
            SetEffectActive(activeEffectRoot, false, false);
            SetEffectActive(burstEffectRoot, true, true);
            SetEffectActive(burstSphereEffectRoot, true, true);
        }

        void ShowReadyState()
        {
            SetEffectActive(burstSphereEffectRoot, false, false);
            SetEffectActive(burstEffectRoot, false, false);
            SetVisualActive(true);
            SetEffectActive(activeEffectRoot, true, true);
        }

        void SetVisualActive(bool active)
        {
            if (CanToggleVisualRoot())
            {
                visualMeshRoot.SetActive(active);
                return;
            }

            for (int i = 0; i < _visualRenderers.Length; i++)
            {
                if (_visualRenderers[i] != null)
                    _visualRenderers[i].enabled = active;
            }
        }

        bool CanToggleVisualRoot()
        {
            if (visualMeshRoot == null || visualMeshRoot == gameObject)
                return false;

            Transform visualTransform = visualMeshRoot.transform;
            return !IsUnderRoot(activeEffectRoot != null ? activeEffectRoot.transform : null, visualTransform)
                && !IsUnderRoot(burstEffectRoot != null ? burstEffectRoot.transform : null, visualTransform);
        }

        static void SetEffectActive(GameObject effectRoot, bool active, bool play)
        {
            if (effectRoot == null)
                return;

            ParticleSystem[] particles = effectRoot.GetComponentsInChildren<ParticleSystem>(true);
            if (active)
            {
                effectRoot.SetActive(true);
                if (play)
                {
                    for (int i = 0; i < particles.Length; i++)
                        particles[i].Play(true);
                }
            }
            else
            {
                for (int i = 0; i < particles.Length; i++)
                    particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                effectRoot.SetActive(false);
            }
        }

        void AutoWireReferences()
        {
            if (activeEffectRoot == null)
                activeEffectRoot = FindChildByExactName("active");

            if (burstEffectRoot == null)
                burstEffectRoot = FindChildByExactName("burst");

            if (visualMeshRoot == null)
            {
                visualMeshRoot = FindChildByPartialName("speedboost")
                    ?? FindChildByPartialName("speedbost")
                    ?? FindChildByPartialName("visual")
                    ?? FindChildByPartialName("mesh")
                    ?? gameObject;
            }
        }

        void CacheVisualRenderers()
        {
            GameObject root = visualMeshRoot != null ? visualMeshRoot : gameObject;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            List<Renderer> filtered = new List<Renderer>(renderers.Length);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsUnderEffectRoot(renderer.transform))
                    continue;

                filtered.Add(renderer);
            }

            _visualRenderers = filtered.ToArray();
        }

        bool IsUnderEffectRoot(Transform candidate)
        {
            return IsUnderRoot(candidate, activeEffectRoot != null ? activeEffectRoot.transform : null)
                || IsUnderRoot(candidate, burstEffectRoot != null ? burstEffectRoot.transform : null);
        }

        static bool IsUnderRoot(Transform candidate, Transform root)
        {
            if (candidate == null || root == null)
                return false;

            Transform current = candidate;
            while (current != null)
            {
                if (current == root)
                    return true;
                current = current.parent;
            }

            return false;
        }

        void ResolvePickupColliders()
        {
            _resolvedPickupColliders.Clear();

            if (pickupColliders != null && pickupColliders.Length > 0)
            {
                for (int i = 0; i < pickupColliders.Length; i++)
                {
                    if (pickupColliders[i] != null)
                        _resolvedPickupColliders.Add(pickupColliders[i]);
                }
                return;
            }

            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && colliders[i].isTrigger)
                    _resolvedPickupColliders.Add(colliders[i]);
            }
        }

        void EnsurePickupPhysics()
        {
            if (autoCreateTriggerCollider && _resolvedPickupColliders.Count == 0)
            {
                SphereCollider trigger = gameObject.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = triggerRadius;
                _resolvedPickupColliders.Add(trigger);
            }

            if (!ensureKinematicRigidbody)
                return;

            Rigidbody body = GetComponent<Rigidbody>();
            if (body == null)
                body = gameObject.AddComponent<Rigidbody>();

            body.useGravity = false;
            body.isKinematic = true;
        }

        void EnablePickupColliders(bool enabled)
        {
            for (int i = 0; i < _resolvedPickupColliders.Count; i++)
            {
                if (_resolvedPickupColliders[i] != null)
                    _resolvedPickupColliders[i].enabled = enabled;
            }
        }

        bool LayerAllowed(int colliderLayer, int shipLayer)
        {
            int mask = shipLayers.value;
            return (mask & (1 << colliderLayer)) != 0 || (mask & (1 << shipLayer)) != 0;
        }

        static SpaceshipController FindShip(Collider other)
        {
            SpaceshipController ship = other.GetComponentInParent<SpaceshipController>();
            if (ship != null)
                return ship;

            Rigidbody body = other.attachedRigidbody;
            return body != null ? body.GetComponentInParent<SpaceshipController>() : null;
        }

        GameObject FindChildByExactName(string childName)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] == transform)
                    continue;

                if (string.Equals(children[i].name, childName, System.StringComparison.OrdinalIgnoreCase))
                    return children[i].gameObject;
            }

            return null;
        }

        GameObject FindChildByPartialName(string partialName)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                string candidate = children[i].name;
                if (candidate.IndexOf(partialName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return children[i].gameObject;
            }

            return null;
        }
    }
}
