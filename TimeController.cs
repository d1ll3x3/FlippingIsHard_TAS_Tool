using UnityEngine;

namespace FlippingIsHardTAS
{
    public class TimeController
    {
        public bool IsPaused      { get; private set; } = false;
        public bool IsSlowMo      { get; private set; } = false;
        public bool IsSlowMoBoost { get; private set; } = false;
        public bool IsFastForward { get; private set; } = false;

        private int   _framesToAdvance = 0;
        private float _lastAdvanceTime = -1f;

        private const float SlowMoNormal = 0.1f;
        private const float SlowMoFast   = 0.3f;
        private const float FastForwardScale = 3f;

        // Frame-advance hold timing: first press = 1 tick, then after the delay 10 ticks/s.
        public const float HoldDelay    = 0.4f;
        public const float HoldInterval = 0.1f;

        /// <summary>Physics ticks elapsed (only counts while time is running).</summary>
        public ulong CurrentTick { get; private set; } = 0;

        /// <summary>Speed multiplier currently applied by the TAS (for the HUD).</summary>
        public float SpeedFactor
            => IsSlowMo ? (IsSlowMoBoost ? SlowMoFast : SlowMoNormal) : IsFastForward ? FastForwardScale : 1f;

        public void ResetTick()
        {
            CurrentTick       = 0;
            _framesToAdvance  = 0;
            _lastAdvanceTime  = -1f;
            IsPaused          = false;
            IsSlowMo          = false;
            IsSlowMoBoost     = false;
            IsFastForward     = false;
            ApplyTimeScale();
        }

        public void SetTick(ulong newTick) => CurrentTick = newTick;

        public void SetPaused(bool paused)
        {
            if (IsPaused != paused) TogglePause();
        }

        public void TogglePause()
        {
            IsPaused = !IsPaused;
            if (!IsPaused) _lastAdvanceTime = -1f;
            ApplyTimeScale();
        }

        /// <summary>
        /// Call every Update() while the FrameAdvance key is held (justPressed only on the
        /// first frame). First press → 1 tick immediately; hold → 10 ticks/s after 0.4 s.
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

            if (now - _lastAdvanceTime >= HoldDelay)
            {
                _lastAdvanceTime += HoldInterval;
                _framesToAdvance  = 1;
                ApplyTimeScale();
            }
        }

        public void ToggleSlowMo()
        {
            IsSlowMo = !IsSlowMo;
            if (!IsSlowMo) IsSlowMoBoost = false;
            if (IsSlowMo) IsFastForward = false;
            ApplyTimeScale();
        }

        /// <summary>Keeps the boost state in sync with its key; only meaningful in slow-mo.</summary>
        public void SetSlowMoBoost(bool active)
        {
            if (!IsSlowMo || IsSlowMoBoost == active) return;
            IsSlowMoBoost = active;
            ApplyTimeScale();
        }

        public void ToggleFastForward()
        {
            IsFastForward = !IsFastForward;
            if (IsFastForward) { IsSlowMo = false; IsSlowMoBoost = false; }
            ApplyTimeScale();
        }

        private void ApplyTimeScale()
        {
            if (_framesToAdvance > 0)
                Time.timeScale = 1f;
            else if (IsPaused)
                Time.timeScale = 0f;
            else
                Time.timeScale = SpeedFactor;
        }

        /// <summary>
        /// Re-asserts the pause if something external reset Time.timeScale. The game does
        /// this while it finishes loading after a quick restart.
        /// </summary>
        public void EnforcePause()
        {
            if (IsPaused && _framesToAdvance == 0 && Time.timeScale != 0f)
                ApplyTimeScale();
        }

        /// <summary>Called from TASController.FixedUpdate (every physics step).</summary>
        public void FixedUpdate()
        {
            if (Time.timeScale > 0f)
                CurrentTick++;

            if (_framesToAdvance > 0)
            {
                _framesToAdvance--;
                if (_framesToAdvance <= 0)
                    ApplyTimeScale();   // back to paused
            }
        }
    }
}
