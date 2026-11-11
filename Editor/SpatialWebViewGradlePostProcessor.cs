#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace SpatialWebView.Editor
{
    /// <summary>
    /// Automatically appends R8/ProGuard keep rules for <c>com.thisisvaze.spatialwebview.**</c> to the exported
    /// Android Gradle library so Release builds with Minify enabled never strip JNI or @JavascriptInterface methods.
    /// </summary>
    public sealed class SpatialWebViewGradlePostProcessor : IPostGenerateGradleAndroidProject
    {
        private const string KeepRuleMarker = "# SpatialWebView Keep Rules";
        private const string KeepRules =
            "\n# SpatialWebView Keep Rules\n" +
            "-keep class com.thisisvaze.spatialwebview.** { *; }\n" +
            "-keepclassmembers class * {\n" +
            "    @android.webkit.JavascriptInterface <methods>;\n" +
            "}\n";

        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            string proguardPath = Path.Combine(path, "proguard-unity.txt");
            if (!File.Exists(proguardPath))
                return;

            string existing = File.ReadAllText(proguardPath);
            if (!existing.Contains(KeepRuleMarker))
            {
                File.AppendAllText(proguardPath, KeepRules);
                Debug.Log("[SpatialWebView] Injected Android R8/ProGuard keep rules.");
            }
        }
    }
}
#endif
