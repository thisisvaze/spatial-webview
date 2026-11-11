package com.thisisvaze.spatialwebview;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.Application;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.PorterDuff;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.Choreographer;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.InputMethodManager;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.PermissionRequest;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import java.nio.ByteBuffer;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Offscreen Android WebView bridge for Unity XR headsets and smart glasses:
 * 1) Compositor Surface path (Meta Quest 2/Pro/3/3S OVROverlay & OpenXR CompositionLayers on Pico/Android XR/Vive):
 *    draws directly onto the runtime Surface via Surface.lockHardwareCanvas() with zero CPU pixel copies.
 * 2) Universal Direct-Buffer Texture fallback (AR/VR smart glasses like XREAL/Viture/RayNeo or runtimes
 *    without XR_KHR_android_surface_swapchain): draws into a double-buffered direct ByteBuffer mapped
 *    zero-alloc into a Unity Texture2D with no C++ native library required.
 */
public final class QuestWebViewBridge {
    private static final String TAG = "SpatialWebView";
    private static final long INTERACTION_BOOST_MS = 2500L;

    public interface Callback {
        void onReady();
        void onPageState(
                String url,
                String title,
                boolean canGoBack,
                boolean canGoForward,
                boolean isLoading,
                int progress,
                int errorCode);
        void onMessage(String payload);
        void onJsResult(int requestId, String jsonResult);
        void onError(String message);
    }

    private static final Handler UI = new Handler(Looper.getMainLooper());
    private static final Map<Integer, Session> SESSIONS = new ConcurrentHashMap<>();

    private QuestWebViewBridge() {}

    public static void openSession(
            final Activity activity,
            final int sessionId,
            final int width,
            final int height,
            final int activeFps,
            final int idleFps,
            final String userAgentOverride,
            final Callback callback) {
        UI.post(() -> {
            if (activity == null || activity.isFinishing() || activity.isDestroyed()) {
                if (callback != null) callback.onError("Activity unavailable");
                return;
            }
            Session existing = SESSIONS.remove(sessionId);
            if (existing != null) {
                existing.close();
            }
            Session session = new Session(
                    activity,
                    sessionId,
                    Math.max(64, width),
                    Math.max(64, height),
                    clampFps(activeFps, 60),
                    clampFps(idleFps, 15),
                    userAgentOverride,
                    callback);
            try {
                session.start();
                SESSIONS.put(sessionId, session);
                if (callback != null) callback.onReady();
            } catch (Throwable t) {
                Log.e(TAG, "Failed to open WebView session " + sessionId, t);
                try {
                    session.close();
                } finally {
                    if (callback != null) callback.onError("open_failed: " + t.getMessage());
                }
            }
        });
    }

