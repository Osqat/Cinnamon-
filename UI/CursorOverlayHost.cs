using System.Collections.Generic;
using UnityEngine;

namespace Cinnamon.UI
{
    internal class CursorOverlayHost : MonoBehaviour
    {
        static CursorOverlayHost _instance;

        Transform _cursorRoot;
        Camera _cursorCam;
        int _refsFrame = -1;
        int _requestedFrame = -999;
        bool _loggedOnce;

        // State.
        bool _active;
        SpriteRenderer _hiddenSr;
        Color _hiddenSrOriginalColor;
        bool _savedCursorVisible;
        Sprite _activeSprite;
        Texture2D _activeTex;

        readonly Dictionary<Sprite, Texture2D> _extractedCache = new Dictionary<Sprite, Texture2D>();

        // 1x1 fully-transparent texture used on ExitActive to overwrite any lingering
        // ForceSoftware cursor pixels. Unity 2021.3's Cursor.SetCursor(null, ..., ForceSoftware)
        // does not reliably drop the last set texture — the previously assigned custom cursor
        // remains the OS cursor texture even after SetCursor(null), so a later Cursor.visible =
        // true (e.g. main menu opening) reveals the leftover custom cursor. Writing a 1x1
        // transparent texture before nulling guarantees the visible cursor pixels are empty
        // regardless of whether SetCursor(null) is honoured.
        Texture2D _blankTex;

        public bool DebugLogging { get; set; }

