using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HallwayManager : MonoBehaviour
{
    [Header("Player Respawn")]
    public Transform player;
    public Transform spawnPoint;

    [Header("Wall Surfaces (6 in order)")]
    public Renderer[] wallRenderers;

    [Header("Backtrack Door Preview")]
    public Renderer backtrackDoorRenderer; // assign your “backtrack door” mesh here

    [Header("Starting Article Title")]
    public string startArticleTitle = "Virtual reality";

    [Header("Starting Page(s)")]
    [Tooltip("If not empty and 'Pick Random On Start' is true, one of these will be chosen at startup.")]
    public List<string> startingArticleTitles = new List<string>();

    [Tooltip("If true and the list above has items, startup will pick a random page from the list.")]
    public bool pickRandomFromListOnStart = true;

    [Header("Service")]
    public WikiImageService wikiServicePrefab;

    [Header("Door Previews (assign existing MeshRenderers)")]
    public Renderer leftDoorPreviewRenderer;
    public Renderer rightDoorPreviewRenderer;

    [Header("Backtracking")]
    [Tooltip("Where to place the player when backtracking to the left/right door side of the previous hallway.")]
    public Transform leftDoorSpawn;
    public Transform rightDoorSpawn;

    [Header("Backtracking Trigger(s)")]
    public BacktrackTrigger backtrackTrigger; // entrance backtrack volume

    [Header("Startup")]
    public bool autoStart = true;

    [Header("Debug")]
    public bool logDebug = false;

    // ---- runtime state ----
    private WikiImageService _service;

    // texture cache (url -> texture) for downloads
    private readonly Dictionary<string, Texture2D> _textureCache =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    // visited set (still used to avoid repeats only when we DON'T have options cached)
    private readonly HashSet<string> _visited =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private List<DoorTarget> _currentDoorTargets = new List<DoorTarget>(2);
    private string currentArticleTitle;

    // What is currently shown on walls (in-scene)
    private List<Texture2D> _currentWallTextures = new List<Texture2D>(6);
    private List<string> _currentImageUrls = null;

    // ---------- DETERMINISTIC PER-ARTICLE CACHES ----------
    // For a given article (normalized title), remember the exact 6 images and the exact two options (left/right).
    private readonly Dictionary<string, List<string>> _imageUrlsCache =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<Texture2D>> _texturesCache =
        new Dictionary<string, List<Texture2D>>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DoorTarget[]> _optionsCache =
        new Dictionary<string, DoorTarget[]>(StringComparer.OrdinalIgnoreCase);

    // --- content backtracking ---
    [Serializable]
    private class ArticleSnapshot
    {
        public string title;
        public List<string> imageUrls;   // fallback if textures were not captured
        public List<Texture2D> textures; // exact textures on walls
        public int returnDoorIndex;      // 0 = left, 1 = right
        public DoorTarget[] options;     // exact left/right door options at that time
    }
    private readonly LinkedList<ArticleSnapshot> _history = new LinkedList<ArticleSnapshot>();

    // door target record
    [Serializable]
    private class DoorTarget
    {
        public string title;
        public List<string> imageUrls;            // prevalidated URLs (≥ wall count)
        public List<Texture2D> preloadedTextures; // populated by PrefetchDoorTarget (for previews & instant walls if reused)
    }

    // ======== NEW: Door material instances & shader IDs (build-safe) ========
    private Material _leftDoorMat, _rightDoorMat, _backtrackDoorMat;

    private static readonly int ID_MainTex = Shader.PropertyToID("_MainTex");
    private static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int ID_ColorMap = Shader.PropertyToID("_BaseColorMap"); // HDRP compat
    private static readonly int ID_MainTex_ST = Shader.PropertyToID("_MainTex_ST");
    private static readonly int ID_BaseMap_ST = Shader.PropertyToID("_BaseMap_ST");

    // ------------------------------- Unity lifecycle -------------------------------

    private void Awake()
    {
        if (wallRenderers == null || wallRenderers.Length == 0)
            if (logDebug) Debug.LogWarning("HallwayManager: No wall renderers assigned.");

        _service = FindObjectOfType<WikiImageService>();
        if (_service == null && wikiServicePrefab != null)
        {
            _service = Instantiate(wikiServicePrefab);
            _service.name = "WikiImageService (spawned)";
        }
        else if (_service == null)
        {
            _service = new GameObject("WikiImageService").AddComponent<WikiImageService>();
        }

        // Build-safe: give door renderers unique material instances
        if (leftDoorPreviewRenderer)
        {
            _leftDoorMat = new Material(leftDoorPreviewRenderer.sharedMaterial);
            leftDoorPreviewRenderer.material = _leftDoorMat;
        }
        if (rightDoorPreviewRenderer)
        {
            _rightDoorMat = new Material(rightDoorPreviewRenderer.sharedMaterial);
            rightDoorPreviewRenderer.material = _rightDoorMat;
        }
        if (backtrackDoorRenderer)
        {
            _backtrackDoorMat = new Material(backtrackDoorRenderer.sharedMaterial);
            backtrackDoorRenderer.material = _backtrackDoorMat;
        }

        // enforce initial backtrack state
        UpdateBacktrackAvailability();
    }

    private void Start()
    {
        if (!autoStart) return;

        // Pick from list (random if enabled) or fall back to startArticleTitle
        var firstTitle = ChooseInitialArticleTitle();

        StopAllCoroutines();
        StartCoroutine(LoadArticleRoutine(firstTitle, presetImageUrls: null));
    }

    // ------------------------------- Public API -------------------------------

    /// <summary>
    /// Called by DoorTrigger when the player enters a door trigger (0 = left, 1 = right).
    /// </summary>
    public void RespawnAndGoToDoor(int doorIndex)
    {
        if (_currentDoorTargets == null || _currentDoorTargets.Count < 2)
        {
            if (logDebug) Debug.Log("No valid door targets at the moment.");
            return;
        }

        doorIndex = Mathf.Clamp(doorIndex, 0, 1);
        var chosen = _currentDoorTargets[doorIndex];

        // snapshot current hallway before leaving it, recording the door side we used (+ options)
        PushHistorySnapshot(doorIndex);
        UpdateBacktrackDoorFromDoorTarget(chosen);

        RespawnPlayer();

        StopAllCoroutines();
        StartCoroutine(LoadArticleFromDoorTarget(chosen));
    }

    /// <summary>
    /// Trigger-driven content backtrack: restore the previous hallway's content and previews,
    /// and place the player at the corresponding door spawn (left/right) they originally used.
    /// </summary>
    /// Note for future backtrack fixing:
    /// unsuccessful backtrack:
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

    // successful backtrack
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
    public void BacktrackToPreviousDoor()
    {
        if (_history.Count == 0)
        {
            if (logDebug) Debug.Log("Backtrack requested but no history available.");
            return;
        }

        if (logDebug) Debug.Log("backtracking");

        var snap = _history.Last.Value;

        _history.RemoveLast();
        UpdateBacktrackAvailability();
        // Show the snapshot's texture on the backtrack door right away
        SetBacktrackDoorFromSnapshot(snap);

        // Teleport to the door side we originally used to LEAVE that snapshot
        Transform targetSpawn = (snap.returnDoorIndex == 0) ? leftDoorSpawn : rightDoorSpawn;

        if (player && targetSpawn)
        {
            player.SetPositionAndRotation(targetSpawn.position, targetSpawn.rotation);

            // recenter look
            var look = player.GetComponentInChildren<FirstPersonLook>(true);

            if (look) look.RecenterToCurrent();
        }

        StopAllCoroutines();
        StartCoroutine(ShowArticleSnapshot(snap));
    }

    private void SetBacktrackDoorFromSnapshot(ArticleSnapshot snap)
    {
        if (!_backtrackDoorMat || snap == null)
        {
            ClearBacktrackDoorPreview();
            return;
        }

        // Prefer an exact texture captured when we left this hallway
        if (snap.textures != null)
        {
            for (int i = 0; i < snap.textures.Count; i++)
            {
                var tex = snap.textures[i];
                if (tex != null)
                {
                    SetBacktrackDoorTextureMirrored(tex);
                    return;
                }
            }
        }

        // Fallback: first available URL (use cache if present, else download)
        if (snap.imageUrls != null)
        {
            foreach (var url in snap.imageUrls)
            {
                if (string.IsNullOrEmpty(url)) continue;

                if (_textureCache.TryGetValue(url, out var cached) && cached)
                {
                    SetBacktrackDoorTextureMirrored(cached);
                    return;
                }

                // async: fetch first valid and set once ready
                StartCoroutine(_service.DownloadTexture(url,
                    onDone: tex =>
                    {
                        if (tex)
                        {
                            _textureCache[url] = tex;
                            SetBacktrackDoorTextureMirrored(tex);
                        }
                    },
                    onError: _ => { /* ignore and try next */ }));
                return; // set it in the callback
            }
        }

        ClearBacktrackDoorPreview();
    }

    /// <summary>
    /// External entry point for start-prompt flow. Clears history/visited and starts fresh.
    /// </summary>
    public void BeginExperience(string title)
    {
        // If caller passed null/empty, choose using the same logic as startup
        if (string.IsNullOrWhiteSpace(title))
            title = ChooseInitialArticleTitle();

        _currentDoorTargets.Clear();
        ClearDoorPreviews();
        currentArticleTitle = null;
        _visited.Clear();
        _history.Clear();
        ClearBacktrackDoorPreview();

        // Clear per-article caches so a new run can re-randomize
        _imageUrlsCache.Clear();
        _texturesCache.Clear();
        _optionsCache.Clear();

        UpdateBacktrackAvailability();

        startArticleTitle = title.Trim();
        autoStart = true;

        StopAllCoroutines();
        StartCoroutine(LoadArticleRoutine(startArticleTitle, presetImageUrls: null));
    }

    // ------------------------------- Core flow -------------------------------


    private IEnumerator LoadArticleRoutine(string title, List<string> presetImageUrls)
    {
        currentArticleTitle = title;
        var norm = NormalizeTitle(title);
        _visited.Add(norm);
        if (logDebug) Debug.Log($"Loading article: {currentArticleTitle}");

        // 1) Apply images for current article
        List<string> imageUrls = presetImageUrls;

        if (_texturesCache.TryGetValue(norm, out var cachedTex) && cachedTex != null && cachedTex.Count > 0)
        {
            _currentImageUrls = _imageUrlsCache.ContainsKey(norm) ? new List<string>(_imageUrlsCache[norm]) : null;
            ApplyTexturesToWalls(cachedTex);
        }
        else if (imageUrls == null && _imageUrlsCache.TryGetValue(norm, out var cachedUrls) && cachedUrls != null)
        {
            _currentImageUrls = new List<string>(cachedUrls);
            yield return StartCoroutine(ApplyImagesFromUrls(cachedUrls));
        }
        else
        {
            if (imageUrls == null)
            {
                string imgErr = null;
                yield return StartCoroutine(_service.GetImageUrls(title, Mathf.Max(6, wallRenderers.Length),
                    onDone: (urls) => imageUrls = urls,
                    onError: (err) => imgErr = err));

                if (!string.IsNullOrEmpty(imgErr))
                {
                    if (logDebug) Debug.LogWarning("Image fetch failed: " + imgErr);
                    ClearWalls();
                    _currentImageUrls = null;
                }
                else
                {
                    _currentImageUrls = (imageUrls != null) ? new List<string>(imageUrls) : null;
                    yield return StartCoroutine(ApplyImagesFromUrls(imageUrls));
                }
            }
            else
            {
                _currentImageUrls = new List<string>(imageUrls);
                yield return StartCoroutine(ApplyImagesFromUrls(imageUrls));
            }
        }

        // Update caches
        _imageUrlsCache[norm] = _currentImageUrls != null ? new List<string>(_currentImageUrls) : null;
        _texturesCache[norm] = _currentWallTextures != null ? new List<Texture2D>(_currentWallTextures) : null;

        // 2) Door options
        ClearDoorPreviews();
        _currentDoorTargets.Clear();

        if (!RestoreDoorTargetsFor(norm))
        {
            yield return StartCoroutine(ChooseDoorTargets(title, 2)); // generates random set once
            _optionsCache[norm] = CloneDoorTargets(_currentDoorTargets); // freeze these for this article
        }

        // 3) Prefetch & show previews (from the frozen _currentDoorTargets)
        SetPreviewsForCurrentDoorTargets();
    }

    private IEnumerator LoadArticleFromDoorTarget(DoorTarget chosen)
    {
        if (chosen == null) yield break;

        currentArticleTitle = chosen.title;
        var norm = NormalizeTitle(chosen.title);
        _visited.Add(norm);
        if (logDebug) Debug.Log($"Chosen door → {currentArticleTitle}");

        // Try preloaded textures first
        var usable = new List<Texture2D>();
        if (chosen.preloadedTextures != null)
        {
            foreach (var t in chosen.preloadedTextures)
                if (t != null) usable.Add(t);
        }

        if (usable.Count >= wallRenderers.Length)
        {
            ApplyTexturesToWalls(usable);
            _currentImageUrls = chosen.imageUrls != null ? new List<string>(chosen.imageUrls) : null;
        }
        else
        {
            // Top-up via URLs (or refetch)
            List<string> urls = chosen.imageUrls;
            if (urls == null || urls.Count == 0)
            {
                string err = null;
                yield return StartCoroutine(_service.GetImageUrls(chosen.title, Mathf.Max(6, wallRenderers.Length),
                    onDone: (u) => urls = u,
                    onError: (e) => err = e));
                if (!string.IsNullOrEmpty(err) && logDebug) Debug.LogWarning("Top-up URL fetch failed: " + err);
            }

            _currentImageUrls = (urls != null) ? new List<string>(urls) : null;
            if (urls != null) yield return StartCoroutine(ApplyImagesFromUrls(urls));
            else ClearWalls();
        }

        // Update caches
        _imageUrlsCache[norm] = _currentImageUrls != null ? new List<string>(_currentImageUrls) : null;
        _texturesCache[norm] = _currentWallTextures != null ? new List<Texture2D>(_currentWallTextures) : null;

        // Door options for the NEW article
        ClearDoorPreviews();
        _currentDoorTargets.Clear();

        if (!RestoreDoorTargetsFor(norm))
        {
            yield return StartCoroutine(ChooseDoorTargets(currentArticleTitle, 2));
            _optionsCache[norm] = CloneDoorTargets(_currentDoorTargets);
        }

        SetPreviewsForCurrentDoorTargets();
    }

    private IEnumerator ShowArticleSnapshot(ArticleSnapshot snap)
    {
        currentArticleTitle = snap.title;
        var norm = NormalizeTitle(snap.title);

        // Restore walls exactly as they were
        if (snap.textures != null && snap.textures.Count > 0)
        {
            ApplyTexturesToWalls(snap.textures);
            _currentImageUrls = (snap.imageUrls != null) ? new List<string>(snap.imageUrls) : null;
        }
        else if (snap.imageUrls != null && snap.imageUrls.Count > 0)
        {
            _currentImageUrls = new List<string>(snap.imageUrls);
            yield return StartCoroutine(ApplyImagesFromUrls(snap.imageUrls));
        }
        else
        {
            List<string> urls = null; string err = null;
            yield return StartCoroutine(_service.GetImageUrls(snap.title, Mathf.Max(6, wallRenderers.Length),
                onDone: u => urls = u,
                onError: e => err = e));
            _currentImageUrls = (urls != null) ? new List<string>(urls) : null;
            if (!string.IsNullOrEmpty(err) || urls == null) ClearWalls();
            else yield return StartCoroutine(ApplyImagesFromUrls(urls));
        }

        // Seed per-article caches from the snapshot
        _imageUrlsCache[norm] = _currentImageUrls != null ? new List<string>(_currentImageUrls) : null;
        _texturesCache[norm] = _currentWallTextures != null ? new List<Texture2D>(_currentWallTextures) : null;

        // Restore the same two door options
        ClearDoorPreviews();
        _currentDoorTargets.Clear();

        bool restored = false;
        if (snap.options != null && snap.options.Length >= 2)
        {
            _currentDoorTargets = RehydrateDoorTargets(snap.options);
            _optionsCache[norm] = CloneDoorTargets(_currentDoorTargets); // keep cache aligned
            restored = true;
        }

        if (!restored && !RestoreDoorTargetsFor(norm))
        {
            yield return StartCoroutine(ChooseDoorTargets(currentArticleTitle, 2));
            _optionsCache[norm] = CloneDoorTargets(_currentDoorTargets);
        }

        SetPreviewsForCurrentDoorTargets();
        SetBacktrackDoorFromSnapshot(snap);
    }

    // ------------------------------- Target selection -------------------------------
    private IEnumerator ChooseDoorTargets(string baseTitle, int requiredDoorTargets)
    {
        const int maxRounds = 4;
        int fetchCount = 300; // fetch more links per page
        int tested = 0;
        int maxCandidatesToTest = 400;
        var testedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int round = 0; round < maxRounds && _currentDoorTargets.Count < requiredDoorTargets; round++)
        {
            List<string> links = null;
            string linkErr = null;

            // Fetch links from the page
            yield return StartCoroutine(_service.GetLinkedArticleTitles(baseTitle, Mathf.Max(2, fetchCount),
                onDone: list => links = list,
                onError: err => linkErr = err));

            if (!string.IsNullOrEmpty(linkErr) || links == null || links.Count == 0)
                break;

            // Shuffle links to randomize order for door assignment
            Shuffle(links);

            foreach (var candidateRaw in links)
            {
                if (_currentDoorTargets.Count >= requiredDoorTargets) break;

                var candidate = NormalizeTitle(candidateRaw);

                // ⛔ SAFEGUARD — skip empty or whitespace titles
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                // Avoid repeats
                if (_visited.Contains(candidate) || testedTitles.Contains(candidate)) continue;
                if (string.Equals(candidate, NormalizeTitle(baseTitle), StringComparison.OrdinalIgnoreCase)) continue;

                testedTitles.Add(candidate);
                tested++;
                if (tested > maxCandidatesToTest) break;

                // Require at least wall count images
                List<string> urls = null;
                string imgErr = null;
                yield return StartCoroutine(_service.GetImageUrls(candidate, Mathf.Max(6, wallRenderers.Length),
                    onDone: u => urls = u,
                    onError: e => imgErr = e));

                if (!string.IsNullOrEmpty(imgErr)) continue;
                if (urls == null || urls.Count < wallRenderers.Length) continue;

                _currentDoorTargets.Add(new DoorTarget { title = candidate, imageUrls = urls });
            }

            if (_currentDoorTargets.Count >= requiredDoorTargets || tested >= maxCandidatesToTest) break;
        }

        // Soft fallback
        while (_currentDoorTargets.Count < requiredDoorTargets)
        {
            if (_currentDoorTargets.Count > 0)
                _currentDoorTargets.Add(CloneDoorTarget(_currentDoorTargets[0]));
            else
            {
                _currentDoorTargets.Add(new DoorTarget { title = baseTitle, imageUrls = new List<string>() });
                break;
            }
        }
    }

    /// <summary>
    /// Fetches images for a single candidate and adds it to _currentDoorTargets if valid.
    /// </summary>
    private IEnumerator FetchCandidateDoorTarget(string candidate, int requiredImages)
    {
        List<string> urls = null;
        string imgErr = null;

        yield return StartCoroutine(_service.GetImageUrls(candidate, Mathf.Max(6, requiredImages),
            onDone: u => urls = u,
            onError: e => imgErr = e));

        if (!string.IsNullOrEmpty(imgErr)) yield break;
        if (urls == null || urls.Count < requiredImages) yield break;

        _currentDoorTargets.Add(new DoorTarget
        {
            title = candidate,
            imageUrls = urls
        });
    }

    // ------------------------------- Prefetch + previews -------------------------------

    private IEnumerator PrefetchDoorTarget(DoorTarget t, Renderer previewRenderer)
    {
        if (t == null || t.imageUrls == null || t.imageUrls.Count == 0)
        {
            // Clear using door material instances
            if (previewRenderer == leftDoorPreviewRenderer && _leftDoorMat) { SetDoorMatTexture(_leftDoorMat, null); SetDoorMatTiling(_leftDoorMat, Vector2.one, Vector2.zero); }
            else if (previewRenderer == rightDoorPreviewRenderer && _rightDoorMat) { SetDoorMatTexture(_rightDoorMat, null); SetDoorMatTiling(_rightDoorMat, Vector2.one, Vector2.zero); }
            else if (previewRenderer != null) TrySetTextureOnAnyPipeline(previewRenderer, null);
            yield break;
        }

        if (t.preloadedTextures == null)
            t.preloadedTextures = new List<Texture2D>(t.imageUrls.Count);

        while (t.preloadedTextures.Count < t.imageUrls.Count) t.preloadedTextures.Add(null);

        bool previewSet = false;

        for (int i = 0; i < t.imageUrls.Count; i++)
        {
            string url = t.imageUrls[i];
            if (string.IsNullOrEmpty(url)) continue;

            if (_textureCache.TryGetValue(url, out var cached))
            {
                t.preloadedTextures[i] = cached;
            }
            else
            {
                Texture2D tex = null;
                string err = null;
                yield return StartCoroutine(_service.DownloadTexture(url,
                    onDone: d => tex = d,
                    onError: e => err = e));

                if (tex != null)
                {
                    _textureCache[url] = tex;
                    t.preloadedTextures[i] = tex;
                }
            }

            if (!previewSet && previewRenderer != null && t.preloadedTextures[i] != null)
            {
                // Use door material instances (build-safe)
                if (previewRenderer == leftDoorPreviewRenderer && _leftDoorMat)
                {
                    SetDoorMatTexture(_leftDoorMat, t.preloadedTextures[i]);
                    SetDoorMatTiling(_leftDoorMat, Vector2.one, Vector2.zero);
                }
                else if (previewRenderer == rightDoorPreviewRenderer && _rightDoorMat)
                {
                    SetDoorMatTexture(_rightDoorMat, t.preloadedTextures[i]);
                    SetDoorMatTiling(_rightDoorMat, Vector2.one, Vector2.zero);
                }
                else
                {
                    // Fallback for any other renderer
                    TrySetTextureOnAnyPipeline(previewRenderer, t.preloadedTextures[i]);
                }
                previewSet = true;
            }
        }

        if (!previewSet && previewRenderer != null)
        {
            if (previewRenderer == leftDoorPreviewRenderer && _leftDoorMat) SetDoorMatTexture(_leftDoorMat, null);
            else if (previewRenderer == rightDoorPreviewRenderer && _rightDoorMat) SetDoorMatTexture(_rightDoorMat, null);
            else TrySetTextureOnAnyPipeline(previewRenderer, null);
        }
    }

    private void ClearDoorPreviews()
    {
        if (_leftDoorMat) { SetDoorMatTexture(_leftDoorMat, null); SetDoorMatTiling(_leftDoorMat, Vector2.one, Vector2.zero); }
        if (_rightDoorMat) { SetDoorMatTexture(_rightDoorMat, null); SetDoorMatTiling(_rightDoorMat, Vector2.one, Vector2.zero); }
    }

    // ------------------------------- Apply current walls -------------------------------

    private IEnumerator ApplyImagesFromUrls(List<string> urls)
    {
        if (urls == null || urls.Count == 0)
        {
            ClearWalls();
            yield break;
        }

        var textures = new List<Texture2D>(wallRenderers.Length);

        for (int i = 0; i < urls.Count && textures.Count < wallRenderers.Length; i++)
        {
            var url = urls[i];
            if (string.IsNullOrEmpty(url)) continue;

            if (_textureCache.TryGetValue(url, out var cached))
            {
                textures.Add(cached);
                continue;
            }

            Texture2D tex = null;
            string err = null;
            yield return StartCoroutine(_service.DownloadTexture(url,
                onDone: d => tex = d,
                onError: e => err = e));

            if (tex != null)
            {
                _textureCache[url] = tex;
                textures.Add(tex);
            }
        }

        ApplyTexturesToWalls(textures);
    }

    private void ApplyTexturesToWalls(List<Texture2D> textures)
    {
        for (int i = 0; i < wallRenderers.Length; i++)
        {
            var tex = (textures != null && i < textures.Count) ? textures[i] : null;
            SetRendererTexture(wallRenderers[i], tex);
        }

        // snapshot currently shown textures for instant backtracking
        _currentWallTextures = new List<Texture2D>(wallRenderers.Length);
        for (int i = 0; i < wallRenderers.Length; i++)
        {
            var tex = (textures != null && i < textures.Count) ? textures[i] : null;
            _currentWallTextures.Add(tex);
        }
    }

    private void SetRendererTexture(Renderer r, Texture tex)
    {
        if (!r) return;
        var m = r.material;
        if (m == null) return;

        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", tex);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);

        m.mainTexture = tex;
    }

    private static void TrySetTextureOnAnyPipeline(Renderer r, Texture tex)
    {
        if (!r) return;
        var m = r.material;
        if (!m) return;
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", tex);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        r.material.mainTexture = tex;
    }

    private static void TrySetColorOnAnyPipeline(Renderer r, Color c)
    {
        if (!r) return;
        var m = r.material;
        if (!m) return;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    private void ClearWalls()
    {
        _currentWallTextures = new List<Texture2D>(wallRenderers.Length);
        foreach (var r in wallRenderers)
        {
            SetRendererTexture(r, null);
            _currentWallTextures.Add(null);
        }
    }

    private void RespawnPlayer()
    {
        if (!player || !spawnPoint) return;

        player.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

        // simple recenter
        var look = player.GetComponentInChildren<FirstPersonLook>(true);
        if (look) look.RecenterToCurrent();
    }

    // ------------------------------- History helpers -------------------------------

    private void PushHistorySnapshot(int returnDoorIndex)
    {
        if (string.IsNullOrEmpty(currentArticleTitle)) return;

        var norm = NormalizeTitle(currentArticleTitle);

        var snap = new ArticleSnapshot
        {
            title = currentArticleTitle,
            imageUrls = (_currentImageUrls != null) ? new List<string>(_currentImageUrls) : null,
            textures = (_currentWallTextures != null) ? new List<Texture2D>(_currentWallTextures) : null,
            returnDoorIndex = Mathf.Clamp(returnDoorIndex, 0, 1),
            options = _currentDoorTargets != null && _currentDoorTargets.Count >= 2
                      ? CloneDoorTargets(_currentDoorTargets)
                      : null
        };

        _history.AddLast(snap);
        UpdateBacktrackAvailability();

        // Also seed the per-article caches so revisits to this article are deterministic
        if (snap.imageUrls != null) _imageUrlsCache[norm] = new List<string>(snap.imageUrls);
        if (snap.textures != null) _texturesCache[norm] = new List<Texture2D>(snap.textures);
        if (snap.options != null) _optionsCache[norm] = CloneDoorTargetsArray(snap.options);
    }

    private void UpdateBacktrackAvailability()
    {
        bool available = _history.Count > 0;
        if (backtrackTrigger) backtrackTrigger.SetBacktrackAvailable(available);
        if (!available) ClearBacktrackDoorPreview(); // keep first hallway blank
    }

    // ------------------------------- Utils -------------------------------

    private static void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static string NormalizeTitle(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        t = t.Trim().Replace('_', ' ');
        return t;
    }

    private bool RestoreDoorTargetsFor(string normalizedTitle)
    {
        if (_optionsCache.TryGetValue(normalizedTitle, out var frozen) && frozen != null && frozen.Length >= 2)
        {
            _currentDoorTargets = RehydrateDoorTargets(frozen);
            return true;
        }
        return false;
    }

    private void SetPreviewsForCurrentDoorTargets()
    {
        if (_currentDoorTargets.Count >= 1)
            StartCoroutine(PrefetchDoorTarget(_currentDoorTargets[0], leftDoorPreviewRenderer));
        if (_currentDoorTargets.Count >= 2)
            StartCoroutine(PrefetchDoorTarget(_currentDoorTargets[1], rightDoorPreviewRenderer));
    }

    private DoorTarget CloneDoorTarget(DoorTarget src)
    {
        return new DoorTarget
        {
            title = src.title,
            imageUrls = src.imageUrls != null ? new List<string>(src.imageUrls) : null,
            preloadedTextures = src.preloadedTextures != null ? new List<Texture2D>(src.preloadedTextures) : null
        };
    }

    private DoorTarget[] CloneDoorTargets(List<DoorTarget> list)
    {
        var arr = new DoorTarget[Mathf.Min(2, list.Count)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = CloneDoorTarget(list[i]);
        return arr;
    }

    private DoorTarget[] CloneDoorTargetsArray(DoorTarget[] arrIn)
    {
        var arr = new DoorTarget[Mathf.Min(2, arrIn.Length)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = CloneDoorTarget(arrIn[i]);
        return arr;
    }

    private List<DoorTarget> RehydrateDoorTargets(DoorTarget[] arr)
    {
        var list = new List<DoorTarget>(2);
        for (int i = 0; i < Mathf.Min(2, arr.Length); i++)
            list.Add(CloneDoorTarget(arr[i]));
        return list;
    }

    // --- backtrack door helpers (build-safe with material instance) ---

    private void ClearBacktrackDoorPreview()
    {
        if (_backtrackDoorMat)
        {
            SetDoorMatTexture(_backtrackDoorMat, null);
            SetDoorMatTiling(_backtrackDoorMat, Vector2.one, Vector2.zero);
        }
        else if (backtrackDoorRenderer)
        {
            TrySetTextureOnAnyPipeline(backtrackDoorRenderer, null);
            ResetTextureTiling(backtrackDoorRenderer);
        }
    }

    private void ResetTextureTiling(Renderer r)
    {
        if (!r) return;
        var m = r.material; if (!m) return;

        if (m.HasProperty("_BaseMap"))
        {
            m.SetTextureScale("_BaseMap", Vector2.one);
            m.SetTextureOffset("_BaseMap", Vector2.zero);
        }
        if (m.HasProperty("_MainTex"))
        {
            m.SetTextureScale("_MainTex", Vector2.one);
            m.SetTextureOffset("_MainTex", Vector2.zero);
        }
        m.mainTextureScale = Vector2.one;
        m.mainTextureOffset = Vector2.zero;
    }

    private void SetBacktrackDoorTextureMirrored(Texture tex)
    {
        if (!_backtrackDoorMat)
        {
            // Fallback to old path if no instance for some reason
            ClearBacktrackDoorPreview();
            if (tex) TrySetTextureOnAnyPipeline(backtrackDoorRenderer, tex);
            return;
        }

        if (!tex)
        {
            SetDoorMatTexture(_backtrackDoorMat, null);
            SetDoorMatTiling(_backtrackDoorMat, Vector2.one, Vector2.zero);
            return;
        }

        SetDoorMatTexture(_backtrackDoorMat, tex);

        // Mirroring via tiling/offset
        bool mirror = true; // set false once to test if mirroring was the culprit
        if (mirror)
            SetDoorMatTiling(_backtrackDoorMat, new Vector2(-1f, 1f), new Vector2(1f, 0f));
        else
            SetDoorMatTiling(_backtrackDoorMat, Vector2.one, Vector2.zero);
    }

    /// <summary>
    /// Show (mirrored) the best available texture for a given door target.
    /// Prefers preloadedTextures, then cached textures by URL, then downloads the first available.
    /// </summary>
    private void UpdateBacktrackDoorFromDoorTarget(DoorTarget t)
    {
        if (!_backtrackDoorMat || t == null) return;

        // 1) Prefer a preloaded texture
        if (t.preloadedTextures != null)
        {
            for (int i = 0; i < t.preloadedTextures.Count; i++)
            {
                var tex = t.preloadedTextures[i];
                if (tex)
                {
                    SetBacktrackDoorTextureMirrored(tex);
                    return;
                }
            }
        }

        // 2) Try the cache by URL
        if (t.imageUrls != null)
        {
            foreach (var url in t.imageUrls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (_textureCache.TryGetValue(url, out var cached) && cached)
                {
                    SetBacktrackDoorTextureMirrored(cached);
                    return;
                }
            }
        }

        // 3) Asynchronous fallback
        if (t.imageUrls != null && t.imageUrls.Count > 0)
            StartCoroutine(PrefetchBacktrackTexture(t));
    }

    private IEnumerator PrefetchBacktrackTexture(DoorTarget t)
    {
        if (t == null || t.imageUrls == null) yield break;

        foreach (var url in t.imageUrls)
        {
            if (string.IsNullOrEmpty(url)) continue;

            if (_textureCache.TryGetValue(url, out var cached) && cached)
            {
                SetBacktrackDoorTextureMirrored(cached);
                yield break;
            }

            Texture2D tex = null;
            string err = null;
            yield return StartCoroutine(_service.DownloadTexture(url,
                onDone: d => tex = d,
                onError: e => err = e));

            if (tex)
            {
                _textureCache[url] = tex;
                SetBacktrackDoorTextureMirrored(tex);
                yield break;
            }
        }
    }

    // ------------------------------- New helper for starting title -------------------------------

    private string ChooseInitialArticleTitle()
    {
        if (startingArticleTitles != null && startingArticleTitles.Count > 0)
        {
            var pool = new List<string>();
            foreach (var t in startingArticleTitles)
            {
                if (!string.IsNullOrWhiteSpace(t))
                    pool.Add(t.Trim());
            }

            if (pool.Count > 0 && pickRandomFromListOnStart)
            {
                int idx = UnityEngine.Random.Range(0, pool.Count);
                return pool[idx];
            }

            if (pool.Count > 0) return pool[0];
        }

        if (string.IsNullOrWhiteSpace(startArticleTitle))
            return "Virtual reality";

        return startArticleTitle.Trim();
    }

    // ======== Build-safe material helpers for doors ========

    private static void SetDoorMatTexture(Material m, Texture tex)
    {
        if (!m) return;
        if (m.HasProperty(ID_BaseMap)) m.SetTexture(ID_BaseMap, tex);
        if (m.HasProperty(ID_ColorMap)) m.SetTexture(ID_ColorMap, tex);
        if (m.HasProperty(ID_MainTex)) m.SetTexture(ID_MainTex, tex);
        m.mainTexture = tex; // legacy safety
    }

    private static void SetDoorMatTiling(Material m, Vector2 tiling, Vector2 offset)
    {
        if (!m) return;

        if (m.HasProperty("_BaseMap"))
        {
            m.SetTextureScale("_BaseMap", tiling);
            m.SetTextureOffset("_BaseMap", offset);
        }
        if (m.HasProperty("_MainTex"))
        {
            m.SetTextureScale("_MainTex", tiling);
            m.SetTextureOffset("_MainTex", offset);
        }

        // Also pack into _ST vectors used by SRP Batcher
        if (m.HasProperty(ID_BaseMap_ST)) m.SetVector(ID_BaseMap_ST, new Vector4(tiling.x, tiling.y, offset.x, offset.y));
        if (m.HasProperty(ID_MainTex_ST)) m.SetVector(ID_MainTex_ST, new Vector4(tiling.x, tiling.y, offset.x, offset.y));

        m.mainTextureScale = tiling;   // legacy
        m.mainTextureOffset = offset;   // legacy
    }
}

