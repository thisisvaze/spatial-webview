using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace SpatialWebView
{
    /// <summary>
    /// Unity C# JNI bridge to <c>com.thisisvaze.spatialwebview.QuestWebViewBridge</c>.
    /// Supports both hardware compositor Surfaces (Meta OVROverlay / OpenXR CompositionLayers)
    /// and zero-allocation Direct-ByteBuffer Texture2D fallback for AR/VR smart glasses.
    /// </summary>
    public sealed class AndroidSurfaceWebViewBackend : ISpatialWebViewBackend
    {
        private static int nextSessionId = 1;

        private readonly int sessionId = Interlocked.Increment(ref nextSessionId);
        private readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> pendingJsRequests =
            new ConcurrentDictionary<int, TaskCompletionSource<string>>();

        private int nextRequestId;
        private bool disposed;

        public bool IsReady { get; private set; }
        public WebPageState CurrentState { get; private set; } =
            new WebPageState(string.Empty, string.Empty, false, false, false, 0, 0);

        public event Action Ready;
        public event Action<WebPageState> PageStateChanged;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorOccurred;

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string BridgeClassName = "com.thisisvaze.spatialwebview.QuestWebViewBridge";
        private AndroidJavaClass bridgeClass;
        private IntPtr attachSurfaceMethodId;
        private IntPtr getSeqMethodId;
        private IntPtr getBufMethodId;
        private readonly jvalue[] singleSessionArg = new jvalue[1];
        private BridgeCallbackProxy callbackProxy;

        private sealed class BridgeCallbackProxy : AndroidJavaProxy
        {
            private readonly AndroidSurfaceWebViewBackend owner;

            public BridgeCallbackProxy(AndroidSurfaceWebViewBackend owner)
                : base(BridgeClassName + "$Callback")
            {
                this.owner = owner;
            }

            public void onReady()
            {
                owner.Enqueue(() =>
                {
                    if (owner.disposed) return;
                    owner.IsReady = true;
                    owner.Ready?.Invoke();
                });
            }

            public void onPageState(
                string url,
                string title,
                bool canGoBack,
                bool canGoForward,
                bool isLoading,
                int progress,
                int errorCode)
            {
                var snapshot = new WebPageState(
                    url,
                    title,
                    canGoBack,
                    canGoForward,
                    isLoading,
                    progress,
                    errorCode);
                owner.Enqueue(() =>
                {
                    if (owner.disposed) return;
                    owner.CurrentState = snapshot;
                    owner.PageStateChanged?.Invoke(snapshot);
                });
            }

            public void onMessage(string payload)
            {
                owner.Enqueue(() =>
                {
                    if (!owner.disposed)
                    {
                        owner.MessageReceived?.Invoke(payload ?? string.Empty);
                    }
                });
            }

            public void onJsResult(int requestId, string jsonResult)
            {
                string decoded = WebContentFormatter.UnwrapWebViewJsonResult(jsonResult);
                owner.Enqueue(() =>
                {
                    if (owner.pendingJsRequests.TryRemove(requestId, out var tcs))
                    {
                        tcs.TrySetResult(decoded);
                    }
                });
            }

            public void onError(string message)
            {
                owner.Enqueue(() =>
                {
                    if (!owner.disposed)
                    {
                        owner.ErrorOccurred?.Invoke(message ?? "Unknown Android WebView error");
                    }
                });
            }
        }
#endif

        public void Initialize(int width, int height, int activeFps, int idleFps, string userAgentOverride)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridgeClass = new AndroidJavaClass(BridgeClassName);
                IntPtr rawClass = bridgeClass.GetRawClass();
                attachSurfaceMethodId = AndroidJNIHelper.GetMethodID(
                    rawClass,
                    "attachSurface",
                    "(ILandroid/view/Surface;II)V",
                    true);
                getSeqMethodId = AndroidJNIHelper.GetMethodID(
                    rawClass,
                    "getFallbackFrameSequence",
                    "(I)I",
                    true);
                getBufMethodId = AndroidJNIHelper.GetMethodID(
                    rawClass,
                    "getReadyFallbackBuffer",
                    "(I)Ljava/nio/ByteBuffer;",
                    true);
                singleSessionArg[0].i = sessionId;

                callbackProxy = new BridgeCallbackProxy(this);

                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                bridgeClass.CallStatic(
                    "openSession",
                    activity,
                    sessionId,
                    Mathf.Max(64, width),
                    Mathf.Max(64, height),
                    Mathf.Clamp(activeFps, 1, 120),
                    Mathf.Clamp(idleFps, 1, 120),
                    string.IsNullOrWhiteSpace(userAgentOverride) ? string.Empty : userAgentOverride,
                    callbackProxy);
            }
            catch (Exception ex)
            {
                Enqueue(() => ErrorOccurred?.Invoke("Android WebView init failed: " + ex.Message));
            }
#else
            IsReady = true;
            Ready?.Invoke();
#endif
        }

        public void AttachSurface(IntPtr androidSurfaceJObject, int width, int height)
        {
            if (disposed || androidSurfaceJObject == IntPtr.Zero) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridgeClass == null || attachSurfaceMethodId == IntPtr.Zero) return;
            jvalue[] args = new jvalue[4];
            args[0].i = sessionId;
            args[1].l = androidSurfaceJObject;
            args[2].i = Mathf.Max(64, width);
            args[3].i = Mathf.Max(64, height);
            AndroidJNI.CallStaticVoidMethod(bridgeClass.GetRawClass(), attachSurfaceMethodId, args);
