using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Tiny Wikipedia client for Unity.
/// - Gets up to N image URLs from an article via REST /page/media-list/{title}
/// - Gets up to N linked article titles via action=query&prop=links
/// NOTE: Titles must be URL-encoded (we encode internally).
/// </summary>
public class WikiImageService : MonoBehaviour
{
    [Header("Debug")]
    public bool logDebug = false;

    // Prefer the REST media-list endpoint (simpler to parse than action=query for images)
    private string MediaListUrl(string title) =>
        "https://en.wikipedia.org/api/rest_v1/page/media-list/" + Uri.EscapeDataString(title);

    // Use action=query to get normal-namespace links from a page
    private string LinksUrl(string title, int limit) =>
        "https://en.wikipedia.org/w/api.php?action=query&format=json&origin=*&prop=links&plnamespace=0&pllimit="
        + limit + "&titles=" + Uri.EscapeDataString(title);

    /// <summary>
    /// Download JSON from url and return as string.
    /// </summary>
    private IEnumerator GetJson(string url, Action<string> onDone, Action<string> onError)
    {
        Debug.Log("GetJson");
        using (var req = UnityWebRequest.Get(url))
        {
            yield return req.SendWebRequest();

    #if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
    #else
            if (req.isNetworkError || req.isHttpError)
    #endif
            {
                if (req.responseCode == 404)
                {
                    onError?.Invoke("Page not found (404)");
                }
                else
                {
                    onError?.Invoke(req.error);
                }
            }
            else
            {
                onDone?.Invoke(req.downloadHandler.text);
            }
        }
    }

    /// <summary>
    /// Get up to maxCount image URLs for a page title.
    /// The REST media-list returns various items; we filter to type=image and pick a reasonable src.
    /// </summary>
    public IEnumerator GetImageUrls(string title, int maxCount, Action<List<string>> onDone, Action<string> onError)
    {
        Debug.Log("GetImageUrls");
        var url = MediaListUrl(title);
        yield return GetJson(url, (json) =>
        {
            try
            {
                var urls = ParseImageUrlsFromMediaList(json, maxCount);
                onDone?.Invoke(urls);
            }
            catch (Exception ex)
            {
                onError?.Invoke("Parse images failed: " + ex.Message);
            }
        }, onError);
    }

    /// <summary>
    /// Get up to maxCount linked article titles (namespace 0) from a page.
    /// </summary>
    public IEnumerator GetLinkedArticleTitles(string title, int maxCount, Action<List<string>> onDone, Action<string> onError)
    {
        Debug.Log("GetLinkedArticleTitles");
        var url = LinksUrl(title, Math.Max(2, maxCount));
        yield return GetJson(url, (json) =>
        {
            try
            {
                var titles = ParseLinksFromQuery(json, maxCount);
                onDone?.Invoke(titles);
            }
            catch (Exception ex)
            {
                onError?.Invoke("Parse links failed: " + ex.Message);
            }
        }, onError);
    }

    // -----------------------
    // Minimal JSON parsing
    // -----------------------

    // We keep parsing super-lightweight using Unity's JsonUtility for small structs + manual dictionary decoding via simple helpers.
    // To avoid pulling third-party libs, we do very small ad-hoc parsing with Unity's built-in JsonUtility where feasible.

    [Serializable]
    private class QueryRoot { public Query query; }
    [Serializable]
    private class Query { public Page[] pages; }
    [Serializable]
    private class Page { public int pageid; public string title; public Link[] links; }
    [Serializable]
    private class Link { public string ns; public string title; } // ns is string in JSON sometimes; we'll ignore and trust API filter

    // The REST media-list JSON is not easily represented with strict classes due to variability.
    // We'll use a tiny lightweight parser via MiniJson-like approach:
    // Below is a very small JSON -> Dictionary parser adapted for our fields (not a full JSON parser).
    // For robustness in production, consider a full JSON lib (e.g., Newtonsoft) via UPM.

    // --- Tiny JSON util (extremely small, only what we need) ---
    private static class TinyJson
    {
        public static object Parse(string json)
        {
            return new Parser(json).ParseValue();
        }