        public static CursorOverlayHost EnsureExists()
        {
            if (_instance != null) return _instance;

            var go = new GameObject("Cinnamon.CursorOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CursorOverlayHost>();
            return _instance;
        }

        public bool IsCursorReady()
        {
            EnsureCursorRefs(forLogPass: false);
            return _cursorRoot != null && _cursorRoot.GetComponentInChildren<SpriteRenderer>(false) != null;
        }

        // Consumer calls this every frame its panel is active. While Request is current
        // the host hands the game's cursor sprite to the OS via Cursor.SetCursor, which
        // draws above the entire Unity window.
        public void Request()
        {
            _requestedFrame = Time.frameCount;
        }

        void LateUpdate()
        {
            bool wantActive = _requestedFrame >= Time.frameCount - 1;

            // If we're Active but the game destroyed our cursor SR (e.g. closing inventory on
            // Escape tears down PickCursor), force-exit so we don't leave a stale OS cursor
            // alongside whatever cursor the game brings back. Unity's overloaded == treats
            // destroyed Object refs as null.
            if (_active && _hiddenSr == null)
            {
                ExitActive();
                return;
            }

            if (wantActive)
                EnterOrUpdateActive();
            else if (_active)
                ExitActive();
        }

        void EnterOrUpdateActive()
        {
            EnsureCursorRefs(forLogPass: DebugLogging && !_loggedOnce);

            // Find the topmost visible SpriteRenderer on the chosen cursor root.
            SpriteRenderer pick = null;
            if (_cursorRoot != null)
            {
                var renderers = _cursorRoot.GetComponentsInChildren<SpriteRenderer>(false);
                int bestOrder = int.MinValue;
                foreach (var sr in renderers)
                {
                    if (sr == null || sr.sprite == null) continue;
                    if (!sr.enabled) continue;
                    if (sr.sortingOrder >= bestOrder) { bestOrder = sr.sortingOrder; pick = sr; }
                }
            }

            if (pick == null)
            {
                if (DebugLogging && !_loggedOnce)
                {
                    _loggedOnce = true;
                    Plugin.Log?.LogInfo("[Cinnamon] activate bail: no usable SpriteRenderer on selected cursor root");
                }
                if (_active) ExitActive();
                return;
            }

            // First transition into Active: save state once.
            if (!_active)
            {
                _hiddenSr = pick;
                _hiddenSrOriginalColor = pick.color;
                _savedCursorVisible = UnityEngine.Cursor.visible;
                _active = true;

                if (DebugLogging)
                {
                    Plugin.Log?.LogInfo($"[Cinnamon] enter Active: sprite='{pick.sprite.name}' tex='{pick.sprite.texture?.name}' pivot={pick.sprite.pivot} rect={pick.sprite.textureRect}");
                }
            }
            else if (_hiddenSr != pick)
            {
                // Cursor renderer changed mid-Active (different player joined, animation
                // swapped onto a different SR, etc.). Restore the old SR's color first,
                // then start hiding the new one. Hotspot/texture are re-evaluated below.
                if (_hiddenSr != null) _hiddenSr.color = _hiddenSrOriginalColor;
                _hiddenSr = pick;
                _hiddenSrOriginalColor = pick.color;
            }

            // Re-apply each frame because the game's followingMouse loop writes back the SR's color.
            var hidden = _hiddenSrOriginalColor; hidden.a = 0f;
            pick.color = hidden;

            if (!UnityEngine.Cursor.visible) UnityEngine.Cursor.visible = true;

            var sp = pick.sprite;
            if (sp == null) return;

            // Target the actual on-screen cursor size, not the atlas-pixel size of the sprite.
            int targetW, targetH;
            ComputeOnScreenSize(pick, out targetW, out targetH);

            var tex = GetOrExtract(pick, targetW, targetH);
            if (tex == null)
            {
                if (DebugLogging && !_loggedOnce)
                {
                    _loggedOnce = true;
                    Plugin.Log?.LogInfo($"[Cinnamon] extract failed: sprite='{sp.name}' tex='{sp.texture?.name}'");
                }
                return;
            }

            if (!ReferenceEquals(sp, _activeSprite) || !ReferenceEquals(tex, _activeTex))
            {
                // The game's cursor sprites use pivots OUTSIDE textureRect (e.g. pivot.y = -7.5)
                // so the in-world sprite renders ABOVE the click point. That math is useless for
                // an OS cursor — Windows hotspots must lie within the texture. Instead, scan
                // the extracted texture from OS-top downward for the first non-transparent
                // pixel; that IS the visible tip of the cursor and is exactly where the user
                // expects the click to register.
                var hotspot = FindTipHotspot(tex);

                // ForceSoftware: bypasses the 32×32 hardware-cursor clamp on Windows and lets
                // Unity render the cursor at full Texture2D size on top of the entire frame
                // (including IMGUI panels).
                UnityEngine.Cursor.SetCursor(tex, hotspot, CursorMode.ForceSoftware);
                _activeSprite = sp;
                _activeTex = tex;

                if (DebugLogging && !_loggedOnce)
                {
                    _loggedOnce = true;
                    var tr = sp.textureRect;
                    Plugin.Log?.LogInfo($"[Cinnamon] SetCursor: sprite='{sp.name}' src={tr.width:F0}x{tr.height:F0} target={tex.width}x{tex.height} pivot={sp.pivot} hotspot={hotspot} flipX={pick.flipX} flipY={pick.flipY} lossy={_cursorRoot.lossyScale} src_tex='{sp.texture?.name}' fmt={sp.texture?.format}");
                }
            }
        }

        // Find the cursor's visible TIP — the pixel sticking out the most in the up-right
        // direction from the cursor's centre of mass. For a character-shaped cursor (UCH uses
        // the snake/raccoon/platypus character art), the topmost-rightmost solid pixel is
        // just the body's silhouette corner; the actual click tip is the snout/extremity
        // poking *out* of the body, which by definition is the solid pixel furthest from the
        // mass centroid along the up-right axis.
        //
        // Algorithm:
        //   1) Compute the centroid of all solid pixels (alpha > threshold).
        //   2) For each solid pixel, score by (dx/w + dy/h) in Unity-Y-up coordinates, where
        //      higher is more "up-right of the centroid". Normalising by w and h makes the
        //      metric shape-agnostic so it works for wide-and-short or tall-and-narrow
        //      sprites equally.
        //   3) Return the highest-scoring pixel as the OS hotspot.
        // Returns coordinates in OS (top-left origin) form.
        Vector2 FindTipHotspot(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            Color32[] pixels;
            try { pixels = tex.GetPixels32(); }
            catch { return new Vector2(w - 1, 0f); }

            const byte alphaThreshold = 128;

            // Centroid pass.
            long sumX = 0, sumY = 0;
            int count = 0;
            for (int unityY = 0; unityY < h; unityY++)
            {
                int row = unityY * w;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[row + x].a > alphaThreshold)
                    {
                        sumX += x;
                        sumY += unityY;
                        count++;
                    }
                }
            }
            if (count == 0) return new Vector2(w - 1, 0f);
            float cx = (float)sumX / count;
            float cy = (float)sumY / count;

