using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine.XR.OpenXR.CompositionLayers;
#endif

namespace SpatialWebView
{
    /// <summary>
    /// Quiet, spatial-first web, reader & video board across Android XR headsets and smart glasses:
    /// - Meta Quest 2, Pro, 3, 3S (OVROverlay External Surface)
    /// - OpenXR headsets: Pico, Android XR, Vive, Snapdragon Spaces (XR Composition Layers AndroidSurface)
    /// - AR/VR smart glasses: XREAL, Viture, RayNeo (automatic Direct-ByteBuffer Texture2D fallback)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpatialWebViewWindow : MonoBehaviour
    {
        private static readonly Color BorderRestColor = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color BorderHoverColor = new Color(0.86f, 0.93f, 1f, 0.72f);
        private static readonly Color BorderHeldViolet = new Color(0.62f, 0.48f, 1.00f, 0.96f);

        private static readonly Color PillRestColor = new Color(0.11f, 0.13f, 0.18f, 0.88f);
        private static readonly Color PillHoverColor = new Color(0.22f, 0.27f, 0.37f, 0.98f);
        private static readonly Color PillActiveViolet = new Color(0.46f, 0.35f, 0.92f, 0.96f);

        [Header("Initial Content")]
        [SerializeField] private string initialUrl = "https://en.wikipedia.org/wiki/Meta_Quest_3";
        [SerializeField] private bool openOnStart = true;
        [SerializeField] private bool autoFormatVideos = true;
        [SerializeField] private bool readerModeEnabled = true;
        [SerializeField] private ReaderTheme readerTheme = ReaderTheme.Dark;
        [SerializeField] private string embedOriginUrl = WebContentFormatter.DefaultEmbedOrigin;

        [Header("Spatial Board Layout")]
        [SerializeField] private Vector2 panelSizeMeters = new Vector2(1.00f, 0.625f);
        [SerializeField] private Vector2Int pixelResolution = new Vector2Int(1600, 1000);
        [SerializeField] private bool showBottomDock = true;
        [SerializeField] private bool enableBuiltInXRPointer = true;
        [SerializeField] private bool enableGlassesGazeFallback = true;

        [Header("Compositor & Cross-Headset Backend")]
        [SerializeField] private SurfaceBackendMode surfaceBackendMode = SurfaceBackendMode.Auto;
        [SerializeField] private CompositorLayerMode compositorMode = CompositorLayerMode.UnderlayPunchThrough;
        [SerializeField] private int underlayCompositionDepth = 10;
        [SerializeField] private bool useBicubicFiltering = true;
        [Range(15, 90)] [SerializeField] private int activeFps = 60;
        [Range(1, 60)] [SerializeField] private int idleFps = 15;
        [Range(60, 200)] [SerializeField] private int textZoomPercent = 115;
        [SerializeField] private string userAgentOverride = string.Empty;

        private ISpatialWebViewBackend backend;
        private SurfaceBackendMode resolvedBackendMode;
        private Transform motionPivot;
        private Transform viewportRoot;
        private MeshRenderer viewportRenderer;
        private BoxCollider viewportCollider;
        private Material punchMaterial;
        private Material fallbackTextureMaterial;
        private Texture2D fallbackTexture;
        private int lastUploadedFallbackSeq;
        private bool fallbackActive;
        private int compositorSurfaceWaitFrames;
        private SpatialWebViewPointerInput pointerInput;

        private Canvas uiCanvas;
        private Image perimeterBorderImage;
        private RectTransform dockRect;
        private CanvasGroup sidePillsGroup;
        private RectTransform grabHandleRect;
        private TextMeshProUGUI handleCaptionLabel;
        private SpatialPillControl backPill;
        private SpatialPillControl readerPill;
        private TextMeshProUGUI readerPillLabel;
        private SpatialPillControl typographyPill;
        private TextMeshProUGUI typographyPillLabel;

        // Implicit spring state (inert at rest)
        private SpatialSpring presenceSpring = new SpatialSpring(1f);
        private SpatialSpring sizeRatioSpring = new SpatialSpring(1f);
        private SpatialSpring travelSpring = new SpatialSpring(0f);
        private SpatialSpring holdGiveSpring = new SpatialSpring(0f);
        private SpatialSpring dockRevealSpring = new SpatialSpring(0f);
        private Vector3 travelWorldOffset;
        private bool dismissing;
        private float currentNearness;
        private bool currentlyHeld;

        // Editor / non-Android Play Mode live preview UI
        private GameObject editorPreviewRoot;
        private RawImage editorThumbnailImage;
        private TextMeshProUGUI editorStatusText;
        private Texture2D downloadedPreviewTex;
        private Coroutine previewCoroutine;

        private string pendingInitialActionUrl;
        private string pendingReaderTitle;
        private string pendingReaderHtml;
        private string pendingRawHtml;
        private string pendingRawBaseUrl;
        private string lastRequestedWebUrl = string.Empty;
        private bool isCurrentContentVideo;
        private bool surfaceAttached;
        private int typographyStepIndex = 1;

#if SPATIAL_WEBVIEW_META_OVR
        private OVROverlay ovrOverlay;
#endif

#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
        private CompositionLayer openXrCompositionLayer;
        private TexturesExtension openXrTexturesExt;
#endif

        public ISpatialWebViewBackend Backend => backend;
        public SurfaceBackendMode ActiveSurfaceMode => resolvedBackendMode;
        public WebPageState PageState => backend != null ? backend.CurrentState : default;
        public bool ReaderModeEnabled => readerModeEnabled;
        public ReaderTheme CurrentTheme => readerTheme;
        public int TextZoomPercent => textZoomPercent;

        public event Action<WebPageState> PageStateChanged;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorOccurred;
        public event Action Closed;

        private void Awake()
        {
            BuildRigIfNeeded();
            CreateBackend();
        }

        private void Start()
        {
            if (openOnStart && !string.IsNullOrWhiteSpace(initialUrl) &&
                string.IsNullOrEmpty(pendingInitialActionUrl) &&
                pendingReaderHtml == null &&
                pendingRawHtml == null)
            {
                OpenUrl(initialUrl);
            }
        }

        private void OnEnable()
        {
            dismissing = false;
            presenceSpring.Value = WebViewSpatialMotion.ReducedMotion ? 1f : WebViewSpatialMotion.PopFromScale;
            presenceSpring.Target = 1f;
            presenceSpring.Velocity = 0f;
            ApplySpringPose();

            if (backend != null)
            {
                backend.SetPaused(false);
            }
            ConfigurePresentationPath();
        }

        private void OnDisable()
        {
            TeardownPresentationPath();
            if (backend != null)
            {
                backend.SetPaused(true);
            }
        }

        private void Update()
        {
            backend?.PumpMainThreadQueue();
            PollPresentationOutput();
            StepSpatialSprings();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            backend?.SetPaused(pauseStatus);
        }

        private void OnDestroy()
        {
            TeardownPresentationPath();
            if (backend != null)
            {
                backend.Ready -= OnBackendReady;
                backend.PageStateChanged -= OnBackendPageStateChanged;
                backend.MessageReceived -= OnBackendMessageReceived;
                backend.ErrorOccurred -= OnBackendError;
                backend.Dispose();
                backend = null;
            }

            if (punchMaterial != null) Destroy(punchMaterial);
            if (fallbackTextureMaterial != null) Destroy(fallbackTextureMaterial);
            if (fallbackTexture != null) Destroy(fallbackTexture);
            if (downloadedPreviewTex != null) Destroy(downloadedPreviewTex);
        }

        public void GlideFrom(Vector3 previousWorldPosition, float previousSizeRatio = 1f)
        {
            if (dismissing || WebViewSpatialMotion.ReducedMotion) return;
            travelWorldOffset = previousWorldPosition - transform.position + travelWorldOffset * travelSpring.Value;
            travelSpring.Value = 1f;
            travelSpring.Target = 0f;
            travelSpring.Velocity = 0f;

            sizeRatioSpring.Value = Mathf.Clamp(previousSizeRatio, 0.25f, 3f);
            sizeRatioSpring.Target = 1f;
            sizeRatioSpring.Velocity = 0f;
            ApplySpringPose();
        }

        public void OpenUrl(string urlOrSearch, bool? forceReaderMode = null)
        {
            ShowIfHidden();

            if (forceReaderMode.HasValue)
            {
                readerModeEnabled = forceReaderMode.Value;
            }

            string normalized = WebContentFormatter.NormalizeUrlOrSearch(urlOrSearch);
            lastRequestedWebUrl = normalized;

            if (autoFormatVideos)
            {
                if (WebContentFormatter.TryExtractYouTubeVideoId(normalized, out string ytId, out int startSec))
                {
                    OpenYouTube(ytId, autoplay: true, startSeconds: startSec);
                    return;
                }

                if (WebContentFormatter.TryExtractVimeoVideoId(normalized, out string vimeoId))
                {
                    isCurrentContentVideo = true;
                    RefreshDockState();
                    LoadRawHtml(WebContentFormatter.BuildVimeoEmbedHtml(vimeoId, autoplay: true), embedOriginUrl);
                    UpdateEditorPreview($"Vimeo · {vimeoId}", normalized, null);
                    return;
                }

                if (WebContentFormatter.IsDirectVideoUrl(normalized))
                {
                    isCurrentContentVideo = true;
                    RefreshDockState();
                    LoadRawHtml(WebContentFormatter.BuildDirectVideoHtml(normalized, autoplay: true), embedOriginUrl);
                    UpdateEditorPreview("Video", normalized, null);
                    return;
                }
            }

            isCurrentContentVideo = false;
            RefreshDockState();
            pendingReaderHtml = null;
            pendingRawHtml = null;

            if (backend == null || !backend.IsReady)
            {
                pendingInitialActionUrl = normalized;
                return;
            }

            backend.LoadUrl(normalized);
            UpdateEditorPreview(FormatCompactDomainOrTitle(normalized, null), normalized, null);
        }

        public void OpenYouTube(string urlOrVideoId, bool autoplay = true, int startSeconds = 0)
        {
            ShowIfHidden();

            string videoId = urlOrVideoId != null ? urlOrVideoId.Trim() : string.Empty;
            if (WebContentFormatter.TryExtractYouTubeVideoId(videoId, out string parsedId, out int parsedStart))
            {
                videoId = parsedId;
                if (startSeconds <= 0) startSeconds = parsedStart;
            }

            string html;
            try
            {
                html = WebContentFormatter.BuildYouTubeEmbedHtml(videoId, embedOriginUrl, autoplay, startSeconds);
            }
            catch (ArgumentException)
            {
                Debug.LogWarning($"[SpatialWebView] '{urlOrVideoId}' is not a valid YouTube URL/ID; opening as standard URL.", this);
                OpenUrl(urlOrVideoId);
                return;
            }

            isCurrentContentVideo = true;
            lastRequestedWebUrl = $"https://www.youtube.com/watch?v={videoId}";
            RefreshDockState();
            LoadRawHtml(html, WebContentFormatter.NormalizeOrigin(embedOriginUrl));
            UpdateEditorPreview(
                $"YouTube · {videoId}",
                lastRequestedWebUrl,
                $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg");
        }

        public void OpenReaderHtml(string title, string bodyHtml, string sourceUrl = null)
        {
            ShowIfHidden();
            isCurrentContentVideo = false;
            readerModeEnabled = true;
            lastRequestedWebUrl = sourceUrl ?? string.Empty;
            RefreshDockState();

            string html = WebContentFormatter.BuildReaderHtml(title, bodyHtml, sourceUrl, readerTheme);
            if (backend == null || !backend.IsReady)
            {
                pendingReaderTitle = title;
                pendingReaderHtml = bodyHtml;
                return;
            }

            backend.LoadHtml(html, string.IsNullOrWhiteSpace(sourceUrl) ? embedOriginUrl : sourceUrl);
            UpdateEditorPreview(title, sourceUrl ?? "Reader", null);
        }

        public void LoadRawHtml(string html, string baseUrl = null)
        {
            pendingInitialActionUrl = null;
            pendingReaderHtml = null;

            string safeBase = string.IsNullOrWhiteSpace(baseUrl)
                ? WebContentFormatter.NormalizeOrigin(embedOriginUrl)
                : baseUrl;

            if (backend == null || !backend.IsReady)
            {
                pendingRawHtml = html ?? string.Empty;
                pendingRawBaseUrl = safeBase;
                return;
            }

            backend.LoadHtml(html ?? string.Empty, safeBase);
        }

        public void ToggleReaderMode() => SetReaderMode(!readerModeEnabled);

        public void SetReaderMode(bool enabled)
        {
            if (readerModeEnabled == enabled) return;
            readerModeEnabled = enabled;
            RefreshDockState();

            if (backend == null || !backend.IsReady || isCurrentContentVideo)
                return;

            if (enabled)
            {
                _ = backend.EvaluateJavaScriptAsync(WebContentFormatter.BuildLiveReaderModeScript(readerTheme));
            }
            else if (!string.IsNullOrEmpty(lastRequestedWebUrl))
            {
                backend.LoadUrl(lastRequestedWebUrl);
            }
            else
            {
                backend.Reload();
            }
        }

        public void CycleTypographyPreset()
        {
            typographyStepIndex = (typographyStepIndex + 1) % 4;
            switch (typographyStepIndex)
            {
                case 0:
                    SetTextZoom(100);
                    SetTheme(ReaderTheme.Dark);
                    break;
                case 1:
                    SetTextZoom(115);
                    SetTheme(ReaderTheme.Dark);
                    break;
                case 2:
                    SetTextZoom(130);
                    SetTheme(ReaderTheme.Sepia);
                    break;
                case 3:
                    SetTextZoom(115);
                    SetTheme(ReaderTheme.Light);
                    break;
            }
        }

        public void CycleTheme() => SetTheme((ReaderTheme)(((int)readerTheme + 1) % 3));

        public void SetTheme(ReaderTheme theme)
        {
            readerTheme = theme;
            ApplyBackgroundThemeColor();
            RefreshDockState();
            if (backend != null && backend.IsReady && readerModeEnabled && !isCurrentContentVideo)
            {
                _ = backend.EvaluateJavaScriptAsync(WebContentFormatter.BuildThemeUpdateScript(readerTheme));
            }
        }

        public void ZoomIn() => SetTextZoom(textZoomPercent + 15);
        public void ZoomOut() => SetTextZoom(textZoomPercent - 15);

        public void SetTextZoom(int percent)
        {
            textZoomPercent = Mathf.Clamp(percent, 60, 200);
            backend?.SetTextZoom(textZoomPercent);
            RefreshDockState();
        }

        public void SetVideoPlaying(bool play)
        {
            if (backend == null || !backend.IsReady) return;
            string cmd = play ? "playVideo" : "pauseVideo";
            _ = backend.EvaluateJavaScriptAsync(
                $"if(window.xrPlayerCommand)window.xrPlayerCommand('{cmd}');");
        }

        public Task<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            if (backend == null) return Task.FromResult<string>(null);
            return backend.EvaluateJavaScriptAsync(script, cancellationToken);
        }

        public void GoBack() => backend?.GoBack();
        public void GoForward() => backend?.GoForward();
        public void Reload() => backend?.Reload();

        public void CloseWindow()
        {
            if (dismissing) return;
            if (WebViewSpatialMotion.ReducedMotion || !gameObject.activeInHierarchy)
            {
                gameObject.SetActive(false);
                Closed?.Invoke();
                return;
            }

            dismissing = true;
            presenceSpring.Target = 0f;
        }

        public void SetPanelSize(Vector2 sizeMeters, Vector2Int resolutionPixels)
        {
            Vector2 oldSize = panelSizeMeters;
            panelSizeMeters = new Vector2(Mathf.Max(0.25f, sizeMeters.x), Mathf.Max(0.16f, sizeMeters.y));
            pixelResolution = new Vector2Int(
                Mathf.Clamp(resolutionPixels.x, 256, 3840),
                Mathf.Clamp(resolutionPixels.y, 256, 2160));
            ApplyGeometryLayout();
            backend?.Resize(pixelResolution.x, pixelResolution.y);

            if (fallbackActive)
            {
                EnsureFallbackTextureResources();
            }

            if (!WebViewSpatialMotion.ReducedMotion && panelSizeMeters.x > 0.01f)
            {
                sizeRatioSpring.Value = Mathf.Clamp(oldSize.x / panelSizeMeters.x, 0.35f, 2.5f);
                sizeRatioSpring.Target = 1f;
                sizeRatioSpring.Velocity = 0f;
            }
        }

        internal void NotifySpatialInteractionState(float nearness01, bool isHeld)
        {
            nearness01 = Mathf.Clamp01(nearness01);
            bool changed = Mathf.Abs(nearness01 - currentNearness) > 0.005f || isHeld != currentlyHeld;
            currentNearness = nearness01;
            currentlyHeld = isHeld;

            holdGiveSpring.Target = isHeld ? 1f : 0f;
            dockRevealSpring.Target = (isHeld || nearness01 > 0.08f) ? 1f : 0f;

            if (changed && perimeterBorderImage != null)
            {
                perimeterBorderImage.color = isHeld
                    ? BorderHeldViolet
                    : Color.Lerp(BorderRestColor, BorderHoverColor, nearness01);
            }
        }

        internal void SendPointer(int action, Vector2 uv) =>
            backend?.DispatchPointer(action, uv.x, uv.y);

        internal void SendHover(int hoverAction, Vector2 uv) =>
            backend?.DispatchHover(hoverAction, uv.x, uv.y);

        internal void SendScroll(Vector2 uv, Vector2 delta) =>
            backend?.DispatchScroll(uv.x, uv.y, delta.x, delta.y);

        private SurfaceBackendMode ResolveSurfaceBackendMode()
        {
            if (surfaceBackendMode != SurfaceBackendMode.Auto)
                return surfaceBackendMode;

#if SPATIAL_WEBVIEW_META_OVR
            if (OVRManager.instance != null || FindFirstObjectByType<OVRCameraRig>() != null)
            {
                return SurfaceBackendMode.MetaOVROverlay;
            }
#endif
#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
            return SurfaceBackendMode.OpenXRCompositionLayer;
#elif SPATIAL_WEBVIEW_META_OVR
            return SurfaceBackendMode.MetaOVROverlay;
#else
            return SurfaceBackendMode.UniversalTexture;
#endif
        }

        private void ConfigurePresentationPath()
        {
            surfaceAttached = false;
            fallbackActive = false;
            compositorSurfaceWaitFrames = 0;
            resolvedBackendMode = ResolveSurfaceBackendMode();

#if !UNITY_ANDROID || UNITY_EDITOR
            if (viewportRenderer != null) viewportRenderer.enabled = false;
            return;
#else
            if (resolvedBackendMode == SurfaceBackendMode.MetaOVROverlay)
            {
#if SPATIAL_WEBVIEW_META_OVR
                ConfigureMetaOvrOverlay();
                return;
#endif
            }

            if (resolvedBackendMode == SurfaceBackendMode.OpenXRCompositionLayer)
            {
#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
                ConfigureOpenXrCompositionLayer();
                return;
#endif
            }

            ActivateUniversalTextureFallback();
#endif
        }

#if SPATIAL_WEBVIEW_META_OVR
        private void ConfigureMetaOvrOverlay()
        {
            if (ovrOverlay == null && viewportRoot != null)
            {
                ovrOverlay = viewportRoot.GetComponent<OVROverlay>();
                if (ovrOverlay == null)
                {
                    ovrOverlay = viewportRoot.gameObject.AddComponent<OVROverlay>();
                }
            }

            if (ovrOverlay != null)
            {
                ovrOverlay.enabled = true;
                ovrOverlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
                ovrOverlay.currentOverlayType = compositorMode == CompositorLayerMode.UnderlayPunchThrough
                    ? OVROverlay.OverlayType.Underlay
                    : OVROverlay.OverlayType.Overlay;
                ovrOverlay.compositionDepth = compositorMode == CompositorLayerMode.UnderlayPunchThrough
                    ? underlayCompositionDepth
                    : 0;
                ovrOverlay.isExternalSurface = true;
                ovrOverlay.externalSurfaceWidth = pixelResolution.x;
                ovrOverlay.externalSurfaceHeight = pixelResolution.y;
                ovrOverlay.useBicubicFiltering = useBicubicFiltering;
                ovrOverlay.externalSurfaceObjectCreated -= OnMetaExternalSurfaceCreated;
                ovrOverlay.externalSurfaceObjectCreated += OnMetaExternalSurfaceCreated;

                if (ovrOverlay.externalSurfaceObject != IntPtr.Zero)
                {
                    AttachCompositorSurface(ovrOverlay.externalSurfaceObject);
                }
            }

            SetPunchThroughRendererActive(compositorMode == CompositorLayerMode.UnderlayPunchThrough);
        }

        private void OnMetaExternalSurfaceCreated()
        {
            if (ovrOverlay != null && ovrOverlay.externalSurfaceObject != IntPtr.Zero)
            {
                AttachCompositorSurface(ovrOverlay.externalSurfaceObject);
            }
        }
#endif

#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
        private void ConfigureOpenXrCompositionLayer()
        {
            if (viewportRoot == null) return;

            if (openXrCompositionLayer == null)
            {
                openXrCompositionLayer = viewportRoot.GetComponent<CompositionLayer>();
                if (openXrCompositionLayer == null)
                {
                    openXrCompositionLayer = viewportRoot.gameObject.AddComponent<CompositionLayer>();
                }
            }

            openXrCompositionLayer.ChangeLayerDataType<QuadLayerData>();
            if (openXrCompositionLayer.LayerData is QuadLayerData quadData)
            {
                quadData.ApplyTransformScale = true;
                quadData.Size = Vector2.one;
            }
            openXrCompositionLayer.Order = compositorMode == CompositorLayerMode.UnderlayPunchThrough ? -1 : 1;

            if (openXrTexturesExt == null)
            {
                openXrTexturesExt = viewportRoot.GetComponent<TexturesExtension>();
                if (openXrTexturesExt == null)
                {
                    openXrTexturesExt = viewportRoot.gameObject.AddComponent<TexturesExtension>();
                }
            }
            openXrTexturesExt.enabled = true;
            openXrTexturesExt.sourceTexture = TexturesExtension.SourceTextureEnum.AndroidSurface;
            openXrTexturesExt.Resolution = new Vector2(pixelResolution.x, pixelResolution.y);
            openXrCompositionLayer.enabled = true;

            SetPunchThroughRendererActive(compositorMode == CompositorLayerMode.UnderlayPunchThrough);
        }
#endif

        private void ActivateUniversalTextureFallback()
        {
            resolvedBackendMode = SurfaceBackendMode.UniversalTexture;
            fallbackActive = true;
            surfaceAttached = false;

#if SPATIAL_WEBVIEW_META_OVR
            if (ovrOverlay != null) ovrOverlay.enabled = false;
#endif
#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
            if (openXrCompositionLayer != null) openXrCompositionLayer.enabled = false;
#endif

            EnsureFallbackTextureResources();
            if (backend != null && backend.IsReady)
            {
                backend.EnableTextureFallback(pixelResolution.x, pixelResolution.y);
            }
        }

        private void EnsureFallbackTextureResources()
        {
            if (fallbackTexture == null ||
                fallbackTexture.width != pixelResolution.x ||
                fallbackTexture.height != pixelResolution.y)
            {
                if (fallbackTexture != null) Destroy(fallbackTexture);
                fallbackTexture = new Texture2D(
                    pixelResolution.x,
                    pixelResolution.y,
                    TextureFormat.RGBA32,
                    mipChain: true)
                {
                    name = "SpatialWebView_FallbackTex",
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                    wrapMode = TextureWrapMode.Clamp
                };
                lastUploadedFallbackSeq = 0;
            }

            if (fallbackTextureMaterial == null)
            {
                Shader texShader = Resources.Load<Shader>("SpatialWebView/ViewportTexture")
                                   ?? Shader.Find("Hidden/SpatialWebView/ViewportTexture")
                                   ?? Shader.Find("Unlit/Texture");
                if (texShader != null)
                {
                    fallbackTextureMaterial = new Material(texShader);
                }
            }

            if (fallbackTextureMaterial != null)
            {
                fallbackTextureMaterial.mainTexture = fallbackTexture;
            }

            if (viewportRenderer != null && fallbackTextureMaterial != null)
            {
                viewportRenderer.sharedMaterial = fallbackTextureMaterial;
                viewportRenderer.enabled = true;
            }
        }

        private void SetPunchThroughRendererActive(bool active)
        {
            if (viewportRenderer == null) return;
            if (active && punchMaterial != null)
            {
                viewportRenderer.sharedMaterial = punchMaterial;
                viewportRenderer.enabled = true;
            }
            else
            {
                viewportRenderer.enabled = false;
            }
        }

        private void PollPresentationOutput()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (backend == null || !backend.IsReady) return;

            if (fallbackActive)
            {
                if (fallbackTexture != null)
                {
                    backend.TryUploadFallbackFrame(fallbackTexture, ref lastUploadedFallbackSeq);
                }
                return;
            }

            if (surfaceAttached) return;
            compositorSurfaceWaitFrames++;

#if SPATIAL_WEBVIEW_META_OVR
            if (resolvedBackendMode == SurfaceBackendMode.MetaOVROverlay && ovrOverlay != null)
            {
                if (ovrOverlay.externalSurfaceObject != IntPtr.Zero)
                {
                    AttachCompositorSurface(ovrOverlay.externalSurfaceObject);
                    return;
                }
            }
#endif

#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
            if (resolvedBackendMode == SurfaceBackendMode.OpenXRCompositionLayer &&
                openXrCompositionLayer != null &&
                compositorSurfaceWaitFrames >= 3)
            {
                if (CompositionLayerManager.TryGetLayerId(openXrCompositionLayer, out int layerId))
                {
                    IntPtr surf = OpenXRLayerUtility.GetLayerAndroidSurfaceObject(layerId);
                    if (surf != IntPtr.Zero)
                    {
                        AttachCompositorSurface(surf);
                        return;
                    }
                }
            }
#endif

            // If Auto mode was chosen and the runtime fails to provide a hardware compositor Surface
            // (e.g. smart glasses or OpenXR runtime without XR_KHR_android_surface_swapchain),
            // switch automatically to Universal Direct-Buffer Texture2D mode instead of staying blank.
            if (surfaceBackendMode == SurfaceBackendMode.Auto && compositorSurfaceWaitFrames > 60)
            {
                Debug.Log("[SpatialWebView] Compositor external surface unavailable on this runtime; switching to Universal Texture mode.", this);
                ActivateUniversalTextureFallback();
            }
#endif
        }

