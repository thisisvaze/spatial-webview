using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialWebView
{
    public enum ReaderTheme
    {
        Dark = 0,
        Sepia = 1,
        Light = 2
    }

    public enum CompositorLayerMode
    {
        /// <summary>
        /// Renders the browser as a hardware Underlay behind Unity's eye buffer and punches an
        /// RGBA=(0,0,0,0) window in the viewport mesh so 3D hands, avatars, and rays render in front.
        /// </summary>
        UnderlayPunchThrough = 0,

        /// <summary>
        /// Renders the browser on top of all 3D scene geometry.
        /// </summary>
        OverlayOnTop = 1
    }

    public enum SurfaceBackendMode
    {
        /// <summary>
        /// Automatically picks the best available path on the active device:
        /// 1. Meta OVROverlay (Quest 2, Pro, 3, 3S, Horizon OS)
        /// 2. OpenXR Composition Layers (Pico, Android XR, Vive, Snapdragon Spaces)
        /// 3. Universal Direct-Buffer Texture2D (AR/VR smart glasses like XREAL, Viture, RayNeo)
        /// </summary>
        Auto = 0,
        MetaOVROverlay = 1,
        OpenXRCompositionLayer = 2,
        UniversalTexture = 3
    }

    public readonly struct WebPageState
    {
        public readonly string Url;
        public readonly string Title;
        public readonly bool CanGoBack;
        public readonly bool CanGoForward;
        public readonly bool IsLoading;
        public readonly int Progress;
        public readonly int ErrorCode;

        public WebPageState(
            string url,
            string title,
            bool canGoBack,
            bool canGoForward,
            bool isLoading,
            int progress,
            int errorCode)
        {
            Url = url ?? string.Empty;
            Title = title ?? string.Empty;
            CanGoBack = canGoBack;
            CanGoForward = canGoForward;
            IsLoading = isLoading;
            Progress = progress;
            ErrorCode = errorCode;
        }
    }

    public static class AndroidMotionActions
    {
        public const int Down = 0;
        public const int Up = 1;
        public const int Move = 2;
        public const int Cancel = 3;

        public const int HoverMove = 7;
        public const int HoverEnter = 9;
        public const int HoverExit = 10;
    }

    public interface ISpatialWebViewBackend : IDisposable
    {
        bool IsReady { get; }
        WebPageState CurrentState { get; }

        event Action Ready;
        event Action<WebPageState> PageStateChanged;
        event Action<string> MessageReceived;
        event Action<string> ErrorOccurred;

        void Initialize(int width, int height, int activeFps, int idleFps, string userAgentOverride);
        void AttachSurface(IntPtr androidSurfaceJObject, int width, int height);
        void DetachSurface();

        void EnableTextureFallback(int width, int height);
        void DisableTextureFallback();
        bool TryUploadFallbackFrame(Texture2D targetTexture, ref int lastSequence);

        void Resize(int width, int height);

        void LoadUrl(string url);
        void LoadHtml(string html, string baseUrl);
        Task<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default);

        void GoBack();
        void GoForward();
        void Reload();
        void StopLoading();

        void SetTextZoom(int percent);
        void SetBackgroundColor(Color32 color);
        void SetFrameRates(int activeFps, int idleFps);

        void DispatchPointer(int motionAction, float normalizedX, float normalizedY);
        void DispatchHover(int hoverAction, float normalizedX, float normalizedY);
        void DispatchScroll(float normalizedX, float normalizedY, float deltaX, float deltaY);
        void BlurInput();
        void SetPaused(bool paused);

        void PumpMainThreadQueue();
    }
}
