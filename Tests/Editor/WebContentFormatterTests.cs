using NUnit.Framework;
using UnityEngine;

namespace SpatialWebView.Tests
{
    public sealed class WebContentFormatterTests
    {
        [TestCase("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ", 0)]
        [TestCase("https://youtu.be/dQw4w9WgXcQ?t=45", "dQw4w9WgXcQ", 45)]
        [TestCase("https://www.youtube.com/shorts/AbCdEfGhIjK", "AbCdEfGhIjK", 0)]
        [TestCase("https://m.youtube.com/watch?v=1234567890A&t=1m30s", "1234567890A", 90)]
        [TestCase("https://www.youtube-nocookie.com/embed/ZyXwVuTsRqP?start=12", "ZyXwVuTsRqP", 12)]
        public void ExtractsValidYouTubeIdsAndStartTimes(string url, string expectedId, int expectedStart)
        {
            bool ok = WebContentFormatter.TryExtractYouTubeVideoId(url, out string videoId, out int startSec);
            Assert.IsTrue(ok);
            Assert.AreEqual(expectedId, videoId);
            Assert.AreEqual(expectedStart, startSec);
        }

        [Test]
        public void RejectsNonYouTubeUrls()
        {
            Assert.IsFalse(WebContentFormatter.TryExtractYouTubeVideoId("https://en.wikipedia.org/wiki/Mars", out _, out _));
            Assert.IsFalse(WebContentFormatter.TryExtractYouTubeVideoId("https://youtube.com/watch?v=short", out _, out _));
        }

        [Test]
        public void BuildsError153SafeYouTubeEmbedHtml()
        {
            string html = WebContentFormatter.BuildYouTubeEmbedHtml(
                "dQw4w9WgXcQ",
                "https://example.com",
                autoplay: true,
                startSeconds: 30);

            StringAssert.Contains("strict-origin-when-cross-origin", html);
            StringAssert.Contains("https://www.youtube.com/embed/dQw4w9WgXcQ", html);
            StringAssert.Contains("origin=https%3A%2F%2Fexample.com", html);
            StringAssert.Contains("start=30", html);
        }

        [Test]
        public void ExtractsVimeoAndDirectVideoUrls()
        {
            Assert.IsTrue(WebContentFormatter.TryExtractVimeoVideoId("https://vimeo.com/76979871", out string vimeoId));
            Assert.AreEqual("76979871", vimeoId);

            Assert.IsTrue(WebContentFormatter.IsDirectVideoUrl("https://cdn.example.com/demo.mp4?token=1"));
            Assert.IsFalse(WebContentFormatter.IsDirectVideoUrl("https://cdn.example.com/article.html"));
        }

        [Test]
        public void BuildsReaderHtmlAndUnwrapsWebViewJsonStrings()
        {
            string reader = WebContentFormatter.BuildReaderHtml(
                "Solar System",
                "<p>Hello <b>Mars</b>.</p>",
                "https://en.wikipedia.org/wiki/Mars",
                ReaderTheme.Sepia);

            StringAssert.Contains("Solar System", reader);
            StringAssert.Contains("en.wikipedia.org", reader);
            StringAssert.Contains("#f3ead8", reader);

            Assert.AreEqual("Hello \"Quest\"\nLine 2", WebContentFormatter.UnwrapWebViewJsonResult("\"Hello \\\"Quest\\\"\\nLine 2\""));
            Assert.IsNull(WebContentFormatter.UnwrapWebViewJsonResult("null"));
        }

        [Test]
        public void MapsWorldPointOnQuadToTopLeftOriginWebViewUv()
        {
            var go = new GameObject("TestQuad");
            try
            {
                go.transform.position = new Vector3(0f, 1.5f, 2.0f);
                go.transform.localScale = new Vector3(1.2f, 0.8f, 1f);

                // Top-left corner in world space -> (0, 0) in Android WebView UV.
                Vector3 topLeft = go.transform.TransformPoint(new Vector3(-0.5f, 0.5f, 0f));
                Vector2 uvTopLeft = SpatialWebViewPointerInput.WorldPointToViewportUv(go.transform, topLeft);
                Assert.AreEqual(0f, uvTopLeft.x, 1e-4f);
                Assert.AreEqual(0f, uvTopLeft.y, 1e-4f);

                // Bottom-right corner in world space -> (1, 1) in Android WebView UV.
                Vector3 bottomRight = go.transform.TransformPoint(new Vector3(0.5f, -0.5f, 0f));
                Vector2 uvBottomRight = SpatialWebViewPointerInput.WorldPointToViewportUv(go.transform, bottomRight);
                Assert.AreEqual(1f, uvBottomRight.x, 1e-4f);
                Assert.AreEqual(1f, uvBottomRight.y, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SpatialSpringConvergesAndRespectsReducedMotion()
        {
            WebViewSpatialMotion.ReducedMotion = false;
            var spring = new SpatialSpring(WebViewSpatialMotion.PopFromScale) { Target = 1f };
            Assert.IsFalse(spring.AtRest);

            for (int i = 0; i < 90; i++)
            {
                spring.Step(1f / 60f, WebViewSpatialMotion.Soft, WebViewSpatialMotion.SoftDamping);
            }

            Assert.AreEqual(1f, spring.Value, 1e-3f);
            Assert.IsTrue(spring.AtRest);

            WebViewSpatialMotion.ReducedMotion = true;
            var instant = new SpatialSpring(0f) { Target = 1f };
            instant.Step(1f / 60f, WebViewSpatialMotion.Quick, WebViewSpatialMotion.SoftDamping);
            Assert.AreEqual(1f, instant.Value);
            Assert.IsTrue(instant.AtRest);
            WebViewSpatialMotion.ReducedMotion = false;
        }
    }
}