        private void AttachCompositorSurface(IntPtr surfaceObj)
        {
            if (surfaceObj == IntPtr.Zero || backend == null || !backend.IsReady)
                return;

            surfaceAttached = true;
            fallbackActive = false;
            backend.AttachSurface(surfaceObj, pixelResolution.x, pixelResolution.y);
        }

        private void TeardownPresentationPath()
        {
            surfaceAttached = false;
            if (fallbackActive)
            {
                backend?.DisableTextureFallback();
                fallbackActive = false;
            }
            else
            {
                backend?.DetachSurface();
            }

#if SPATIAL_WEBVIEW_META_OVR
            if (ovrOverlay != null)
            {
                ovrOverlay.externalSurfaceObjectCreated -= OnMetaExternalSurfaceCreated;
                ovrOverlay.externalSurfaceObject = IntPtr.Zero;
            }
#endif
        }

        private void ShowIfHidden()
        {
            dismissing = false;
            presenceSpring.Target = 1f;
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
        }

        private void StepSpatialSprings()
        {
            bool allAtRest =
                presenceSpring.AtRest &&
                sizeRatioSpring.AtRest &&
                travelSpring.AtRest &&
                holdGiveSpring.AtRest &&
                dockRevealSpring.AtRest;

            if (allAtRest) return;

            float dt = Time.unscaledDeltaTime;
            presenceSpring.Step(
                dt,
                dismissing ? WebViewSpatialMotion.Quick : WebViewSpatialMotion.Soft,
                dismissing ? WebViewSpatialMotion.FirmDamping : WebViewSpatialMotion.SoftDamping);
            sizeRatioSpring.Step(dt, WebViewSpatialMotion.Soft, WebViewSpatialMotion.FirmDamping);
            travelSpring.Step(dt, WebViewSpatialMotion.Soft, WebViewSpatialMotion.FirmDamping);
            holdGiveSpring.Step(dt, WebViewSpatialMotion.Quick, WebViewSpatialMotion.SoftDamping);
            dockRevealSpring.Step(dt, WebViewSpatialMotion.Quick, WebViewSpatialMotion.SoftDamping);

            ApplySpringPose();

            if (dismissing && presenceSpring.Value < 0.03f)
            {
                dismissing = false;
                presenceSpring.Snap(1f);
                gameObject.SetActive(false);
                Closed?.Invoke();
            }
        }