#endif
        }

        public void DetachSurface()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("detachSurface", sessionId);
#endif
        }

        public void EnableTextureFallback(int width, int height)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("enableTextureFallback", sessionId, Mathf.Max(64, width), Mathf.Max(64, height));
#endif
        }

        public void DisableTextureFallback()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("disableTextureFallback", sessionId);
#endif
        }

        public bool TryUploadFallbackFrame(Texture2D targetTexture, ref int lastSequence)
        {
            if (disposed || targetTexture == null) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridgeClass == null || getSeqMethodId == IntPtr.Zero || getBufMethodId == IntPtr.Zero)
                return false;

            IntPtr rawClass = bridgeClass.GetRawClass();
            int seq = AndroidJNI.CallStaticIntMethod(rawClass, getSeqMethodId, singleSessionArg);
            if (seq <= 0 || seq == lastSequence)
                return false;

            IntPtr bufferLocalRef = AndroidJNI.CallStaticObjectMethod(rawClass, getBufMethodId, singleSessionArg);
            if (bufferLocalRef == IntPtr.Zero)
                return false;

            try
            {
                NativeArray<byte> directBytes = AndroidJNI.GetDirectByteBuffer(bufferLocalRef);
                int expectedBytes = targetTexture.width * targetTexture.height * 4;
                if (directBytes.IsCreated && directBytes.Length >= expectedBytes)
                {
                    targetTexture.LoadRawTextureData(directBytes);
                    targetTexture.Apply(updateMipmaps: true, makeNoLongerReadable: false);
                    lastSequence = seq;
                    return true;
                }
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(bufferLocalRef);
            }
#endif
            return false;
        }

        public void Resize(int width, int height)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("resize", sessionId, Mathf.Max(64, width), Mathf.Max(64, height));
#endif
        }

        public void LoadUrl(string url)
        {
            if (disposed || string.IsNullOrWhiteSpace(url)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("loadUrl", sessionId, url);
#endif
        }

        public void LoadHtml(string html, string baseUrl)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic(
                "loadHtml",
                sessionId,
                html ?? string.Empty,
                string.IsNullOrWhiteSpace(baseUrl) ? WebContentFormatter.DefaultEmbedOrigin : baseUrl);
#endif
        }

        public Task<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            if (disposed)
                return Task.FromResult<string>(null);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<string>(cancellationToken);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (bridgeClass == null)
                return Task.FromResult<string>(null);

            int requestId = Interlocked.Increment(ref nextRequestId);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingJsRequests[requestId] = tcs;

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    if (pendingJsRequests.TryRemove(requestId, out var removed))
                    {
                        removed.TrySetCanceled(cancellationToken);
                    }
                });
            }

            bridgeClass.CallStatic("evaluateJs", sessionId, requestId, script ?? string.Empty);
            return tcs.Task;
#else
            return Task.FromResult<string>(null);
#endif
        }

        public void GoBack()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("goBack", sessionId);
#endif
        }

        public void GoForward()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("goForward", sessionId);
#endif
        }

        public void Reload()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("reload", sessionId);
#endif
        }

        public void StopLoading()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("stopLoading", sessionId);
#endif
        }

        public void SetTextZoom(int percent)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("setTextZoom", sessionId, Mathf.Clamp(percent, 50, 250));
#endif
        }

        public void SetBackgroundColor(Color32 color)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            int argb = unchecked((int)(
                ((uint)color.a << 24) |
                ((uint)color.r << 16) |
                ((uint)color.g << 8) |
                color.b));
            bridgeClass?.CallStatic("setBackgroundColor", sessionId, argb);
#endif
        }

        public void SetFrameRates(int activeFps, int idleFps)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic(
                "setFrameRates",
                sessionId,
                Mathf.Clamp(activeFps, 1, 120),
                Mathf.Clamp(idleFps, 1, 120));
#endif
        }

        public void DispatchPointer(int motionAction, float normalizedX, float normalizedY)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("dispatchPointer", sessionId, motionAction, normalizedX, normalizedY);
#endif
        }

        public void DispatchHover(int hoverAction, float normalizedX, float normalizedY)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("dispatchHover", sessionId, hoverAction, normalizedX, normalizedY);
#endif
        }

        public void DispatchScroll(float normalizedX, float normalizedY, float deltaX, float deltaY)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("dispatchScroll", sessionId, normalizedX, normalizedY, deltaX, deltaY);
#endif
        }

        public void BlurInput()
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("blurInput", sessionId);
#endif
        }

        public void SetPaused(bool paused)
        {
            if (disposed) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            bridgeClass?.CallStatic("setPaused", sessionId, paused);
#endif
        }

        public void PumpMainThreadQueue()
        {
            int safety = 256;
            while (safety-- > 0 && mainThreadQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsReady = false;

            foreach (var kv in pendingJsRequests)
            {
                if (pendingJsRequests.TryRemove(kv.Key, out var tcs))
                {
                    tcs.TrySetCanceled();
                }
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                bridgeClass?.CallStatic("closeSession", sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpatialWebView] Error closing Android session: " + ex.Message);
            }
            finally
            {
                bridgeClass?.Dispose();
                bridgeClass = null;
                callbackProxy = null;
                attachSurfaceMethodId = IntPtr.Zero;
                getSeqMethodId = IntPtr.Zero;
                getBufMethodId = IntPtr.Zero;
            }
#endif
        }

        private void Enqueue(Action action)
        {
            if (!disposed && action != null)
            {
                mainThreadQueue.Enqueue(action);
            }
        }
    }
}