    /**
     * Binds an external Android Surface (Meta OVROverlay or OpenXR XR_KHR_android_surface_swapchain).
     * Note: The XR runtime owns the underlying native Surface; we never call surface.release().
     */
    public static void attachSurface(
            final int sessionId,
            final Surface surface,
            final int width,
            final int height) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s == null) return;
            s.attachSurface(surface, Math.max(64, width), Math.max(64, height));
        });
    }

    public static void detachSurface(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.detachSurface();
        });
    }

    /**
     * Activates universal double-buffered direct ByteBuffer rendering for AR/VR glasses
     * and runtimes without external compositor surfaces.
     */
    public static void enableTextureFallback(
            final int sessionId,
            final int width,
            final int height) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.enableTextureFallback(Math.max(64, width), Math.max(64, height));
        });
    }

    public static void disableTextureFallback(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.disableTextureFallback();
        });
    }

    public static int getFallbackFrameSequence(final int sessionId) {
        Session s = SESSIONS.get(sessionId);
        return s != null ? s.fallbackFrameSeq : 0;
    }

    public static ByteBuffer getReadyFallbackBuffer(final int sessionId) {
        Session s = SESSIONS.get(sessionId);
        if (s == null) return null;
        int idx = s.readyBufferIndex;
        ByteBuffer[] bufs = s.fallbackBuffers;
        if (bufs == null || idx < 0 || idx >= bufs.length) return null;
        return bufs[idx];
    }

    public static void resize(final int sessionId, final int width, final int height) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.resize(Math.max(64, width), Math.max(64, height));
        });
    }

    public static void loadUrl(final int sessionId, final String url) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.loadUrl(url);
        });
    }

    public static void loadHtml(final int sessionId, final String html, final String baseUrl) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.loadHtml(html, baseUrl);
        });
    }

    public static void evaluateJs(final int sessionId, final int requestId, final String script) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.evaluateJs(requestId, script);
        });
    }

    public static void goBack(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null && s.webView != null && s.webView.canGoBack()) {
                s.blurInput();
                s.webView.goBack();
                s.boostInteraction();
            }
        });
    }

    public static void goForward(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null && s.webView != null && s.webView.canGoForward()) {
                s.blurInput();
                s.webView.goForward();
                s.boostInteraction();
            }
        });
    }

    public static void reload(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null && s.webView != null) {
                s.blurInput();
                s.webView.reload();
                s.boostInteraction();
            }
        });
    }

    public static void stopLoading(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null && s.webView != null) {
                s.webView.stopLoading();
                s.loading = false;
                s.publishState();
            }
        });
    }

    public static void setTextZoom(final int sessionId, final int percent) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null && s.webView != null) {
                int clamped = Math.max(50, Math.min(250, percent));
                s.webView.getSettings().setTextZoom(clamped);
                s.boostInteraction();
            }
        });
    }

    public static void setBackgroundColor(final int sessionId, final int argbColor) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) {
                s.backgroundColor = argbColor;
                if (s.webView != null) {
                    s.webView.setBackgroundColor(argbColor);
                }
                s.boostInteraction();
            }
        });
    }

    public static void setFrameRates(final int sessionId, final int activeFps, final int idleFps) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) {
                s.activeFps = clampFps(activeFps, 60);
                s.idleFps = clampFps(idleFps, 15);
                s.markDirty();
            }
        });
    }

    public static void dispatchPointer(
            final int sessionId,
            final int action,
            final float normalizedX,
            final float normalizedY) {
        if (!Float.isFinite(normalizedX) || !Float.isFinite(normalizedY)) return;
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.dispatchPointer(action, normalizedX, normalizedY);
        });
    }

    public static void dispatchHover(
            final int sessionId,
            final int action,
            final float normalizedX,
            final float normalizedY) {
        if (!Float.isFinite(normalizedX) || !Float.isFinite(normalizedY)) return;
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.dispatchHover(action, normalizedX, normalizedY);
        });
    }

    public static void dispatchScroll(
            final int sessionId,
            final float normalizedX,
            final float normalizedY,
            final float deltaX,
            final float deltaY) {
        if (!Float.isFinite(normalizedX)
                || !Float.isFinite(normalizedY)
                || !Float.isFinite(deltaX)
                || !Float.isFinite(deltaY)) {
            return;
        }
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.dispatchScroll(normalizedX, normalizedY, deltaX, deltaY);
        });
    }

    public static void blurInput(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.blurInput();
        });
    }

    public static void setPaused(final int sessionId, final boolean paused) {
        UI.post(() -> {
            Session s = SESSIONS.get(sessionId);
            if (s != null) s.setPaused(paused);
        });
    }

    public static void closeSession(final int sessionId) {
        UI.post(() -> {
            Session s = SESSIONS.remove(sessionId);
            if (s != null) s.close();
        });
    }

    private static int clampFps(int fps, int fallback) {
        if (fps <= 0) return fallback;
        return Math.max(1, Math.min(120, fps));
    }

    private static final class OffscreenHostContainer extends FrameLayout {
        OffscreenHostContainer(Context context) {
            super(context);
            setClickable(false);
            setFocusable(false);
            setWillNotDraw(true);
        }

        @Override
        public boolean dispatchTouchEvent(MotionEvent ev) {
            return false;
        }

        @Override
        public boolean dispatchGenericMotionEvent(MotionEvent event) {
            return false;
        }

        @SuppressLint("MissingSuperCall")
        @Override
        protected void dispatchDraw(Canvas canvas) {
        }

        void renderToSurface(Surface targetSurface, int backgroundColor, OffscreenWebView webView, View customView) {
            if (targetSurface == null || !targetSurface.isValid()) return;
            Canvas hwCanvas = targetSurface.lockHardwareCanvas();
            try {
                hwCanvas.drawColor(backgroundColor, PorterDuff.Mode.SRC);
                if (customView != null && customView.isAttachedToWindow()) {
                    customView.draw(hwCanvas);
                } else if (webView != null && webView.isAttachedToWindow()) {
                    webView.drawToTargetCanvas(hwCanvas);
                }
            } finally {
                targetSurface.unlockCanvasAndPost(hwCanvas);
            }
        }

        void renderToBitmapCanvas(Canvas bmpCanvas, int height, int backgroundColor, OffscreenWebView webView, View customView) {
            bmpCanvas.drawColor(backgroundColor, PorterDuff.Mode.SRC);
            int save = bmpCanvas.save();
            try {
                // Pre-flip vertically so Unity Texture2D UVs (bottom-left origin) display upright with zero row-copy CPU work.
                bmpCanvas.scale(1f, -1f, 0f, height * 0.5f);
                if (customView != null && customView.isAttachedToWindow()) {
                    customView.draw(bmpCanvas);
                } else if (webView != null && webView.isAttachedToWindow()) {
                    webView.drawToTargetCanvas(bmpCanvas);
                }
            } finally {
                bmpCanvas.restoreToCount(save);
            }
        }
    }

    private static final class OffscreenWebView extends WebView {
        Runnable onInvalidatedCallback;

        OffscreenWebView(Context context) {
            super(context);
        }

        @SuppressLint("MissingSuperCall")
        @Override
        public void draw(Canvas canvas) {
        }

        void drawToTargetCanvas(Canvas targetCanvas) {
            computeScroll();
            int saveCount = targetCanvas.save();
            try {
                targetCanvas.translate(-getScrollX(), -getScrollY());
                super.draw(targetCanvas);
            } finally {
                targetCanvas.restoreToCount(saveCount);
            }
        }

        @Override
        public void invalidate() {
            super.invalidate();
            notifyInvalidated();
        }

        @Override
        public void postInvalidate() {
            super.postInvalidate();
            notifyInvalidated();
        }

        @Override
        public void postInvalidateOnAnimation() {
            super.postInvalidateOnAnimation();
            notifyInvalidated();
        }

        @Override
        protected void onScrollChanged(int l, int t, int oldl, int oldt) {
            super.onScrollChanged(l, t, oldl, oldt);
            notifyInvalidated();
        }

        private void notifyInvalidated() {
            if (onInvalidatedCallback != null) {
                onInvalidatedCallback.run();
            }
        }
    }

    private static final class Session implements Application.ActivityLifecycleCallbacks, Choreographer.FrameCallback {
        private static final String MEDIA_BRIDGE_JS =
                "(function(){if(window.__xrBrowserInit)return;window.__xrBrowserInit=true;"
                + "if(!window.xrBrowserPost){window.xrBrowserPost=function(m){"
                + "if(window.xrBrowser&&window.xrBrowser.postMessage){"
                + "window.xrBrowser.postMessage(typeof m==='string'?m:JSON.stringify(m));}};}"
                + "function syncMedia(){try{var v=document.querySelectorAll('video,audio'),p=false;"
                + "for(var i=0;i<v.length;i++){if(!v[i].paused&&!v[i].ended){p=true;break;}}"
                + "if(document.querySelector('iframe[src*=\"youtube.com\"],iframe[src*=\"youtube-nocookie.com\"],iframe[src*=\"vimeo.com\"]'))p=true;"
                + "if(window.xrBrowser&&window.xrBrowser.notifyMediaActive)window.xrBrowser.notifyMediaActive(p);"
                + "}catch(e){}}"
                + "document.addEventListener('play',syncMedia,true);"
                + "document.addEventListener('pause',syncMedia,true);"
                + "document.addEventListener('ended',syncMedia,true);"
                + "setTimeout(syncMedia,600);})();";

        final Activity activity;
        final int sessionId;
        final Callback callback;
        final String userAgentOverride;

        int width;
        int height;
        int activeFps;
        int idleFps;
        int backgroundColor = Color.rgb(18, 20, 26);

        OffscreenHostContainer container;
        OffscreenWebView webView;
        View customFullscreenView;
        WebChromeClient.CustomViewCallback customViewCallback;
        View previousActivityFocus;
        Surface surface;

        // Universal direct-ByteBuffer fallback (for AR/VR glasses without compositor surfaces)
        boolean fallbackEnabled;
        Bitmap fallbackBitmap;
        Canvas fallbackCanvas;
        volatile ByteBuffer[] fallbackBuffers;
        volatile int readyBufferIndex = -1;
        volatile int fallbackFrameSeq;

        boolean closed;
        boolean paused;
        boolean lifecycleRegistered;
        boolean frameCallbackScheduled;
        boolean dirty = true;
        boolean loading;
        boolean mediaActive;
        boolean touching;
        boolean hovering;

        int progress;
        int lastErrorCode;
        long downTimeMs;
        long boostUntilMs;
        long lastDrawTimeNs;
        float lastHoverX;
        float lastHoverY;

        Session(
                Activity activity,
                int sessionId,
                int width,
                int height,
                int activeFps,
                int idleFps,
                String userAgentOverride,
                Callback callback) {
            this.activity = activity;
            this.sessionId = sessionId;
            this.width = width;
            this.height = height;
            this.activeFps = activeFps;
            this.idleFps = idleFps;
            this.userAgentOverride = userAgentOverride;
            this.callback = callback;
        }

        final class JsBridge {
            @JavascriptInterface
            public void postMessage(final String payload) {
                UI.post(() -> {
                    if (!closed && callback != null) {
                        callback.onMessage(payload != null ? payload : "");
                    }
                });
            }

            @JavascriptInterface
            public void notifyMediaActive(final boolean active) {
                UI.post(() -> {
                    if (closed) return;
                    mediaActive = active;
                    if (active) markDirty();
                });
            }
        }

        @SuppressLint("SetJavaScriptEnabled")
        void start() {
            previousActivityFocus = activity.getCurrentFocus();

            container = new OffscreenHostContainer(activity);
            webView = new OffscreenWebView(activity);
            webView.setBackgroundColor(backgroundColor);
            webView.setFocusable(true);
            webView.setFocusableInTouchMode(true);
            webView.onInvalidatedCallback = this::markDirty;

            WebSettings settings = webView.getSettings();
            settings.setJavaScriptEnabled(true);
            settings.setDomStorageEnabled(true);
            settings.setMediaPlaybackRequiresUserGesture(false);
            settings.setUseWideViewPort(true);
            settings.setLoadWithOverviewMode(true);
            settings.setBuiltInZoomControls(false);
            settings.setDisplayZoomControls(false);
            settings.setSupportMultipleWindows(false);
            settings.setAllowFileAccess(false);
            settings.setAllowContentAccess(false);
            settings.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);

            if (userAgentOverride != null && !userAgentOverride.trim().isEmpty()) {
                settings.setUserAgentString(userAgentOverride.trim());
            } else {
                String defaultUa = settings.getUserAgentString();
                if (defaultUa != null) {
                    String cleanedUa = defaultUa
                            .replace("; wv)", ")")
                            .replace("Version/4.0 ", "");
                    settings.setUserAgentString(cleanedUa);
                }
            }

            CookieManager cookieManager = CookieManager.getInstance();
            cookieManager.setAcceptCookie(true);
            cookieManager.setAcceptThirdPartyCookies(webView, true);

            webView.addJavascriptInterface(new JsBridge(), "xrBrowser");

            webView.setWebViewClient(new WebViewClient() {
                @Override
                public void onPageStarted(WebView view, String url, Bitmap favicon) {
                    if (closed) return;
                    loading = true;
                    mediaActive = false;
                    lastErrorCode = 0;
                    progress = 5;
                    boostInteraction();
                    publishState();
                }

                @Override
                public void onPageFinished(WebView view, String url) {
                    if (closed) return;
                    loading = false;
                    progress = 100;
                    view.evaluateJavascript(MEDIA_BRIDGE_JS, null);
                    boostInteraction();
                    publishState();
                }

                @Override
                public void doUpdateVisitedHistory(WebView view, String url, boolean isReload) {
                    if (!closed) publishState();
                }

                @Override
                public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                    if (closed || request == null || !request.isForMainFrame()) return;
                    loading = false;
                    lastErrorCode = error != null ? error.getErrorCode() : -1;
                    publishState();
                }

                @Override
                public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                    if (request == null || request.getUrl() == null) return true;
                    Uri uri = request.getUrl();
                    String scheme = uri.getScheme();
                    return !("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme));
                }
            });

            webView.setWebChromeClient(new WebChromeClient() {
                @Override
                public void onReceivedTitle(WebView view, String title) {
                    if (!closed) publishState();
                }

                @Override
                public void onProgressChanged(WebView view, int newProgress) {
                    if (closed) return;
                    progress = newProgress;
                    markDirty();
                    publishState();
                }

                @Override
                public void onPermissionRequest(PermissionRequest request) {
                    if (request == null) return;
                    String[] resources = request.getResources();
                    boolean allowProtectedMedia = false;
                    if (resources != null) {
                        for (String r : resources) {
                            if (PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID.equals(r)) {
                                allowProtectedMedia = true;
                            }
                        }
                    }
                    if (allowProtectedMedia) {
                        request.grant(new String[]{PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID});
                    } else {
                        request.deny();
                    }
                }

                @Override
                public void onShowCustomView(View view, CustomViewCallback cb) {
                    if (closed || container == null) {
                        if (cb != null) cb.onCustomViewHidden();
                        return;
                    }
                    if (customFullscreenView != null && customViewCallback != null) {
                        customViewCallback.onCustomViewHidden();
                    }
                    customFullscreenView = view;
                    customViewCallback = cb;
                    container.addView(view, new FrameLayout.LayoutParams(width, height));
                    mediaActive = true;
                    boostInteraction();
                }

                @Override
                public void onHideCustomView() {
                    if (customFullscreenView != null && container != null) {
                        container.removeView(customFullscreenView);
                    }
                    customFullscreenView = null;
                    if (customViewCallback != null) {
                        customViewCallback.onCustomViewHidden();
                        customViewCallback = null;
                    }
                    boostInteraction();
                }
            });

            container.addView(webView, new FrameLayout.LayoutParams(width, height));
            activity.addContentView(container, new ViewGroup.LayoutParams(width, height));

            activity.getApplication().registerActivityLifecycleCallbacks(this);
            lifecycleRegistered = true;
        }

        private boolean hasOutputTarget() {
            return surface != null || fallbackEnabled;
        }

        void attachSurface(Surface newSurface, int newWidth, int newHeight) {
            if (closed) return;
            disableTextureFallback();
            this.surface = newSurface;
            if (newWidth != this.width || newHeight != this.height) {
                resize(newWidth, newHeight);
            }
            dirty = true;
            boostInteraction();
            scheduleFramePump();
        }

        void detachSurface() {
            cancelTouch();
            exitHover();
            this.surface = null;
            if (!fallbackEnabled) {
                unscheduleFramePump();
            }
        }

        void enableTextureFallback(int newWidth, int newHeight) {
            if (closed) return;
            this.surface = null;
            if (newWidth != this.width || newHeight != this.height) {
                resize(newWidth, newHeight);
            }
            ensureFallbackBuffers(this.width, this.height);
            fallbackEnabled = true;
            dirty = true;
            boostInteraction();
            scheduleFramePump();
        }

        void disableTextureFallback() {
            fallbackEnabled = false;
            readyBufferIndex = -1;
            fallbackBuffers = null;
            fallbackCanvas = null;
            if (fallbackBitmap != null) {
                fallbackBitmap.recycle();
                fallbackBitmap = null;
            }
            if (surface == null) {
                unscheduleFramePump();
            }
        }

        private void ensureFallbackBuffers(int w, int h) {
            int expectedBytes = w * h * 4;
            if (fallbackBitmap != null
                    && fallbackBitmap.getWidth() == w
                    && fallbackBitmap.getHeight() == h
                    && fallbackBuffers != null
                    && fallbackBuffers[0].capacity() == expectedBytes) {
                return;
            }
            if (fallbackBitmap != null) {
                fallbackBitmap.recycle();
            }
            fallbackBitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888);
            fallbackCanvas = new Canvas(fallbackBitmap);
            ByteBuffer b0 = ByteBuffer.allocateDirect(expectedBytes);
            ByteBuffer b1 = ByteBuffer.allocateDirect(expectedBytes);
            readyBufferIndex = -1;
            fallbackBuffers = new ByteBuffer[]{b0, b1};
        }

        void resize(int newWidth, int newHeight) {
            if (closed || webView == null || container == null) return;
            this.width = newWidth;
            this.height = newHeight;
            webView.setLayoutParams(new FrameLayout.LayoutParams(newWidth, newHeight));
            if (customFullscreenView != null) {
                customFullscreenView.setLayoutParams(new FrameLayout.LayoutParams(newWidth, newHeight));
            }
            ViewGroup.LayoutParams hostLp = container.getLayoutParams();
            if (hostLp != null) {
                hostLp.width = newWidth;
                hostLp.height = newHeight;
                container.setLayoutParams(hostLp);
            }
            if (fallbackEnabled) {
                ensureFallbackBuffers(newWidth, newHeight);
            }
            boostInteraction();
        }

        void loadUrl(String url) {
            if (closed || webView == null || url == null || url.trim().isEmpty()) return;
            blurInput();
            mediaActive = false;
            webView.loadUrl(url.trim());
            boostInteraction();
        }

        void loadHtml(String html, String baseUrl) {
            if (closed || webView == null) return;
            blurInput();
            String safeBase = (baseUrl != null && !baseUrl.trim().isEmpty())
                    ? baseUrl.trim()
                    : "https://player.spatialwebview.local/";
            String safeHtml = html != null ? html : "";
            webView.loadDataWithBaseURL(safeBase, safeHtml, "text/html", "UTF-8", null);
            boostInteraction();
        }

        void evaluateJs(final int requestId, String script) {
            if (closed || webView == null) return;
            if (script == null) {
                if (callback != null) callback.onJsResult(requestId, "null");
                return;
            }
            try {
                webView.evaluateJavascript(script, value -> {
                    if (!closed && callback != null) {
                        callback.onJsResult(requestId, value != null ? value : "null");
                    }
                });
                markDirty();
            } catch (Throwable t) {
                if (callback != null) callback.onJsResult(requestId, "null");
            }
        }

        void dispatchPointer(int action, float normX, float normY) {
            if (closed || paused || !hasOutputTarget()) return;
            View target = customFullscreenView != null ? customFullscreenView : webView;
            if (target == null) return;

            float px = clamp01(normX) * width;
            float py = clamp01(normY) * height;
            long now = SystemClock.uptimeMillis();

            if (action == MotionEvent.ACTION_DOWN) {
                cancelTouch();
                exitHover();
                downTimeMs = now;
                touching = true;
                target.requestFocus();
            } else if (!touching) {
                return;
            }

            MotionEvent ev = MotionEvent.obtain(downTimeMs, now, action, px, py, 0);
            try {
                target.dispatchTouchEvent(ev);
            } finally {
                ev.recycle();
            }

            if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                touching = false;
            }
            boostInteraction();
        }

        void dispatchHover(int action, float normX, float normY) {
            if (closed || paused || touching || !hasOutputTarget()) return;
            View target = customFullscreenView != null ? customFullscreenView : webView;
            if (target == null) return;

            if (action == MotionEvent.ACTION_HOVER_ENTER) {
                hovering = true;
            } else if (!hovering) {
                return;
            }

            lastHoverX = clamp01(normX);
            lastHoverY = clamp01(normY);
            if (action == MotionEvent.ACTION_HOVER_EXIT) {
                hovering = false;
            }

            sendHoverEvent(target, action, lastHoverX * width, lastHoverY * height);
            markDirty();
        }

        void dispatchScroll(float normX, float normY, float deltaX, float deltaY) {
            if (closed || paused || !hasOutputTarget()) return;
            View target = customFullscreenView != null ? customFullscreenView : webView;
            if (target == null) return;

            MotionEvent.PointerProperties props = new MotionEvent.PointerProperties();
            props.id = 0;
            props.toolType = MotionEvent.TOOL_TYPE_MOUSE;

            MotionEvent.PointerCoords coords = new MotionEvent.PointerCoords();
            coords.x = clamp01(normX) * width;
            coords.y = clamp01(normY) * height;
            coords.setAxisValue(MotionEvent.AXIS_HSCROLL, deltaX);
            coords.setAxisValue(MotionEvent.AXIS_VSCROLL, deltaY);

            long now = SystemClock.uptimeMillis();
            MotionEvent ev = MotionEvent.obtain(
                    now,
                    now,
                    MotionEvent.ACTION_SCROLL,
                    1,
                    new MotionEvent.PointerProperties[]{props},
                    new MotionEvent.PointerCoords[]{coords},
                    0,
                    0,
                    1f,
                    1f,
                    0,
                    0,
                    InputDevice.SOURCE_MOUSE,
                    0);
            try {
                target.dispatchGenericMotionEvent(ev);
            } finally {
                ev.recycle();
            }
            boostInteraction();
        }

        void blurInput() {
            if (webView == null) return;
            InputMethodManager imm =
                    (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE);
            if (imm != null && imm.isActive(webView)) {
                imm.hideSoftInputFromWindow(webView.getWindowToken(), 0);
            }
            webView.clearFocus();
            if (previousActivityFocus != null && previousActivityFocus.isAttachedToWindow()) {
                previousActivityFocus.requestFocus();
            }
        }

        void setPaused(boolean shouldPause) {
            if (closed || paused == shouldPause) return;
            paused = shouldPause;
            if (webView != null) {
                if (shouldPause) {
                    blurInput();
                    cancelTouch();
                    exitHover();
                    webView.onPause();
                    unscheduleFramePump();
                } else {
                    webView.onResume();
                    dirty = true;
                    boostInteraction();
                    scheduleFramePump();
                }
            }
        }

        void boostInteraction() {
            dirty = true;
            boostUntilMs = SystemClock.uptimeMillis() + INTERACTION_BOOST_MS;
            scheduleFramePump();
        }

        void markDirty() {
            dirty = true;
            scheduleFramePump();
        }

        private void scheduleFramePump() {
            if (closed || paused || !hasOutputTarget() || frameCallbackScheduled) return;
            frameCallbackScheduled = true;
            Choreographer.getInstance().postFrameCallback(this);
        }

        private void unscheduleFramePump() {
            if (!frameCallbackScheduled) return;
            frameCallbackScheduled = false;
            Choreographer.getInstance().removeFrameCallback(this);
        }

        @Override
        public void doFrame(long frameTimeNanos) {
            frameCallbackScheduled = false;
            if (closed || paused || !hasOutputTarget()) return;

            if (surface != null && !surface.isValid()) {
                surface = null;
                if (!fallbackEnabled) return;
            }

            boolean highRate = loading || mediaActive || (SystemClock.uptimeMillis() < boostUntilMs);
            // Cap CPU/Bitmap fallback path at 30fps active / 8fps idle so smart-glasses stay cool.
            int effectiveActiveFps = (surface != null) ? activeFps : Math.min(activeFps, 30);
            int effectiveIdleFps = (surface != null) ? idleFps : Math.min(idleFps, 8);
            int targetFps = highRate ? effectiveActiveFps : effectiveIdleFps;
            long minIntervalNs = 1_000_000_000L / Math.max(1, targetFps);

            if ((dirty || highRate) && ((frameTimeNanos - lastDrawTimeNs) >= (minIntervalNs - 2_000_000L) || dirty)) {
                try {
                    if (container != null && webView != null && webView.getWidth() > 0 && webView.getHeight() > 0) {
                        if (surface != null) {
                            container.renderToSurface(surface, backgroundColor, webView, customFullscreenView);
                        } else if (fallbackEnabled && fallbackCanvas != null && fallbackBitmap != null && fallbackBuffers != null) {
                            container.renderToBitmapCanvas(fallbackCanvas, height, backgroundColor, webView, customFullscreenView);
                            int writeIdx = (readyBufferIndex == 0) ? 1 : 0;
                            ByteBuffer dst = fallbackBuffers[writeIdx];
                            dst.rewind();
                            fallbackBitmap.copyPixelsToBuffer(dst);
                            dst.rewind();
                            readyBufferIndex = writeIdx;
                            fallbackFrameSeq++;
                        }
                        lastDrawTimeNs = frameTimeNanos;
                        dirty = false;
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "Frame draw skipped for session " + sessionId + ": " + t.getMessage());
                    if (surface != null) surface = null;
                }
            }

            scheduleFramePump();
        }

        void publishState() {
            if (closed || webView == null || callback == null) return;
            String url = webView.getUrl();
            String title = webView.getTitle();
            callback.onPageState(
                    url != null ? url : "",
                    title != null ? title : "",
                    webView.canGoBack(),
                    webView.canGoForward(),
                    loading,
                    progress,
                    lastErrorCode);
        }

        private void sendHoverEvent(View target, int action, float px, float py) {
            MotionEvent.PointerProperties props = new MotionEvent.PointerProperties();
            props.id = 0;
            props.toolType = MotionEvent.TOOL_TYPE_MOUSE;

            MotionEvent.PointerCoords coords = new MotionEvent.PointerCoords();
            coords.x = px;
            coords.y = py;

            long now = SystemClock.uptimeMillis();
            MotionEvent ev = MotionEvent.obtain(
                    now,
                    now,
                    action,
                    1,
                    new MotionEvent.PointerProperties[]{props},
                    new MotionEvent.PointerCoords[]{coords},
                    0,
                    0,
                    1f,
                    1f,
                    0,
                    0,
                    InputDevice.SOURCE_MOUSE,
                    0);
            try {
                target.dispatchGenericMotionEvent(ev);
            } finally {
                ev.recycle();
            }
        }

        private void cancelTouch() {
            if (!touching || webView == null) return;
            long now = SystemClock.uptimeMillis();
            MotionEvent ev = MotionEvent.obtain(downTimeMs, now, MotionEvent.ACTION_CANCEL, 0f, 0f, 0);
            try {
                View target = customFullscreenView != null ? customFullscreenView : webView;
                target.dispatchTouchEvent(ev);
            } finally {
                ev.recycle();
            }
            touching = false;
        }

        private void exitHover() {
            if (!hovering || webView == null) return;
            hovering = false;
            View target = customFullscreenView != null ? customFullscreenView : webView;
            sendHoverEvent(target, MotionEvent.ACTION_HOVER_EXIT, lastHoverX * width, lastHoverY * height);
        }

        private static float clamp01(float v) {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        void close() {
            if (closed) return;
            closed = true;
            unscheduleFramePump();
            surface = null;
            disableTextureFallback();

            try {
                blurInput();
                cancelTouch();
                exitHover();
            } catch (Throwable ignored) {
            }

            if (lifecycleRegistered) {
                try {
                    activity.getApplication().unregisterActivityLifecycleCallbacks(this);
                } catch (Throwable ignored) {
                }
                lifecycleRegistered = false;
            }

            if (customFullscreenView != null && container != null) {
                try {
                    container.removeView(customFullscreenView);
                } catch (Throwable ignored) {
                }
                customFullscreenView = null;
            }
            if (customViewCallback != null) {
                try {
                    customViewCallback.onCustomViewHidden();
                } catch (Throwable ignored) {
                }
                customViewCallback = null;
            }

            if (webView != null) {
                webView.onInvalidatedCallback = null;
                try {
                    webView.stopLoading();
                    webView.loadUrl("about:blank");
                    webView.onPause();
                } catch (Throwable ignored) {
                }
                if (container != null) {
                    try {
                        container.removeView(webView);
                    } catch (Throwable ignored) {
                    }
                }
                try {
                    webView.destroy();
                } catch (Throwable ignored) {
                }
                webView = null;
            }

            if (container != null) {
                if (container.getParent() instanceof ViewGroup) {
                    try {
                        ((ViewGroup) container.getParent()).removeView(container);
                    } catch (Throwable ignored) {
                    }
                }
                container = null;
            }
        }

        @Override public void onActivityCreated(Activity a, Bundle b) {}
        @Override public void onActivityStarted(Activity a) {}
        @Override public void onActivityResumed(Activity a) { if (a == activity) setPaused(false); }
        @Override public void onActivityPaused(Activity a) { if (a == activity) setPaused(true); }
        @Override public void onActivityStopped(Activity a) { if (a == activity) setPaused(true); }
        @Override public void onActivitySaveInstanceState(Activity a, Bundle b) {}
        @Override
        public void onActivityDestroyed(Activity a) {
            if (a == activity && !closed) {
                SESSIONS.remove(sessionId);
                close();
            }
        }
    }
}
