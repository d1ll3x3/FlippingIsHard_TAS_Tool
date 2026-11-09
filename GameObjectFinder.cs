using System;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Locates and caches the local player and its components. Lookups that fail are
    /// throttled so an empty scene doesn't search every frame.
    /// </summary>
    public class GameObjectFinder
    {
        private GameObject _cachedPlayer;
        private Rigidbody _cachedPlayerRigidbody;
        private EHS.PlayerInputHandler _cachedInputHandler;
        private EHS.PlayerMovement _cachedMovement;
        private GameObject _cachedCamera;

        private float _playerSearchCooldown = 0f;
        private float _cameraSearchCooldown = 0f;
        private const float SEARCH_COOLDOWN_DURATION = 2f;

        public Rigidbody GetCachedPlayerRigidbody() => _cachedPlayerRigidbody;

        public Transform FindPlayerTransform() => FindPlayer()?.transform;

        public EHS.PlayerInputHandler GetInputHandler()
        {
            if (_cachedInputHandler == null && FindPlayer() != null)
                _cachedInputHandler = _cachedPlayer.GetComponent<EHS.PlayerInputHandler>();
            return _cachedInputHandler;
        }

        public EHS.PlayerMovement GetMovement()
        {
            if (_cachedMovement == null && FindPlayer() != null)
                _cachedMovement = _cachedPlayer.GetComponent<EHS.PlayerMovement>();
            return _cachedMovement;
        }

        public GameObject FindPlayer()
        {
            if (_cachedPlayer != null)
                return _cachedPlayer;

            if (Time.unscaledTime < _playerSearchCooldown)
                return null;

            // By tag first (fast path; works on the demo).
            try
            {
                var tagged = GameObject.FindWithTag("Player");
                if (tagged != null)
                {
                    SetPlayer(tagged);
                    return _cachedPlayer;
                }
            }
            catch (Exception ex)
            {
                // Throws when the tag isn't defined in this game version — expected, fall through.
                TASPlugin.WarnOnce("find-player-tag", $"Player tag lookup failed ({ex.Message}); using component lookup.");
            }

            // By component — version-agnostic. The tag changed in v0.12, but the player
            // still carries EHS.PlayerInputHandler / EHS.PlayerMovement.
            try
            {
                var handler = UnityEngine.Object.FindObjectOfType<EHS.PlayerInputHandler>();
                Component comp = handler != null
                    ? handler
                    : UnityEngine.Object.FindObjectOfType<EHS.PlayerMovement>();
                if (comp != null)
                {
                    SetPlayer(comp.gameObject);
                    return _cachedPlayer;
                }
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("find-player-comp", $"Player component lookup failed: {ex.Message}");
            }

            _playerSearchCooldown = Time.unscaledTime + SEARCH_COOLDOWN_DURATION;
            return null;
        }

        private void SetPlayer(GameObject go)
        {
            _cachedPlayer = go;
            _cachedInputHandler = null;
            _cachedMovement = null;
            // The rigidbody may sit on a parent/child depending on the game version.
            // Explicit Unity null checks: `??` would skip the native-null check.
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.GetComponentInParent<Rigidbody>();
            if (rb == null) rb = go.GetComponentInChildren<Rigidbody>();
            _cachedPlayerRigidbody = rb;
        }

        public GameObject FindCamera()
        {
            if (_cachedCamera != null)
                return _cachedCamera;

            if (Time.unscaledTime < _cameraSearchCooldown)
                return null;

            var main = Camera.main;
            if (main != null)
            {
                _cachedCamera = main.gameObject;
                return _cachedCamera;
            }

            _cameraSearchCooldown = Time.unscaledTime + SEARCH_COOLDOWN_DURATION;
            return null;
        }

        public void ClearCache()
        {
            _cachedPlayer = null;
            _cachedPlayerRigidbody = null;
            _cachedInputHandler = null;
            _cachedMovement = null;
            _cachedCamera = null;
            _playerSearchCooldown = 0f;
            _cameraSearchCooldown = 0f;
        }
    }
}
