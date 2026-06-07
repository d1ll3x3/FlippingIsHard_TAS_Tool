using System;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public class TimeController
    {
        public bool IsPaused     { get; private set; } = false;
        public bool IsSlowMo     { get; private set; } = false;
        public bool IsSlowMoBoost{ get; private set; } = false;

        private int   _framesToAdvance = 0;
        private float _lastAdvanceTime = -1f;

        // Slow-motion speeds
        private const float SlowMoNormal = 0.1f;  // ×0.1
        private const float SlowMoFast   = 0.3f;  // ×0.3  (boost)

        // Frame-advance hold timing
        private const float AdvanceHoldDelay    = 0.4f;   // seconds before hold-repeat starts
        private const float AdvanceHoldInterval = 0.1f;   // seconds between repeat ticks (10/s)

        // Actual physics tick counter
        public ulong CurrentTick { get; private set; } = 0;

        public void ResetTick()
        {
            CurrentTick       = 0;
            _framesToAdvance  = 0;
            _lastAdvanceTime  = -1f;
            IsPaused          = false;
            IsSlowMo          = false;
            IsSlowMoBoost     = false;
            ApplyTimeScale();
        }

        public void SetTick(ulong newTick) => CurrentTick = newTick;

        // ── Pause ─────────────────────────────────────────────────────────
        public void TogglePause()
        {
            IsPaused = !IsPaused;
            if (!IsPaused) _lastAdvanceTime = -1f;
            ApplyTimeScale();
            TASPlugin.Logger.LogInfo($"TAS Paused: {IsPaused}");
        }

        // ── Frame Advance ─────────────────────────────────────────────────
        /// <summary>
        /// Call every Update() while the FrameAdvance key is held.
        /// justPressed = true only on the first frame the key is down.
        /// First press → 1 tick immediately. Hold 0.4s → 10 ticks/sec.
        /// </summary>
        public void TickFrameAdvance(bool justPressed)
        {
            if (!IsPaused) return;

            float now = Time.unscaledTime;

            if (justPressed)
            {
                _framesToAdvance = 1;
                _lastAdvanceTime = now;
                ApplyTimeScale();
                return;
            }

            // Auto-repeat after hold delay
            float heldFor = now - _lastAdvanceTime;
            if (heldFor >= AdvanceHoldDelay)
            {
                _lastAdvanceTime += AdvanceHoldInterval;
                _framesToAdvance  = 1;
                ApplyTimeScale();
            }
        }

        // ── Slow Motion ───────────────────────────────────────────────────
        public void ToggleSlowMo()
        {
            IsSlowMo = !IsSlowMo;
            if (!IsSlowMo) IsSlowMoBoost = false;
            ApplyTimeScale();
            TASPlugin.Logger.LogInfo($"SlowMo: {IsSlowMo}");
        }

        /// <summary>
        /// Called every Update() frame to keep boost state in sync with the key.
        /// Only has effect while IsSlowMo is true.
        /// </summary>
        public void SetSlowMoBoost(bool active)
        {
            if (!IsSlowMo) return;
            if (IsSlowMoBoost == active) return; // no change, avoid redundant timeScale writes
            IsSlowMoBoost = active;
            ApplyTimeScale();
        }

        // ── Time Scale ────────────────────────────────────────────────────
        private void ApplyTimeScale()
        {
            if (_framesToAdvance > 0)
                Time.timeScale = 1f;
            else if (IsPaused)
                Time.timeScale = 0f;
            else if (IsSlowMo)
                Time.timeScale = IsSlowMoBoost ? SlowMoFast : SlowMoNormal;
            else
                Time.timeScale = 1f;
        }

        // ── FixedUpdate ───────────────────────────────────────────────────
        // Called from TASController.FixedUpdate (runs every physics step).
        public void FixedUpdate()
        {
            if (Time.timeScale > 0f)
                CurrentTick++;

            if (_framesToAdvance > 0)
            {
                _framesToAdvance--;
                if (_framesToAdvance <= 0)
                    ApplyTimeScale(); // re-pause since IsPaused is still true
            }
        }
    }
}