        private class Parser
        {
            private readonly string s;
            private int i;
            public Parser(string s) { this.s = s; i = 0; }

            public object ParseValue()
            {
                SkipWs();
                if (i >= s.Length) return null;
                char c = s[i];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '\"') return ParseString();
                if (char.IsDigit(c) || c == '-') return ParseNumber();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                throw new Exception("Unexpected token at " + i);
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                i++; // skip {
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length) throw new Exception("Unclosed object");
                    if (s[i] == '}') { i++; break; }
                    var key = ParseString();
                    SkipWs();
                    if (s[i] != ':') throw new Exception("Expected ':'");
                    i++;
                    var val = ParseValue();
                    dict[key] = val;
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; break; }
                }
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                i++; // skip [
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length) throw new Exception("Unclosed array");
                    if (s[i] == ']') { i++; break; }
                    var val = ParseValue();
                    list.Add(val);
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; break; }
                }
                return list;
            }

            private string ParseString()
            {
                if (s[i] != '\"') throw new Exception("Expected '\"'");
                i++;
                var start = i;
                var result = "";
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '\"') break;
                    if (c == '\\')
                    {
                        if (i >= s.Length) break;
                        char esc = s[i++];
                        switch (esc)
                        {
                            case '\"': result += '\"'; break;
                            case '\\': result += '\\'; break;
                            case '/': result += '/'; break;
                            case 'b': result += '\b'; break;
                            case 'f': result += '\f'; break;
                            case 'n': result += '\n'; break;
                            case 'r': result += '\r'; break;
                            case 't': result += '\t'; break;
                            case 'u':
                                if (i + 4 <= s.Length)
                                {
                                    string hex = s.Substring(i, 4);
                                    result += (char)Convert.ToInt32(hex, 16);
                                    i += 4;
                                }
                                break;
                            default: result += esc; break;
                        }
                    }
                    else
                    {
                        result += c;
                    }
                }
                return result;
            }

            private object ParseNumber()
            {
                int start = i;
                while (i < s.Length && "-+0123456789.eE".IndexOf(s[i]) >= 0) i++;
                var numStr = s.Substring(start, i - start);
                if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
                {
                    if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return d;
                }
                else
                {
                    if (long.TryParse(numStr, out var l)) return l;
                }
                throw new Exception("Invalid number: " + numStr);
            }

            private void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            private bool Match(string kw)
            {
                SkipWs();
                if (i + kw.Length <= s.Length && string.Compare(s, i, kw, 0, kw.Length, StringComparison.Ordinal) == 0)
                {
                    i += kw.Length;
                    return true;
                }
                return false;
            }
        }
    }

    private List<string> ParseImageUrlsFromMediaList(string json, int maxCount)
    {
        Debug.Log("ParseImageUrlsFromMediaList");
        var result = new List<string>(maxCount);
        var root = TinyJson.Parse(json) as Dictionary<string, object>;
        if (root == null || !root.ContainsKey("items")) return result;

        var items = root["items"] as List<object>;
        if (items == null) return result;

        foreach (var it in items)
        {
            if (result.Count >= maxCount) break;
            var dict = it as Dictionary<string, object>;
            if (dict == null) continue;

            // Only care about images
            if (!dict.ContainsKey("type") || (dict["type"] as string) != "image") continue;

            // Pick best URL
            string best = TryPickBestImageSrc(dict);

            // Skip null/empty URLs
            if (string.IsNullOrEmpty(best)) continue;

            // Only allow supported formats
            string lower = best.ToLower();

            // Block all SVG thumbnails disguised as PNG
            if (lower.Contains(".svg") && lower.EndsWith(".png"))
            {
                if (logDebug) Debug.Log($"[IMG] Skipping rasterized SVG thumbnail: {best}");
                continue;
            }

            if ((lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png"))
            )
            {
                result.Add(best);
            }
            else
            {
                if (logDebug) Debug.Log($"[IMG] Skipping unsupported format: {best}");
            }
        }

        return result;
    }

    private string TryPickBestImageSrc(Dictionary<string, object> mediaItem)
    {
        Debug.Log("TryPickBestImageSrc");
        // 1. Prefer: original.source (full-resolution, always https)
        if (mediaItem.TryGetValue("original", out var origObj) &&
            origObj is Dictionary<string, object> original &&
            original.TryGetValue("source", out var origSourceObj))
        {
            return NormalizeWikiUrl(origSourceObj as string);
        }

        // 2. Then: thumbnail.source (always https)
        if (mediaItem.TryGetValue("thumbnail", out var thumbObj) &&
            thumbObj is Dictionary<string, object> thumb &&
            thumb.TryGetValue("source", out var thumbSourceObj))
        {
            return NormalizeWikiUrl(thumbSourceObj as string);
        }

        // 3. Then: largest srcset entry (may be // or http)
        if (mediaItem.TryGetValue("srcset", out var srcsetObj) &&
            srcsetObj is List<object> srcset &&
            srcset.Count > 0)
        {
            var last = srcset[srcset.Count - 1] as Dictionary<string, object>;
            if (last != null && last.TryGetValue("src", out var lastSrcObj))
            {
                return NormalizeWikiUrl(lastSrcObj as string);
            }
        }

        // 4. Finally: src (may be // or http)
        if (mediaItem.TryGetValue("src", out var srcObj))
        {
            return NormalizeWikiUrl(srcObj as string);
        }

        return null;
    }

    private string NormalizeWikiUrl(string url)
    {
        Debug.Log("NormalizeWikiUrl");
        if (string.IsNullOrEmpty(url)) return null;

        // Convert protocol-relative URLs like //upload.wikimedia.org/... → https://...
        if (url.StartsWith("//"))
            return "https:" + url;

        // Convert insecure URLs http://... → https://...
        if (url.StartsWith("http://"))
            return "https://" + url.Substring(7);

        return url;
    }


    private List<string> ParseLinksFromQuery(string json, int maxCount)
    {
        Debug.Log("ParseLinksFromQuery");
        // We'll lightly transform to classes where possible
        // But page keys are dynamic, so we first use TinyJson to locate pages[*].links[*].title
        var result = new List<string>(maxCount);
        var root = TinyJson.Parse(json) as Dictionary<string, object>;
        if (root == null || !root.ContainsKey("query")) return result;

        var query = root["query"] as Dictionary<string, object>;
        if (query == null || !query.ContainsKey("pages")) return result;

        var pages = query["pages"] as Dictionary<string, object>;
        if (pages == null) return result;

        foreach (var kv in pages)
        {
            var page = kv.Value as Dictionary<string, object>;
            if (page == null) continue;
            if (!page.ContainsKey("links")) continue;

            var links = page["links"] as List<object>;
            if (links == null) continue;

            foreach (var l in links)
            {
                if (result.Count >= maxCount) break;
                var ld = l as Dictionary<string, object>;
                if (ld != null && ld.ContainsKey("title"))
                {
                    var t = ld["title"] as string;
                    if (!string.IsNullOrEmpty(t)) result.Add(t);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Download a Texture2D from a URL (png/jpg).
    /// </summary>

    public IEnumerator DownloadTexture(string url, Action<Texture2D> onDone, Action<string> onError)
    {
        Debug.Log("DownloadTexture");
        using (var req = UnityWebRequestTexture.GetTexture(url))
        {
            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                onError?.Invoke(req.error);
            }
            else
            {
                var tex = DownloadHandlerTexture.GetContent(req);
                onDone?.Invoke(tex);
            }
        }
    }
}

//unsuccessful:
// BacktrackToPreviousDoor
// backtracking
// UpdateBacktrackAvailability
// SetBacktrackDoorFromSnapshot
// ShowArticleSnapshot
// ApplyTexturesToWalls
// SetRendererTexture 6
// ClearDoorPreviews
// PrefetchDoorTarget 2
// SetBacktrackDoorFromSnapshot

// successful
// BacktrackToPreviousDoor
// backtracking
// UpdateBacktrackAvailability
// SetBacktrackDoorFromSnapshot
// ShowArticleSnapshot
// ApplyTexturesToWalls
// SetRendererTexture 6
// ClearDoorPreviews
// PrefetchDoorTarget 2
// SetBacktrackDoorFromSnapshot