            // Furthest-up-right pass. Score is positive when a pixel is right of and above
            // the centroid; the maximum is the protruding tip.
            float bestScore = float.MinValue;
            int bestX = w - 1, bestUnityY = h - 1;
            float invW = 1f / w, invH = 1f / h;
            for (int unityY = 0; unityY < h; unityY++)
            {
                int row = unityY * w;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[row + x].a > alphaThreshold)
                    {
                        float score = (x - cx) * invW + (unityY - cy) * invH;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestUnityY = unityY;
                        }
                    }
                }
            }

            int osY = (h - 1) - bestUnityY;
            return new Vector2(bestX, osY);
        }

        // Project the sprite renderer's world-space bounds through the cursor's camera to get
        // the actual on-screen pixel size — that's the size the game itself is drawing the
        // cursor at, and the size our OS cursor should match.
        void ComputeOnScreenSize(SpriteRenderer sr, out int w, out int h)
        {
            var fallback = sr.sprite.textureRect;
            w = Mathf.Max(1, Mathf.RoundToInt(fallback.width));
            h = Mathf.Max(1, Mathf.RoundToInt(fallback.height));

            var cam = _cursorCam != null ? _cursorCam : Camera.main;
            if (cam == null) return;

            var b = sr.bounds;
            var p0 = cam.WorldToScreenPoint(new Vector3(b.min.x, b.min.y, b.center.z));
            var p1 = cam.WorldToScreenPoint(new Vector3(b.max.x, b.max.y, b.center.z));
            int pw = Mathf.RoundToInt(Mathf.Abs(p1.x - p0.x));
            int ph = Mathf.RoundToInt(Mathf.Abs(p1.y - p0.y));
            if (pw >= 8 && ph >= 8) { w = pw; h = ph; }
        }

        void ExitActive()
        {
            // First, overwrite the ForceSoftware texture with a 1x1 fully-transparent pixel.
            // Unity 2021.3 doesn't reliably clear the software-cursor texture when SetCursor(null)
            // is called in ForceSoftware mode, so the previously set custom cursor stays visible
            // whenever Cursor.visible later flips to true (e.g. main menu opens). Writing a blank
            // texture first guarantees the cursor is visually empty even if SetCursor(null) is
            // a no-op underneath.
            EnsureBlankTex();
            UnityEngine.Cursor.SetCursor(_blankTex, Vector2.zero, CursorMode.ForceSoftware);

            // Then null in both modes to release the software cursor entirely.
            UnityEngine.Cursor.SetCursor(null, Vector2.zero, CursorMode.ForceSoftware);
            UnityEngine.Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);

            if (_hiddenSr != null)
            {
                _hiddenSr.color = _hiddenSrOriginalColor;
                _hiddenSr = null;
            }

            // Force-hide the OS cursor on exit. UCH renders its in-world SpriteRenderer cursor
            // as the "real" cursor and normally keeps the OS cursor hidden. Restoring our
            // captured _savedCursorVisible is unreliable because the game may flip visibility
            // between EnterActive and ExitActive — leaving the Windows default arrow visible
            // alongside the game's sprite cursor. Just hide it; if a real menu later needs the
            // OS cursor, the game's own logic will set Cursor.visible = true.
            UnityEngine.Cursor.visible = false;

            _activeSprite = null;
            _activeTex = null;
            _active = false;

            if (DebugLogging)
                Plugin.Log?.LogInfo("[Cinnamon] exit Active");
        }

        Texture2D GetOrExtract(SpriteRenderer source, int targetW, int targetH)
        {
            var sp = source.sprite;
            if (sp == null) return null;

            int w = Mathf.Max(1, targetW);
            int h = Mathf.Max(1, targetH);

            if (_extractedCache.TryGetValue(sp, out var cached) && cached != null)
            {
                if (cached.width == w && cached.height == h) return cached;
                Destroy(cached);
                _extractedCache.Remove(sp);
            }

            // Render the sprite through Unity's actual sprite pipeline instead of trying to
            // slice it out of the atlas ourselves. We spawn a temporary SpriteRenderer with
            // the same sprite at a far-off coordinate, frame it with an orthographic camera
            // on a dedicated layer (so no real game cameras render it), and capture the
            // camera's RenderTexture. This is bulletproof against atlas padding / packing
            // rotation / SpriteAtlas indirection — whatever Unity normally draws for this
            // sprite is exactly what we get, with nothing else in the frame.
            var tex = RenderSpriteViaCamera(source, w, h);
            if (tex != null) _extractedCache[sp] = tex;
            return tex;
        }

        Texture2D RenderSpriteViaCamera(SpriteRenderer source, int w, int h)
        {
            const int extractLayer = 31; // Unity reserves 0-7 for built-ins; 31 is safe to assume unused.

            GameObject spriteHolder = null;
            GameObject camHolder = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            Texture2D result = null;

            try
            {
                spriteHolder = new GameObject("Cinnamon.CursorExtract.Sprite");
                spriteHolder.hideFlags = HideFlags.HideAndDontSave;
                spriteHolder.layer = extractLayer;
                spriteHolder.transform.position = new Vector3(50000f, 50000f, 0f);
                spriteHolder.transform.rotation = Quaternion.identity;
                spriteHolder.transform.localScale = Vector3.one;

                var sr = spriteHolder.AddComponent<SpriteRenderer>();
                sr.sprite = source.sprite;
                // Use the saved original color — `source.color` is the alpha=0 hidden state we
                // wrote during EnterActive, and rendering that would give us an empty texture.
                sr.color = _hiddenSrOriginalColor;
                sr.flipX = source.flipX;
                sr.flipY = source.flipY;
                sr.sortingOrder = 0;
                // Match the source's material so any UCH-specific cursor shader gets honored
                // (and we don't accidentally apply edge-AA the in-world cursor doesn't have).
                if (source.sharedMaterial != null) sr.sharedMaterial = source.sharedMaterial;

                // Frame the camera tightly to the sprite's bounds.
                var b = sr.bounds;
                float halfH = Mathf.Max(0.001f, b.size.y * 0.5f);
                float aspect = b.size.x > 0f ? b.size.x / b.size.y : 1f;

                camHolder = new GameObject("Cinnamon.CursorExtract.Cam");
                camHolder.hideFlags = HideFlags.HideAndDontSave;
                camHolder.transform.position = new Vector3(b.center.x, b.center.y, b.center.z - 1f);
                camHolder.transform.rotation = Quaternion.identity;

                var cam = camHolder.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = halfH;
                cam.aspect = aspect;
                cam.cullingMask = 1 << extractLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.enabled = false;
                cam.allowHDR = false;
                cam.allowMSAA = false;

                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                result = new Texture2D(w, h, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                result.Apply();
                result.hideFlags = HideFlags.HideAndDontSave;

                // Unity's Sprites/Default shader pre-multiplies RGB by alpha in the fragment
                // (it blends with `One OneMinusSrcAlpha`). The RT therefore holds (RGB·α, α).
                // `Cursor.SetCursor` then composites it onto the screen with straight alpha
                // blending, so edge pixels with α=128 get RGB·α² — visible as a dark halo
                // around every translucent edge. Reverse the pre-multiplication so the OS sees
                // straight-alpha pixels.
                var px = result.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    byte a = px[i].a;
                    if (a == 0 || a == 255) continue;
                    float inv = 255f / a;
                    px[i].r = (byte)Mathf.Min(255, Mathf.RoundToInt(px[i].r * inv));
                    px[i].g = (byte)Mathf.Min(255, Mathf.RoundToInt(px[i].g * inv));
                    px[i].b = (byte)Mathf.Min(255, Mathf.RoundToInt(px[i].b * inv));
                }
                result.SetPixels32(px);
                result.Apply();
            }
            catch (System.Exception e)
            {
                if (DebugLogging)
                    Plugin.Log?.LogInfo($"[Cinnamon] camera extract exception: {e.Message}");
                if (result != null) { Destroy(result); result = null; }
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (spriteHolder != null) Destroy(spriteHolder);
                if (camHolder != null) Destroy(camHolder);
            }

            return result;
        }

        void EnsureCursorRefs(bool forLogPass)
        {
            if (_refsFrame == Time.frameCount) return;
            _refsFrame = Time.frameCount;

            Transform bestRoot = null;
            Camera bestCam = null;
            int bestSortOrder = int.MinValue;
            float bestDist = float.MaxValue;
            Vector3 mouse = Input.mousePosition;

            var candidates = Object.FindObjectsOfType<global::Cursor>();
            if (forLogPass) Plugin.Log?.LogInfo($"[Cinnamon] candidates: {candidates.Length} Cursor object(s)");

            foreach (var cur in candidates)
            {
                if (cur == null) continue;
                var t = ((Component)cur).transform;
                bool active = t.gameObject.activeInHierarchy;
                var sr = t.GetComponentInChildren<SpriteRenderer>(false);
                bool srOk = sr != null && sr.enabled && sr.sprite != null;
                float alpha = sr != null ? sr.color.a : 0f;
                var useCam = cur.UseCamera;

                if (forLogPass)
                {
                    Plugin.Log?.LogInfo($"[Cinnamon]   '{t.name}' active={active} useCam={(useCam ? useCam.name : "null")} srOk={srOk} alpha={alpha:F2} sortOrder={(sr ? sr.sortingOrder : 0)} sprite={(sr && sr.sprite ? sr.sprite.name : "null")}");
                }

                if (!active)        { if (forLogPass) Plugin.Log?.LogInfo($"[Cinnamon]     reject '{t.name}': !active"); continue; }
                if (!srOk)          { if (forLogPass) Plugin.Log?.LogInfo($"[Cinnamon]     reject '{t.name}': srOk=false"); continue; }
                // We INTENTIONALLY accept alpha<=0 here because the *previous* Active frame
                // is the one that set this SR's alpha to 0 to hide it. Treating that as
                // "not a valid cursor" would make us drop our own selection every frame.

                int layerMask = 1 << sr.gameObject.layer;

                Camera bestForThis = null;
                float bestForThisDist = float.MaxValue;

                if (useCam != null && useCam.enabled)
                {
                    var sp2 = useCam.WorldToScreenPoint(sr.bounds.center);
                    bestForThis = useCam;
                    bestForThisDist = new Vector2(sp2.x - mouse.x, sp2.y - mouse.y).sqrMagnitude;
                }

                if (bestForThis == null)
                {
                    foreach (var c in Camera.allCameras)
                    {
                        if (c == null || !c.enabled || (c.cullingMask & layerMask) == 0) continue;
                        var sp2 = c.WorldToScreenPoint(sr.bounds.center);
                        if (sp2.z <= 0f) continue;
                        float d2 = (new Vector2(sp2.x, sp2.y) - new Vector2(mouse.x, mouse.y)).sqrMagnitude;
                        if (d2 < bestForThisDist) { bestForThisDist = d2; bestForThis = c; }
                    }
                }
                if (bestForThis == null)
                {
                    if (forLogPass) Plugin.Log?.LogInfo($"[Cinnamon]     reject '{t.name}': no camera passed");
                    continue;
                }

                bool winsBySort = sr.sortingOrder > bestSortOrder;
                bool winsByDist = sr.sortingOrder == bestSortOrder && bestForThisDist < bestDist;
                if (winsBySort || winsByDist)
                {
                    bestSortOrder = sr.sortingOrder;
                    bestDist = bestForThisDist;
                    bestRoot = t;
                    bestCam = bestForThis;
                }
            }

            if (bestRoot != null)
            {
                _cursorRoot = bestRoot;
                _cursorCam = bestCam;
            }
            else if (_cursorRoot != null && !_cursorRoot.gameObject.activeInHierarchy)
            {
                _cursorRoot = null;
                _cursorCam = null;
            }
        }

        void EnsureBlankTex()
        {
            if (_blankTex != null) return;
            _blankTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _blankTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            _blankTex.Apply();
            _blankTex.hideFlags = HideFlags.HideAndDontSave;
        }

        void OnDestroy()
        {
            if (_active) ExitActive();
            foreach (var kv in _extractedCache)
                if (kv.Value != null) Destroy(kv.Value);
            _extractedCache.Clear();
            if (_blankTex != null) { Destroy(_blankTex); _blankTex = null; }
            if (_instance == this) _instance = null;
        }
    }
}
