using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SpatialWebView
{
    /// <summary>
    /// Formats URLs, Error-153-safe YouTube/Vimeo/HTML5 video embeds, and distraction-free VR Reader HTML/JS.
    /// </summary>
    public static class WebContentFormatter
    {
        public const string DefaultEmbedOrigin = "https://player.spatialwebview.local";

        private static readonly Regex YouTubeIdPattern = new Regex(
            @"^[A-Za-z0-9_-]{11}$",
            RegexOptions.Compiled);

        public static string NormalizeUrlOrSearch(string input, string searchTemplate = "https://duckduckgo.com/?q={0}")
        {
            if (string.IsNullOrWhiteSpace(input))
                return "about:blank";

            string trimmed = input.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            // Treat single-token domain-like strings (e.g. "en.wikipedia.org/wiki/Mars") as HTTPS URLs.
            if (!trimmed.Contains(' ') && trimmed.Contains('.'))
            {
                return "https://" + trimmed;
            }

            return string.Format(searchTemplate, Uri.EscapeDataString(trimmed));
        }

        public static bool TryExtractYouTubeVideoId(string url, out string videoId, out int startSeconds)
        {
            videoId = null;
            startSeconds = 0;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            string normalized = NormalizeUrlOrSearch(url);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri uri))
                return false;

            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);
            if (host.StartsWith("m.")) host = host.Substring(2);

            string candidate = null;
            if (host == "youtu.be")
            {
                string seg = uri.AbsolutePath.Trim('/');
                int slash = seg.IndexOf('/');
                candidate = slash >= 0 ? seg.Substring(0, slash) : seg;
            }
            else if (host == "youtube.com" || host == "youtube-nocookie.com")
            {
                string path = uri.AbsolutePath.Trim('/');
                if (path.Equals("watch", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = GetQueryParam(uri.Query, "v");
                }
                else if (path.StartsWith("embed/", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("live/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = path.Substring(path.IndexOf('/') + 1);
                    int slash = rest.IndexOf('/');
                    candidate = slash >= 0 ? rest.Substring(0, slash) : rest;
                }
            }

            if (string.IsNullOrEmpty(candidate) || !YouTubeIdPattern.IsMatch(candidate))
                return false;

            videoId = candidate;
            string tParam = GetQueryParam(uri.Query, "t") ?? GetQueryParam(uri.Query, "start");
            if (!string.IsNullOrEmpty(tParam))
            {
                startSeconds = ParseDurationSeconds(tParam);
            }
            return true;
        }

        public static bool TryExtractVimeoVideoId(string url, out string vimeoId)
        {
            vimeoId = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            string normalized = NormalizeUrlOrSearch(url);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri uri))
                return false;

            string host = uri.Host.ToLowerInvariant();
            if (host != "vimeo.com" && host != "www.vimeo.com" && host != "player.vimeo.com")
                return false;

            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                if (IsAllDigits(segments[i]) && segments[i].Length >= 6)
                {
                    vimeoId = segments[i];
                    return true;
                }
            }
            return false;
        }

        public static bool IsDirectVideoUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(NormalizeUrlOrSearch(url), UriKind.Absolute, out Uri uri))
                return false;

            string path = uri.AbsolutePath.ToLowerInvariant();
            return path.EndsWith(".mp4") ||
                   path.EndsWith(".webm") ||
                   path.EndsWith(".ogg") ||
                   path.EndsWith(".mov") ||
                   path.EndsWith(".m3u8");
        }

        /// <summary>
        /// Builds an Error-153-safe YouTube embed HTML document designed to be loaded via
        /// loadDataWithBaseURL(originUrl, html, "text/html", "UTF-8", null) so Android WebView
        /// sends a valid HTTPS Referer and Origin header to YouTube.
        /// </summary>
        public static string BuildYouTubeEmbedHtml(
            string videoId,
            string originUrl = DefaultEmbedOrigin,
            bool autoplay = true,
            int startSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(videoId) || !YouTubeIdPattern.IsMatch(videoId))
                throw new ArgumentException("Invalid YouTube video ID.", nameof(videoId));

            string safeOrigin = NormalizeOrigin(originUrl);
            var query = new StringBuilder();
            query.Append("autoplay=").Append(autoplay ? "1" : "0");
            query.Append("&playsinline=1&rel=0&modestbranding=1&enablejsapi=1");
            query.Append("&origin=").Append(Uri.EscapeDataString(safeOrigin));
            if (startSeconds > 0)
            {
                query.Append("&start=").Append(startSeconds);
            }

            string embedSrc = $"https://www.youtube.com/embed/{videoId}?{query}";
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"">
<meta name=""referrer"" content=""strict-origin-when-cross-origin"">
<title>YouTube Video</title>
<style>
  html, body {
    margin: 0; padding: 0; width: 100%; height: 100%;
    background: #0b0d12; overflow: hidden;
    font-family: system-ui, -apple-system, sans-serif;
  }
  #player {
    position: fixed; inset: 0; width: 100%; height: 100%; border: 0;
  }