        private void ApplySpringPose()
        {
            if (motionPivot != null)
            {
                float s = Mathf.Max(0.0001f,
                    presenceSpring.Value *
                    sizeRatioSpring.Value *
                    (1f - WebViewSpatialMotion.BoardGrabGive * holdGiveSpring.Value));
                motionPivot.localScale = Vector3.one * s;
                motionPivot.localPosition = transform.InverseTransformVector(travelWorldOffset * travelSpring.Value);
            }

            if (sidePillsGroup != null)
            {
                float alpha = Mathf.Clamp01(dockRevealSpring.Value);
                sidePillsGroup.alpha = alpha;
                bool interactive = alpha > 0.25f;
                sidePillsGroup.interactable = interactive;
                sidePillsGroup.blocksRaycasts = interactive;
            }
        }

        private void CreateBackend()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            backend = new AndroidSurfaceWebViewBackend();
#else
            backend = new EditorPreviewBackend();
#endif
            backend.Ready += OnBackendReady;
            backend.PageStateChanged += OnBackendPageStateChanged;
            backend.MessageReceived += OnBackendMessageReceived;
            backend.ErrorOccurred += OnBackendError;

            backend.Initialize(
                pixelResolution.x,
                pixelResolution.y,
                activeFps,
                idleFps,
                userAgentOverride);
        }

        private void OnBackendReady()
        {
            ApplyBackgroundThemeColor();
            backend.SetTextZoom(textZoomPercent);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (fallbackActive)
            {
                backend.EnableTextureFallback(pixelResolution.x, pixelResolution.y);
            }
#if SPATIAL_WEBVIEW_META_OVR
            else if (resolvedBackendMode == SurfaceBackendMode.MetaOVROverlay &&
                     ovrOverlay != null &&
                     ovrOverlay.externalSurfaceObject != IntPtr.Zero)
            {
                AttachCompositorSurface(ovrOverlay.externalSurfaceObject);
            }
#endif
#endif

            if (pendingRawHtml != null)
            {
                string html = pendingRawHtml;
                string baseUrl = pendingRawBaseUrl;
                pendingRawHtml = null;
                pendingRawBaseUrl = null;
                backend.LoadHtml(html, baseUrl);
            }
            else if (pendingReaderHtml != null)
            {
                string title = pendingReaderTitle;
                string body = pendingReaderHtml;
                pendingReaderTitle = null;
                pendingReaderHtml = null;
                OpenReaderHtml(title, body, lastRequestedWebUrl);
            }
            else if (!string.IsNullOrEmpty(pendingInitialActionUrl))
            {
                string url = pendingInitialActionUrl;
                pendingInitialActionUrl = null;
                backend.LoadUrl(url);
            }
        }

        private void OnBackendPageStateChanged(WebPageState state)
        {
            if (handleCaptionLabel != null)
            {
                handleCaptionLabel.text = FormatCompactDomainOrTitle(state.Url, state.Title, state.IsLoading);
            }

            if (backPill != null)
            {
                backPill.gameObject.SetActive(state.CanGoBack);
                backPill.Interactable = state.CanGoBack;
            }

            if (!state.IsLoading && readerModeEnabled && !isCurrentContentVideo && state.ErrorCode == 0)
            {
                _ = backend.EvaluateJavaScriptAsync(WebContentFormatter.BuildLiveReaderModeScript(readerTheme));
            }

            PageStateChanged?.Invoke(state);
        }

        private void OnBackendMessageReceived(string payload) => MessageReceived?.Invoke(payload);

        private void OnBackendError(string err)
        {
            Debug.LogWarning("[SpatialWebView] " + err, this);
            ErrorOccurred?.Invoke(err);
        }

        private void ApplyBackgroundThemeColor()
        {
            Color32 c = readerTheme switch
            {
                ReaderTheme.Light => new Color32(247, 248, 250, 255),
                ReaderTheme.Sepia => new Color32(243, 234, 216, 255),
                _ => new Color32(18, 21, 28, 255)
            };
            backend?.SetBackgroundColor(c);
        }

        private void BuildRigIfNeeded()
        {
            if (viewportRoot != null) return;

            var pivotGo = new GameObject("MotionPivot");
            pivotGo.transform.SetParent(transform, false);
            motionPivot = pivotGo.transform;

            GameObject vpGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            vpGo.name = "BrowserViewport";
            vpGo.transform.SetParent(motionPivot, false);
            Collider defaultMeshCollider = vpGo.GetComponent<Collider>();
            if (defaultMeshCollider != null) DestroyImmediate(defaultMeshCollider);

            viewportCollider = vpGo.AddComponent<BoxCollider>();
            viewportCollider.center = Vector3.zero;
            viewportCollider.size = new Vector3(1f, 1f, 0.015f);

            viewportRenderer = vpGo.GetComponent<MeshRenderer>();
            Shader punchShader = Resources.Load<Shader>("SpatialWebView/UnderlayPunchThrough")
                                 ?? Shader.Find("Hidden/SpatialWebView/UnderlayPunchThrough");
            if (punchShader != null)
            {
                punchMaterial = new Material(punchShader);
                viewportRenderer.sharedMaterial = punchMaterial;
            }
            viewportRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            viewportRenderer.receiveShadows = false;
            viewportRoot = vpGo.transform;

            var canvasGo = new GameObject(
                "BoardSpatialOverlay",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(motionPivot, false);
            uiCanvas = canvasGo.GetComponent<Canvas>();
            uiCanvas.renderMode = RenderMode.WorldSpace;

            var hitTargetGo = new GameObject("ViewportPointerTarget", typeof(RectTransform), typeof(Image));
            hitTargetGo.transform.SetParent(canvasGo.transform, false);
            Image hitImg = hitTargetGo.GetComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);
            hitImg.raycastTarget = true;

            var borderGo = new GameObject("PerimeterFrame", typeof(RectTransform), typeof(Image));
            borderGo.transform.SetParent(hitTargetGo.transform, false);
            RectTransform borderRt = borderGo.GetComponent<RectTransform>();
            borderRt.anchorMin = Vector2.zero;
            borderRt.anchorMax = Vector2.one;
            borderRt.offsetMin = new Vector2(-4f, -4f);
            borderRt.offsetMax = new Vector2(4f, 4f);

            perimeterBorderImage = borderGo.GetComponent<Image>();
            perimeterBorderImage.sprite = SpatialPillControl.GetThinBorderSprite();
            perimeterBorderImage.type = Image.Type.Sliced;
            perimeterBorderImage.color = BorderRestColor;
            perimeterBorderImage.raycastTarget = false;

            BuildBottomFloatingDock(canvasGo.transform);
            BuildEditorPreviewCard(hitTargetGo.transform);

            pointerInput = hitTargetGo.AddComponent<SpatialWebViewPointerInput>();
            pointerInput.EnableBuiltInRayInput = enableBuiltInXRPointer;
            pointerInput.EnableGlassesGazeFallback = enableGlassesGazeFallback;
            pointerInput.Bind(this, viewportRoot, viewportCollider, dockRect, grabHandleRect);

            ApplyGeometryLayout();
            RefreshDockState();
            ApplySpringPose();
        }

        private void BuildBottomFloatingDock(Transform canvasParent)
        {
            var dockGo = new GameObject("BottomPillDock", typeof(RectTransform));
            dockGo.transform.SetParent(canvasParent, false);
            dockRect = dockGo.GetComponent<RectTransform>();

            SpatialPillControl grabPill = CreateSpatialPill(
                dockGo.transform,
                "GrabHandlePill",
                new Vector2(280f, 52f),
                "web",
                fontSize: 18f,
                swellOnHover: false,
                onClick: null,
                out handleCaptionLabel);
            grabHandleRect = grabPill.GetComponent<RectTransform>();
            grabHandleRect.anchoredPosition = Vector2.zero;

            var sideGo = new GameObject("ContextPills", typeof(RectTransform), typeof(CanvasGroup));
            sideGo.transform.SetParent(dockGo.transform, false);
            RectTransform sideRt = sideGo.GetComponent<RectTransform>();
            sideRt.anchorMin = new Vector2(0.5f, 0.5f);
            sideRt.anchorMax = new Vector2(0.5f, 0.5f);
            sideRt.sizeDelta = new Vector2(860f, 64f);
            sideRt.anchoredPosition = Vector2.zero;
            sidePillsGroup = sideGo.GetComponent<CanvasGroup>();
            sidePillsGroup.alpha = 0f;

            backPill = CreateSpatialPill(
                sideGo.transform,
                "Pill_Back",
                new Vector2(64f, 56f),
                "←",
                fontSize: 24f,
                swellOnHover: true,
                onClick: GoBack,
                out _);
            backPill.GetComponent<RectTransform>().anchoredPosition = new Vector2(-192f, 0f);
            backPill.gameObject.SetActive(false);

            readerPill = CreateSpatialPill(
                sideGo.transform,
                "Pill_Reader",
                new Vector2(112f, 56f),
                "Reader",
                fontSize: 18f,
                swellOnHover: true,
                onClick: ToggleReaderMode,
                out readerPillLabel);
            readerPill.GetComponent<RectTransform>().anchoredPosition = new Vector2(216f, 0f);

            typographyPill = CreateSpatialPill(
                sideGo.transform,
                "Pill_Typography",
                new Vector2(76f, 56f),
                "Aa",
                fontSize: 19f,
                swellOnHover: true,
                onClick: CycleTypographyPreset,
                out typographyPillLabel);
            typographyPill.GetComponent<RectTransform>().anchoredPosition = new Vector2(322f, 0f);

            SpatialPillControl closePill = CreateSpatialPill(
                sideGo.transform,
                "Pill_Close",
                new Vector2(56f, 56f),
                "×",
                fontSize: 26f,
                swellOnHover: true,
                onClick: CloseWindow,
                out _);
            closePill.GetComponent<RectTransform>().anchoredPosition = new Vector2(400f, 0f);
        }

        private static SpatialPillControl CreateSpatialPill(
            Transform parent,
            string name,
            Vector2 sizePx,
            string text,
            float fontSize,
            bool swellOnHover,
            Action onClick,
            out TextMeshProUGUI labelOut)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(SpatialPillControl));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sizePx;

            Image bg = go.GetComponent<Image>();
            SpatialPillControl.ApplyPillRounding(bg, sizePx);
            bg.raycastTarget = true;

            SpatialPillControl pill = go.GetComponent<SpatialPillControl>();
            pill.Configure(bg, PillRestColor, PillHoverColor, PillActiveViolet, swellOnHover);
            if (onClick != null)
            {
                pill.Clicked += onClick;
            }

            var txtGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            RectTransform tr = txtGo.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(14f, 0f);
            tr.offsetMax = new Vector2(-14f, 0f);

            labelOut = txtGo.GetComponent<TextMeshProUGUI>();
            labelOut.text = text;
            labelOut.fontSize = fontSize;
            labelOut.color = new Color(0.94f, 0.96f, 1.0f, 0.96f);
            labelOut.alignment = TextAlignmentOptions.Center;
            labelOut.textWrappingMode = TextWrappingModes.NoWrap;
            labelOut.overflowMode = TextOverflowModes.Ellipsis;
            labelOut.raycastTarget = false;

            return pill;
        }

        private void BuildEditorPreviewCard(Transform viewportUiParent)
        {
            editorPreviewRoot = new GameObject("EditorPreviewOverlay", typeof(RectTransform), typeof(Image));
            editorPreviewRoot.transform.SetParent(viewportUiParent, false);
            RectTransform rt = editorPreviewRoot.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image bg = editorPreviewRoot.GetComponent<Image>();
            bg.color = new Color(0.07f, 0.09f, 0.13f, 0.98f);
            bg.raycastTarget = false;

            var thumbGo = new GameObject("Thumbnail", typeof(RectTransform), typeof(RawImage));
            thumbGo.transform.SetParent(editorPreviewRoot.transform, false);
            RectTransform thumbRt = thumbGo.GetComponent<RectTransform>();
            thumbRt.anchorMin = new Vector2(0.14f, 0.24f);
            thumbRt.anchorMax = new Vector2(0.86f, 0.88f);
            thumbRt.offsetMin = Vector2.zero;
            thumbRt.offsetMax = Vector2.zero;
            editorThumbnailImage = thumbGo.GetComponent<RawImage>();
            editorThumbnailImage.color = new Color(1f, 1f, 1f, 0.96f);
            editorThumbnailImage.raycastTarget = false;
            editorThumbnailImage.gameObject.SetActive(false);

            var statusGo = new GameObject("PreviewStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(editorPreviewRoot.transform, false);
            RectTransform statusRt = statusGo.GetComponent<RectTransform>();
            statusRt.anchorMin = new Vector2(0.08f, 0.10f);
            statusRt.anchorMax = new Vector2(0.92f, 0.23f);
            statusRt.offsetMin = Vector2.zero;
            statusRt.offsetMax = Vector2.zero;

            editorStatusText = statusGo.GetComponent<TextMeshProUGUI>();
            editorStatusText.fontSize = 22f;
            editorStatusText.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            editorStatusText.alignment = TextAlignmentOptions.Center;
            editorStatusText.raycastTarget = false;

            SpatialPillControl openBtn = CreateSpatialPill(
                editorPreviewRoot.transform,
                "Pill_OpenBrowser",
                new Vector2(240f, 48f),
                "Open in Browser ↗",
                fontSize: 17f,
                swellOnHover: true,
                onClick: () =>
                {
                    if (!string.IsNullOrWhiteSpace(lastRequestedWebUrl))
                        Application.OpenURL(lastRequestedWebUrl);
                },
                out _);
            RectTransform btnRt = openBtn.GetComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0.5f, 0.02f);
            btnRt.anchorMax = new Vector2(0.5f, 0.02f);
            btnRt.pivot = new Vector2(0.5f, 0f);
            btnRt.anchoredPosition = new Vector2(0f, 8f);

#if UNITY_ANDROID && !UNITY_EDITOR
            editorPreviewRoot.SetActive(false);
#else
            editorPreviewRoot.SetActive(true);
#endif
        }

        private void ApplyGeometryLayout()
        {
            if (viewportRoot == null || uiCanvas == null) return;

            viewportRoot.localPosition = Vector3.zero;
            viewportRoot.localRotation = Quaternion.identity;
            viewportRoot.localScale = new Vector3(panelSizeMeters.x, panelSizeMeters.y, 1f);

            const float pixelsPerMeter = 1000f;
            float canvasW = panelSizeMeters.x * pixelsPerMeter;
            float canvasH = panelSizeMeters.y * pixelsPerMeter;
            const float dockZonePx = 92f;

            RectTransform canvasRt = uiCanvas.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(canvasW, canvasH + dockZonePx);
            canvasRt.localScale = Vector3.one / pixelsPerMeter;
            canvasRt.localPosition = new Vector3(0f, -(dockZonePx * 0.5f / pixelsPerMeter), -0.002f);
            canvasRt.localRotation = Quaternion.identity;

            if (pointerInput != null)
            {
                RectTransform hitRt = pointerInput.GetComponent<RectTransform>();
                hitRt.anchorMin = new Vector2(0.5f, 1f);
                hitRt.anchorMax = new Vector2(0.5f, 1f);
                hitRt.pivot = new Vector2(0.5f, 1f);
                hitRt.sizeDelta = new Vector2(canvasW, canvasH);
                hitRt.anchoredPosition = Vector2.zero;
            }

            if (dockRect != null)
            {
                dockRect.gameObject.SetActive(showBottomDock);
                dockRect.anchorMin = new Vector2(0.5f, 0f);
                dockRect.anchorMax = new Vector2(0.5f, 0f);
                dockRect.pivot = new Vector2(0.5f, 0f);
                dockRect.sizeDelta = new Vector2(Mathf.Max(canvasW, 900f), dockZonePx);
                dockRect.anchoredPosition = Vector2.zero;
            }

#if SPATIAL_WEBVIEW_META_OVR
            if (ovrOverlay != null)
            {
                ovrOverlay.externalSurfaceWidth = pixelResolution.x;
                ovrOverlay.externalSurfaceHeight = pixelResolution.y;
            }
#endif
#if SPATIAL_WEBVIEW_OPENXR && SPATIAL_WEBVIEW_COMPOSITION_LAYERS
            if (openXrTexturesExt != null)
            {
                openXrTexturesExt.Resolution = new Vector2(pixelResolution.x, pixelResolution.y);
            }
#endif
        }

        private void RefreshDockState()
        {
            if (readerPill != null)
            {
                readerPill.gameObject.SetActive(!isCurrentContentVideo);
                readerPill.ToggledActive = readerModeEnabled;
                if (readerPillLabel != null)
                {
                    readerPillLabel.text = readerModeEnabled ? "Reader" : "Web";
                }
            }

            if (typographyPill != null)
            {
                typographyPill.gameObject.SetActive(!isCurrentContentVideo);
                if (typographyPillLabel != null)
                {
                    typographyPillLabel.text = $"{textZoomPercent}%";
                }
            }
        }

        private static string FormatCompactDomainOrTitle(string url, string title, bool loading = false)
        {
            if (!string.IsNullOrWhiteSpace(title) && title.Length <= 34)
                return loading ? $"{title} ···" : title;

            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                string host = uri.Host.StartsWith("www.") ? uri.Host.Substring(4) : uri.Host;
                return loading ? $"{host} ···" : host;
            }

            if (!string.IsNullOrWhiteSpace(title))
                return title.Substring(0, Mathf.Min(32, title.Length)) + "…";

            return loading ? "Loading ···" : "Board";
        }

        private void UpdateEditorPreview(string headline, string url, string thumbnailUrl)
        {
#if !UNITY_ANDROID || UNITY_EDITOR
            if (handleCaptionLabel != null)
            {
                handleCaptionLabel.text = headline;
            }

            if (editorStatusText != null)
            {
                editorStatusText.text =
                    $"<b>{headline}</b>\n<size=16><color=#8F9BB0>{url}</color></size>";
            }

            if (previewCoroutine != null)
            {
                StopCoroutine(previewCoroutine);
                previewCoroutine = null;
            }

            if (!string.IsNullOrEmpty(thumbnailUrl) && isActiveAndEnabled)
            {
                previewCoroutine = StartCoroutine(LoadThumbnailRoutine(thumbnailUrl));
            }
            else if (editorThumbnailImage != null)
            {
                editorThumbnailImage.gameObject.SetActive(false);
            }
#endif
        }

        private IEnumerator LoadThumbnailRoutine(string url)
        {
            using UnityWebRequest req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success || editorThumbnailImage == null)
                yield break;

            if (downloadedPreviewTex != null) Destroy(downloadedPreviewTex);
            downloadedPreviewTex = DownloadHandlerTexture.GetContent(req);
            editorThumbnailImage.texture = downloadedPreviewTex;
            editorThumbnailImage.gameObject.SetActive(true);
        }
    }
}
