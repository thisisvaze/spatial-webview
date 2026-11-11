using UnityEngine;

namespace SpatialWebView
{
    /// <summary>
    /// Oleg Frolov (Volorf/xr-prototypes) tuned implicit spring constants and integrator:
    /// - Show: 0.5s from 0.5x scale, damping 0.85 (soft settle, almost no bounce)
    /// - Hover / Hide / Press: 0.3s
    /// - Travel / Exit: damping 1.0 (critically damped, never overshoots)
    /// - Completely inert at rest (AtRest == true).
    /// </summary>
    public static class WebViewSpatialMotion
    {
        /// <summary>Global accessibility switch: springs snap straight to targets with zero motion.</summary>
        public static bool ReducedMotion;

        public const float Quick = 0.30f;          // hover, press, dismiss
        public const float Soft = 0.50f;           // materialize, spatial travel
        public const float SoftDamping = 0.85f;    // soft settle, almost no bounce
        public const float FirmDamping = 1.00f;    // travel & exits never overshoot

        public const float PopFromScale = 0.50f;   // materialize from half size
        public const float PillHoverSwell = 0.08f; // 8% swell on small pill controls (viewport stays still!)
        public const float PillPressScale = 0.90f; // 0.9x momentary button press
        public const float BoardGrabGive = 0.03f;  // subtle 3% give while holding the board handle
        public const float ApproachRangeMeters = 0.18f; // fingertip proximity ramp distance

        // Oleg's vr-slider haptic feel: 1-in-3 frame pulsed rattle (controllers only).
        public const float GrabBuzzAmplitude = 0.85f;
        public const float GrabBuzzSeconds = 0.20f;
        public const float ReleaseBuzzAmplitude = 0.42f;
        public const float ReleaseBuzzSeconds = 0.10f;
        public const float TapBuzzAmplitude = 0.55f;
        public const float TapBuzzSeconds = 0.08f;
    }

    /// <summary>
    /// Unconditionally stable implicit damped spring. Safe across Quest frame hitches
    /// and carries velocity seamlessly when retargeted mid-flight.
    /// </summary>
    public struct SpatialSpring
    {
        public float Value;
        public float Target;
        public float Velocity;

        public SpatialSpring(float initialValue)
        {
            Value = Target = initialValue;
            Velocity = 0f;
        }

        public bool AtRest => Value == Target && Velocity == 0f;

        public void Snap(float value)
        {
            Value = Target = value;
            Velocity = 0f;
        }

        public void Step(float deltaTime, float response, float damping)
        {
            if (WebViewSpatialMotion.ReducedMotion || response <= 0.0001f)
            {
                Value = Target;
                Velocity = 0f;
                return;
            }

            float omega = 2f * Mathf.PI / response;
            float f = 1f + 2f * deltaTime * damping * omega;
            float hoo = deltaTime * omega * omega;
            float hhoo = deltaTime * hoo;
            float inv = 1f / (f + hhoo);

            float nextValue = (f * Value + deltaTime * Velocity + hhoo * Target) * inv;
            Velocity = (Velocity + hoo * (Target - Value)) * inv;
            Value = nextValue;

            if (Mathf.Abs(Target - Value) < 0.0005f && Mathf.Abs(Velocity) < 0.005f)
            {
                Value = Target;
                Velocity = 0f;
            }
        }
    }
}