</style>
</head>
<body>
<iframe id=""player""
  src=""" + WebUtility.HtmlEncode(embedSrc) + @"""
  referrerpolicy=""strict-origin-when-cross-origin""
  allow=""accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share""
  allowfullscreen></iframe>
<script>
  window.xrPlayerCommand = function(funcName, args) {
    var frame = document.getElementById('player');
    if (frame && frame.contentWindow) {
      frame.contentWindow.postMessage(JSON.stringify({
        event: 'command',
        func: funcName,
        args: args || []
      }), '*');
    }
  };
  if (window.xrBrowser && window.xrBrowser.notifyMediaActive) {
    window.xrBrowser.notifyMediaActive(true);
  }
</script>
</body>
</html>";
        }

        public static string BuildVimeoEmbedHtml(string vimeoId, bool autoplay = true)
        {
            if (string.IsNullOrWhiteSpace(vimeoId) || !IsAllDigits(vimeoId))
                throw new ArgumentException("Invalid Vimeo ID.", nameof(vimeoId));

            string embedSrc = $"https://player.vimeo.com/video/{vimeoId}?autoplay={(autoplay ? 1 : 0)}&dnt=1";
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<meta name=""referrer"" content=""strict-origin-when-cross-origin"">
<title>Vimeo Video</title>
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #0b0d12; overflow: hidden; }
  iframe { position: fixed; inset: 0; width: 100%; height: 100%; border: 0; }
</style>
</head>
<body>
<iframe src=""" + WebUtility.HtmlEncode(embedSrc) + @"""
  referrerpolicy=""strict-origin-when-cross-origin""
  allow=""autoplay; fullscreen; picture-in-picture""
  allowfullscreen></iframe>
</body>
</html>";
        }

        public static string BuildDirectVideoHtml(string videoUrl, bool autoplay = true)
        {
            string safeUrl = WebUtility.HtmlEncode(NormalizeUrlOrSearch(videoUrl));
            string autoAttr = autoplay ? " autoplay" : string.Empty;
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Video Player</title>
<style>
  html, body {
    margin: 0; padding: 0; width: 100%; height: 100%;
    background: #0b0d12; display: flex; align-items: center; justify-content: center; overflow: hidden;
  }
  video { width: 100%; height: 100%; object-fit: contain; outline: none; }
</style>
</head>
<body>
<video id=""xrVideo"" src=""" + safeUrl + @""" controls playsinline" + autoAttr + @"></video>
<script>
  window.xrPlayerCommand = function(cmd) {
    var v = document.getElementById('xrVideo');
    if (!v) return;
    if (cmd === 'playVideo' || cmd === 'play') v.play();
    else if (cmd === 'pauseVideo' || cmd === 'pause') v.pause();
  };
</script>
</body>
</html>";
        }

        /// <summary>
        /// Wraps clean article HTML in a high-contrast, comfortable VR Reader stylesheet.
        /// </summary>
        public static string BuildReaderHtml(
            string title,
            string bodyHtml,
            string sourceUrl = null,
            ReaderTheme theme = ReaderTheme.Dark)
        {
            GetThemeColors(theme, out string bg, out string fg, out string muted, out string surface, out string accent);
            string encodedTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "Article" : title);
            string domainLine = string.Empty;
            if (!string.IsNullOrWhiteSpace(sourceUrl) && Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri u))
            {
                domainLine = $"<div class=\"meta\">{WebUtility.HtmlEncode(u.Host)}</div>";
            }

            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>" + encodedTitle + @"</title>
