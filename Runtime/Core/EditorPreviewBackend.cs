using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialWebView
{
    /// <summary>
    /// Desktop/Editor Play Mode backend so spatial browser scenes, navigation history,
    /// Reader/YouTube state, and C# callbacks run on macOS and Windows without building an APK.
    /// </summary>
    public sealed class EditorPreviewBackend : ISpatialWebViewBackend
    {
        private readonly List<string> history = new List<string>();
        private int historyIndex = -1;
        private bool disposed;

        public bool IsReady { get; private set; }
        public WebPageState CurrentState { get; private set; } =
            new WebPageState(string.Empty, string.Empty, false, false, false, 0, 0);

        public string LastLoadedHtml { get; private set; }

        public event Action Ready;
        public event Action<WebPageState> PageStateChanged;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorOccurred;

        public void Initialize(int width, int height, int activeFps, int idleFps, string userAgentOverride)
        {
            if (disposed) return;
            IsReady = true;
            Ready?.Invoke();
        }

        public void AttachSurface(IntPtr androidSurfaceJObject, int width, int height) { }
        public void DetachSurface() { }
        public void EnableTextureFallback(int width, int height) { }
        public void DisableTextureFallback() { }
        public bool TryUploadFallbackFrame(Texture2D targetTexture, ref int lastSequence) => false;
        public void Resize(int width, int height) { }

        public void LoadUrl(string url)
        {
            if (disposed) return;
            LastLoadedHtml = null;
            PushHistory(url ?? string.Empty);
            EmitState(url ?? string.Empty, ExtractFallbackTitle(url));
        }

        public void LoadHtml(string html, string baseUrl)
        {
            if (disposed) return;
            LastLoadedHtml = html ?? string.Empty;
            string effectiveUrl = string.IsNullOrWhiteSpace(baseUrl) ? "about:html" : baseUrl;
            PushHistory(effectiveUrl);
            string title = ExtractHtmlTitle(html) ?? ExtractFallbackTitle(effectiveUrl);
            EmitState(effectiveUrl, title);
        }

        public Task<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default)
        {
            if (disposed || cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<string>(cancellationToken);
            return Task.FromResult<string>(null);
        }

        public void GoBack()
        {
            if (disposed || historyIndex <= 0) return;
            historyIndex--;
            string url = history[historyIndex];
            EmitState(url, ExtractFallbackTitle(url));
        }

        public void GoForward()
        {
            if (disposed || historyIndex < 0 || historyIndex >= history.Count - 1) return;
            historyIndex++;
            string url = history[historyIndex];
            EmitState(url, ExtractFallbackTitle(url));
        }

        public void Reload()
        {
            if (disposed || historyIndex < 0) return;
            string url = history[historyIndex];
            EmitState(url, CurrentState.Title);
        }

        public void StopLoading() { }
        public void SetTextZoom(int percent) { }
        public void SetBackgroundColor(Color32 color) { }
        public void SetFrameRates(int activeFps, int idleFps) { }
        public void DispatchPointer(int motionAction, float normalizedX, float normalizedY) { }
        public void DispatchHover(int hoverAction, float normalizedX, float normalizedY) { }
        public void DispatchScroll(float normalizedX, float normalizedY, float deltaX, float deltaY) { }
        public void BlurInput() { }
        public void SetPaused(bool paused) { }
        public void PumpMainThreadQueue() { }

        public void SimulateMessageFromJs(string message) => MessageReceived?.Invoke(message ?? string.Empty);
        public void SimulateError(string error) => ErrorOccurred?.Invoke(error ?? string.Empty);

        public void Dispose()
        {
            disposed = true;
            IsReady = false;
        }

        private void PushHistory(string url)
        {
            if (historyIndex >= 0 && historyIndex < history.Count - 1)
            {
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            }
            if (historyIndex < 0 || history[historyIndex] != url)
            {
                history.Add(url);
                historyIndex = history.Count - 1;
            }
        }

        private void EmitState(string url, string title)
        {
            CurrentState = new WebPageState(
                url,
                title ?? string.Empty,
                historyIndex > 0,
                historyIndex >= 0 && historyIndex < history.Count - 1,
                false,
                100,
                0);
            PageStateChanged?.Invoke(CurrentState);
        }

        private static string ExtractHtmlTitle(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            Match m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
        }

        private static string ExtractFallbackTitle(string url)
        {
            if (WebContentFormatter.TryExtractYouTubeVideoId(url, out string ytId, out _))
                return $"YouTube ({ytId})";
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                return uri.Host;
            return url ?? "Ready";
        }
    }
}
