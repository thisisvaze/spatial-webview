using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpatialWebView
{
    /// <summary>
    /// Oleg-style spatial pill button/handle:
    /// - Instant colour change on hover/press/active (colour leads, motion follows).
    /// - 0.3s implicit spring swell (+8% hover, 0.90x press), completely disabled at rest.
    /// - Procedural anti-aliased 9-sliced rounded pill sprite (zero external PNG assets).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialPillControl :
        MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler
    {
        private static Sprite sharedPillSprite;
        private static Sprite sharedFrameSprite;

        private Image background;
        private Transform visualTransform;
        private SpatialSpring scaleSpring = new SpatialSpring(1f);

        private Color restColor = new Color(0.14f, 0.16f, 0.22f, 0.88f);
        private Color hoverColor = new Color(0.26f, 0.31f, 0.42f, 0.98f);
        private Color activeColor = new Color(0.46f, 0.36f, 0.92f, 0.96f);
        private Color heldColor = new Color(0.62f, 0.48f, 1.00f, 1.00f);

        private bool hovered;
        private bool pressed;
        private bool toggledActive;
        private bool interactable = true;

        public event Action Clicked;

        public bool Interactable
        {
            get => interactable;
            set
            {
                if (interactable == value) return;
                interactable = value;
                if (!interactable)
                {
                    hovered = false;
                    pressed = false;
                }
                RefreshVisualTarget();
            }
        }

        public bool ToggledActive
        {
            get => toggledActive;
            set
            {
                if (toggledActive == value) return;
                toggledActive = value;
                RefreshColorInstant();
            }
        }

        public void Configure(
            Image targetImage,
            Color rest,
            Color hover,
            Color active,
            bool swellOnHover = true)
        {
            background = targetImage;
            visualTransform = transform;
            restColor = rest;
            hoverColor = hover;
            activeColor = active;
            SwellingEnabled = swellOnHover;
            RefreshColorInstant();
            enabled = false;
        }

        public bool SwellingEnabled { get; set; } = true;

        public void SetHoveredExternal(bool isHovered)
        {
            if (!interactable) isHovered = false;
            if (hovered == isHovered) return;
            hovered = isHovered;
            RefreshVisualTarget();
        }

        public void SetPressedExternal(bool isPressed)
        {
            if (!interactable) isPressed = false;
            if (pressed == isPressed) return;
            pressed = isPressed;
            RefreshVisualTarget();
        }

        public void TriggerClickExternal()
        {
            if (!interactable) return;
            Clicked?.Invoke();
        }

        public void OnPointerEnter(PointerEventData eventData) => SetHoveredExternal(true);
        public void OnPointerExit(PointerEventData eventData) { SetPressedExternal(false); SetHoveredExternal(false); }
        public void OnPointerDown(PointerEventData eventData) => SetPressedExternal(true);
        public void OnPointerUp(PointerEventData eventData) => SetPressedExternal(false);
        public void OnPointerClick(PointerEventData eventData) => TriggerClickExternal();

        private void Update()
        {
            scaleSpring.Step(
                Time.unscaledDeltaTime,
                WebViewSpatialMotion.Quick,
                WebViewSpatialMotion.SoftDamping);

            if (visualTransform != null)
            {
                visualTransform.localScale = Vector3.one * scaleSpring.Value;
            }

            if (scaleSpring.AtRest)
            {
                enabled = false;
            }
        }

        private void RefreshVisualTarget()
        {
            RefreshColorInstant();

            float targetScale = 1f;
            if (pressed)
            {
                targetScale = WebViewSpatialMotion.PillPressScale;
            }
            else if (hovered && SwellingEnabled)
            {
                targetScale = 1f + WebViewSpatialMotion.PillHoverSwell;
            }

            scaleSpring.Target = targetScale;
            if (!scaleSpring.AtRest)
            {
                enabled = true;
            }
        }

        private void RefreshColorInstant()
        {
            if (background == null) return;
            if (!interactable)
            {
                Color dim = restColor;
                dim.a *= 0.42f;
                background.color = dim;
                return;
            }

            if (pressed)
            {
                background.color = heldColor;
            }
            else if (hovered)
            {
                background.color = toggledActive
                    ? Color.Lerp(activeColor, Color.white, 0.18f)
                    : hoverColor;
            }
            else
            {
                background.color = toggledActive ? activeColor : restColor;
            }
        }

        public static Sprite GetPillSprite()
        {
            if (sharedPillSprite != null) return sharedPillSprite;

            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "SpatialWebView_Pill",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float radius = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = Mathf.Clamp01(radius + 0.5f - dist);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();

            sharedPillSprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(15f, 15f, 15f, 15f));
            return sharedPillSprite;
        }

        public static Sprite GetThinBorderSprite()
        {
            if (sharedFrameSprite != null) return sharedFrameSprite;

            const int size = 48;
            const float outerRadius = 14f;
            const float strokePx = 2.4f;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "SpatialWebView_FrameBorder",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            float half = size * 0.5f;
            Vector2 boxHalf = new Vector2(half - outerRadius - 1f, half - outerRadius - 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(Mathf.Abs(x + 0.5f - half), Mathf.Abs(y + 0.5f - half)) - boxHalf;
                    float sdf = Vector2.Max(p, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(p.x, p.y), 0f) - outerRadius;
                    float outerAlpha = Mathf.Clamp01(0.5f - sdf);
                    float innerAlpha = Mathf.Clamp01(0.5f - (sdf + strokePx));
                    float ringAlpha = Mathf.Clamp01(outerAlpha - innerAlpha);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, ringAlpha));
                }
            }
            tex.Apply();

            sharedFrameSprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(16f, 16f, 16f, 16f));
            return sharedFrameSprite;
        }

        public static void ApplyPillRounding(Image image, Vector2 sizePx)
        {
            image.sprite = GetPillSprite();
            image.type = Image.Type.Sliced;
            float shortSide = Mathf.Max(4f, Mathf.Min(sizePx.x, sizePx.y));
            image.pixelsPerUnitMultiplier = 30f / shortSide;
        }
    }
}