<style>
  :root {
    --bg: " + bg + @";
    --fg: " + fg + @";
    --muted: " + muted + @";
    --surface: " + surface + @";
    --accent: " + accent + @";
  }
  * { box-sizing: border-box; }
  html, body {
    margin: 0; padding: 0;
    background: var(--bg); color: var(--fg);
    font-family: Georgia, 'Iowan Old Style', 'Noto Serif', system-ui, serif;
    font-size: 22px; line-height: 1.68;
    -webkit-font-smoothing: antialiased;
  }
  main {
    max-width: 40rem;
    margin: 0 auto;
    padding: 2.2rem 2.4rem 4rem;
  }
  h1 {
    font-family: system-ui, -apple-system, sans-serif;
    font-size: 1.75rem; line-height: 1.28; margin: 0 0 0.5rem;
  }
  h2, h3, h4 {
    font-family: system-ui, -apple-system, sans-serif;
    line-height: 1.35; margin-top: 1.8rem;
  }
  .meta {
    font-family: system-ui, -apple-system, sans-serif;
    color: var(--muted); font-size: 0.82rem; margin-bottom: 1.6rem;
  }
  a { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
  img, video, figure {
    max-width: 100%; height: auto; border-radius: 10px; margin: 1.2rem 0;
  }
  pre, code {
    font-family: ui-monospace, Menlo, Consolas, monospace;
    background: var(--surface); border-radius: 6px;
  }
  code { padding: 0.12em 0.35em; font-size: 0.88em; }
  pre { padding: 0.9rem 1.1rem; overflow-x: auto; font-size: 0.82em; line-height: 1.5; }
  blockquote {
    margin: 1.2rem 0; padding: 0.6rem 1.2rem;
    border-left: 4px solid var(--accent); background: var(--surface); border-radius: 0 8px 8px 0;
  }
  table { border-collapse: collapse; width: 100%; margin: 1.2rem 0; font-size: 0.9em; }
  th, td { border: 1px solid var(--surface); padding: 0.5rem 0.7rem; text-align: left; }
</style>
</head>
<body>
<main>
  <h1>" + encodedTitle + @"</h1>
  " + domainLine + @"
  <article>" + (bodyHtml ?? string.Empty) + @"</article>
</main>
</body>
</html>";
        }

        /// <summary>
        /// Returns an idempotent JavaScript snippet that extracts the main readable article container on
        /// live webpages, hides cookie/nav/sidebar clutter, and applies comfortable VR Reader typography.
        /// Pass enable=false to reload/restore the original page.
        /// </summary>
        public static string BuildLiveReaderModeScript(ReaderTheme theme)
        {
            GetThemeColors(theme, out string bg, out string fg, out string muted, out string surface, out string accent);
            return @"(function(){
  try {
    var root = document.querySelector('article, [role=""main""], main, .post-content, .article-body, #mw-content-text');
    if (!root) {
      var best = null, bestScore = 0;
      var candidates = document.querySelectorAll('div, section');
      for (var i = 0; i < candidates.length; i++) {
        var el = candidates[i];
        var ps = el.querySelectorAll('p');
        if (ps.length < 2) continue;
        var textLen = 0;
        for (var j = 0; j < ps.length; j++) textLen += (ps[j].innerText || '').length;
        if (textLen > bestScore && textLen > 350) { bestScore = textLen; best = el; }
      }
      root = best;
    }
    if (!root) return 'no_article_found';
    var titleText = document.title || '';
    var h1 = document.querySelector('h1');
    if (h1 && h1.innerText && h1.innerText.trim().length > 3) titleText = h1.innerText.trim();
    var clone = root.cloneNode(true);
    var junk = clone.querySelectorAll('script, style, nav, aside, footer, form, iframe:not([src*=""youtube""]):not([src*=""vimeo""]), .ad, .ads, .sidebar, .cookie, [aria-hidden=""true""]');
    for (var k = 0; k < junk.length; k++) junk[k].remove();
    var firstH1 = clone.querySelector('h1');
    if (firstH1) firstH1.remove();
    document.head.innerHTML = '<meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">' +
      '<title>' + titleText.replace(/</g,'&lt;') + '</title>' +
      '<style>' +
      ':root{--bg:" + bg + @";--fg:" + fg + @";--muted:" + muted + @";--surface:" + surface + @";--accent:" + accent + @";}' +
      '*{box-sizing:border-box;}' +
      'html,body{margin:0;padding:0;background:var(--bg)!important;color:var(--fg)!important;font-family:Georgia,system-ui,serif;font-size:22px;line-height:1.68;}' +
      '#__xrReader{max-width:40rem;margin:0 auto;padding:2rem 2.4rem 4rem;}' +
      'h1,h2,h3,h4{font-family:system-ui,-apple-system,sans-serif;color:var(--fg)!important;line-height:1.3;}' +
      'h1{font-size:1.75rem;margin:0 0 0.4rem;}' +
      '.meta{font-family:system-ui,sans-serif;font-size:0.8rem;color:var(--muted);margin-bottom:1.5rem;}' +
      'a{color:var(--accent)!important;}' +
      'img,video,figure{max-width:100%!important;height:auto!important;border-radius:10px;}' +
      'pre,code{background:var(--surface)!important;color:var(--fg)!important;border-radius:6px;font-family:monospace;}' +
      'pre{padding:0.9rem;overflow-x:auto;font-size:0.82em;}' +
      'blockquote{margin:1.2rem 0;padding:0.6rem 1.2rem;border-left:4px solid var(--accent);background:var(--surface);}' +
      '</style>';
    document.body.innerHTML = '<main id=""__xrReader""><h1></h1><div class=""meta""></div><article id=""__xrArticle""></article></main>';
    document.querySelector('#__xrReader h1').textContent = titleText;
    document.querySelector('#__xrReader .meta').textContent = location.hostname || '';
    document.getElementById('__xrArticle').appendChild(clone);
    window.scrollTo(0, 0);
    return 'reader_applied';
  } catch (e) {
    return 'reader_error:' + e.message;
  }
})();";
        }

        public static string BuildThemeUpdateScript(ReaderTheme theme)
        {
            GetThemeColors(theme, out string bg, out string fg, out string muted, out string surface, out string accent);
            return $@"(function(){{
  var r = document.documentElement;
  if (!r) return;
  r.style.setProperty('--bg', '{bg}');
  r.style.setProperty('--fg', '{fg}');
  r.style.setProperty('--muted', '{muted}');
  r.style.setProperty('--surface', '{surface}');
  r.style.setProperty('--accent', '{accent}');
}})();";
        }

        public static void GetThemeColors(
            ReaderTheme theme,
            out string bg,
            out string fg,
            out string muted,
            out string surface,
            out string accent)
        {
            switch (theme)
            {
                case ReaderTheme.Light:
                    bg = "#f7f8fa";
                    fg = "#191c24";
                    muted = "#5e6573";
                    surface = "#e8ebf0";
                    accent = "#1d5fd6";
                    break;
                case ReaderTheme.Sepia:
                    bg = "#f3ead8";
                    fg = "#2c241b";
                    muted = "#6e5f4f";
                    surface = "#e6dac3";
                    accent = "#8c4a19";
                    break;
                default:
                    bg = "#12151c";
                    fg = "#e8ecf4";
                    muted = "#949db0";
                    surface = "#1d222e";
                    accent = "#6cb2ff";
                    break;
            }
        }

        public static string NormalizeOrigin(string originUrl)
        {
            if (!string.IsNullOrWhiteSpace(originUrl) &&
                Uri.TryCreate(originUrl.Trim(), UriKind.Absolute, out Uri uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                return $"{uri.Scheme}://{uri.Authority}";
            }
            return DefaultEmbedOrigin;
        }

        /// <summary>
        /// Decodes JSON-encoded string results returned by Android WebView.evaluateJavascript.
        /// </summary>
        public static string UnwrapWebViewJsonResult(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson) || rawJson == "null")
                return null;

            if (rawJson.Length >= 2 && rawJson[0] == '"' && rawJson[rawJson.Length - 1] == '"')
            {
                var sb = new StringBuilder(rawJson.Length - 2);
                for (int i = 1; i < rawJson.Length - 1; i++)
                {
                    char c = rawJson[i];
                    if (c == '\\' && i + 1 < rawJson.Length - 1)
                    {
                        char next = rawJson[++i];
                        switch (next)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (i + 4 < rawJson.Length - 1 &&
                                    int.TryParse(rawJson.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        null,
                                        out int codeUnit))
                                {
                                    sb.Append((char)codeUnit);
                                    i += 4;
                                }
                                break;
                            default:
                                sb.Append(next);
                                break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            return rawJson;
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return null;
            string trimmed = query.TrimStart('?');
            string[] pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return null;
        }

        private static int ParseDurationSeconds(string value)
        {
            if (int.TryParse(value, out int plainSeconds))
                return Math.Max(0, plainSeconds);

            int total = 0;
            int current = 0;
            foreach (char c in value.ToLowerInvariant())
            {
                if (c >= '0' && c <= '9')
                {
                    current = current * 10 + (c - '0');
                }
                else if (c == 'h')
                {
                    total += current * 3600;
                    current = 0;
                }
                else if (c == 'm')
                {
                    total += current * 60;
                    current = 0;
                }
                else if (c == 's')
                {
                    total += current;
                    current = 0;
                }
            }
            return total + current;
        }

        private static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }
    }
}
