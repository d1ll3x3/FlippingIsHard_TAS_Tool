using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlippingIsHardTAS
{
    public class GameObjectFinder
    {
        // Cached references
        private GameObject _cachedPlayer;
        private Rigidbody _cachedPlayerRigidbody;
        private GameObject _cachedCamera;
        
        // Tiempo de espera (cooldown) si la bÃºsqueda falla, para no inundar el log ni causar lag
        private float _playerSearchCooldown = 0f;
        private float _cameraSearchCooldown = 0f;
        private const float SEARCH_COOLDOWN_DURATION = 2f;
        
        public Rigidbody GetCachedPlayerRigidbody() => _cachedPlayerRigidbody;
        
        public Transform FindPlayerTransform()
        {
            var player = FindPlayer();
            return player?.transform;
        }
        
        public Transform FindCameraTransform()
        {
            var camera = FindCamera();
            return camera?.transform;
        }
        
        public GameObject FindPlayer()
        {
            // Return cached player if still valid
            if (_cachedPlayer != null)
                return _cachedPlayer;
                
            // Check cooldown si ha fallado recientemente
            if (Time.unscaledTime < _playerSearchCooldown)
                return null;
            
            // Method 1: by tag (fast path; works on v0.11)
            try
            {
                _cachedPlayer = GameObject.FindWithTag("Player");
                if (_cachedPlayer != null)
                {
                    _cachedPlayerRigidbody = _cachedPlayer.GetComponent<Rigidbody>();
                    return _cachedPlayer;
                }
            }
            catch (Exception) { /* Silent catch */ }

            // Method 2: by component — version-agnostic. The tag/structure changed in v0.12
            // ("Player not found" with the tag lookup), but the player still carries
            // EHS.PlayerInputHandler / EHS.PlayerMovement. Find it by those instead.
            try
            {
                var handler = UnityEngine.Object.FindObjectOfType<EHS.PlayerInputHandler>();
                Component comp = handler != null
                    ? (Component)handler
                    : UnityEngine.Object.FindObjectOfType<EHS.PlayerMovement>();
                if (comp != null)
                {
                    _cachedPlayer = comp.gameObject;
                    _cachedPlayerRigidbody = _cachedPlayer.GetComponent<Rigidbody>()
                                             ?? _cachedPlayer.GetComponentInParent<Rigidbody>()
                                             ?? _cachedPlayer.GetComponentInChildren<Rigidbody>();
                    return _cachedPlayer;
                }
            }
            catch (Exception) { /* Silent catch */ }

            // Si llegamos aquÃ­, no lo encontrÃ³, aplicamos cooldown de 2 segundos antes de volver a buscar
            _playerSearchCooldown = Time.unscaledTime + SEARCH_COOLDOWN_DURATION;
            _cachedPlayerRigidbody = null;
            return null;
        }
        
        public GameObject FindCamera()
        {
            // Return cached camera if still valid
            if (_cachedCamera != null)
                return _cachedCamera;
                
            // Check cooldown si ha fallado recientemente
            if (Time.unscaledTime < _cameraSearchCooldown)
                return null;
            
            // Method 1: by tag
            try
            {
                _cachedCamera = GameObject.FindWithTag("MainCamera");
                if (_cachedCamera != null)
                {
                    return _cachedCamera;
                }
            }
            catch (Exception) { /* Silent catch */ }

            // Method 2: Camera.main — version-agnostic fallback
            try
            {
                if (Camera.main != null)
                {
                    _cachedCamera = Camera.main.gameObject;
                    return _cachedCamera;
                }
            }
            catch (Exception) { /* Silent catch */ }

            // Si llegamos aquÃ­, no lo encontrÃ³, aplicamos cooldown de 2 segundos antes de volver a buscar
            _cameraSearchCooldown = Time.unscaledTime + SEARCH_COOLDOWN_DURATION;
            return null;
        }
        
        public void ClearCache()
        {
            _cachedPlayer = null;
            _cachedPlayerRigidbody = null;
            _cachedCamera = null;
            _playerSearchCooldown = 0f;
            _cameraSearchCooldown = 0f;
        }
    }
}
