# spatial-webview

A small Unity XR package that renders readable web pages and YouTube / HTML5 video onto a floating spatial panel across Meta Quest (2, Pro, 3, 3S), OpenXR headsets (Pico, Android XR, Vive), and AR/VR smart glasses - without bundling a browser engine or a C++ native plugin.

> **v0.1.0** - Android Java bridge and C# package are built and compile-checked against Unity 6 + Android SDK. Spring feel constants and headset layout are awaiting their first on-device tuning pass.

## How it renders (`SurfaceBackendMode.Auto`)

`spatial-webview` picks the best Android rendering path available at runtime, all with 0 KB of bundled native `.so` libraries:

1. **Meta Horizon OS (`OVROverlay.isExternalSurface`)** - used on Quest 2, Quest Pro, Quest 3 and Quest 3S when `com.meta.xr.sdk.core` is installed. Android's built-in Chromium `WebView` draws straight to a TimeWarp compositor `Surface` via `Surface.lockHardwareCanvas()`, with bicubic filtering and Vulkan-only build support
2. **OpenXR Composition Layers (`XR_KHR_android_surface_swapchain`)** - used on Pico, Android XR (Samsung / Google), Vive Focus and Snapdragon Spaces headsets when `com.unity.xr.compositionlayers` + `com.unity.xr.openxr` are installed
3. **Universal Direct-Buffer `Texture2D` fallback** - used automatically on AR/VR smart glasses (XREAL, Viture, RayNeo) or OpenXR runtimes without external surface support. Double-buffers a direct `ByteBuffer` from Java into `AndroidJNI.GetDirectByteBuffer` with zero per-frame C# heap allocations

## Features

- **Underlay punch-through** - sits behind Unity's eye buffer (in front of Passthrough) and punches an `RGBA = (0,0,0,0)` window into the viewport mesh, so hands, avatars, 3D models and rays closer to you still appear in front of the page
- **Built-in Reader mode** - strips cookie banners, navbars and sidebars into clean VR typography (`Dark`, `Sepia`, `Light` themes), or renders pre-cleaned HTML directly
- **YouTube Error 153 handled** - wraps `youtube.com/watch`, `youtu.be`, `shorts`, Vimeo and direct `.mp4` / `.m3u8` URLs in an HTTPS-origin `loadDataWithBaseURL` player with `strict-origin-when-cross-origin` referrer headers
- **Quiet spatial UI** - no desktop browser toolbar across the top. Just the content surface, a thin perimeter border that lights up as your hand or ray nears, and a small pill handle underneath that reveals context controls (`←`, `Reader`, zoom, `×`) only when approached. Springs follow Oleg Frolov's [`xr-prototypes`](https://github.com/Volorf/xr-prototypes) timings and stay completely still while you aim inside the page
- **Cross-vendor input** - works with any Unity `EventSystem` / XRI raycaster, plus built-in support for Meta `OVRHand` / `OVRInput`, OpenXR `Unity.XR.Hands` pinch rays, `UnityEngine.XR` controllers (with 1-in-3 pulsed haptics), and head-gaze + tap / Bluetooth clicker fallback for smart glasses
- **Editor preview** - shows active state, YouTube thumbnails and an *Open in Browser* button in Mac and Windows Play Mode so scenes run without an APK build

## Requirements

- Unity 6 (`6000.0+`) and `com.unity.ugui` (TextMeshPro)
- Optional packages (detected automatically via `versionDefines`):
  - `com.meta.xr.sdk.core` for Meta `OVROverlay`, `OVRHand` and `OVRInput`
  - `com.unity.xr.openxr` + `com.unity.xr.compositionlayers` for vendor-neutral OpenXR hardware surfaces
  - `com.unity.xr.hands` for vendor-neutral OpenXR hand pinch rays

## Install

In Unity, open **Window > Package Manager**, click **+ > Add package from git URL...**, and paste:

```text
https://github.com/thisisvaze/spatial-webview.git
```

## Usage

### From the menu

Click **GameObject > XR > Spatial WebView**, set **Initial Url** in the Inspector, and build to your headset or glasses.

### From C#

```csharp
using SpatialWebView;
using UnityEngine;

public class Example : MonoBehaviour
{
    [SerializeField] private SpatialWebViewWindow webView;

    public void ShowArticle()
    {
        webView.OpenUrl("https://en.wikipedia.org/wiki/James_Webb_Space_Telescope", forceReaderMode: true);
    }

    public void ShowVideo()
    {
        webView.OpenUrl("https://www.youtube.com/watch?v=21X5lGlDOfg");
    }

    public void ShowCleanHtml(string title, string bodyHtml)
    {
        webView.OpenReaderHtml(title, bodyHtml, "https://example.com");
    }
}
```

### Two-way JS messaging

From JavaScript inside a loaded page:

```js
window.xrBrowserPost({ type: "chapterSelected", id: 3 });
```

From Unity C#:

```csharp
webView.MessageReceived += json => Debug.Log("From page: " + json);
string title = await webView.EvaluateJavaScriptAsync("document.title");
```

### System keyboard on Meta Quest (optional)

If your pages have `<input>` fields and you want Horizon OS's overlay keyboard to open on tap, add this to `Assets/Plugins/Android/AndroidManifest.xml`:

```xml
<uses-feature android:name="oculus.software.overlay_keyboard" android:required="false" />
```

## License

MIT
