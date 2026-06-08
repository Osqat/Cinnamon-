using System.Collections.Generic;
using UnityEngine;

namespace Cinnamon.UI
{
    internal class CursorOverlayHost : MonoBehaviour, InputReceiver
    {
        const int MirrorLayer = 31;
        static readonly Vector3 MirrorOrigin = new Vector3(50000f, 50000f, 0f);

        static CursorOverlayHost _instance;

        global::Cursor _cursor;
        Transform _cursorRoot;
        Camera _cursorCam;
        int _refsFrame = -1;
        int _requestedFrame = -999;
        bool _registeredGlobalReceiver;

        bool _active;
        readonly Dictionary<SpriteRenderer, Color> _hiddenColors = new Dictionary<SpriteRenderer, Color>();
        readonly List<SpriteRenderer> _sourceRenderers = new List<SpriteRenderer>();
        readonly List<MirrorRenderer> _mirrorRenderers = new List<MirrorRenderer>();

        GameObject _mirrorRoot;
        Camera _mirrorCam;
        RenderTexture _mirrorRt;
        Rect _drawRect;
        bool _hasDrawRect;

        bool _acceptHeld;
        int _acceptPressedFrame = -999;
        int _acceptConsumedFrame = -999;
        int _acceptReleasedFrame = -999;

        public bool DebugLogging { get; set; }
        internal static CursorOverlayHost Instance { get { return _instance; } }

        public static CursorOverlayHost EnsureExists()
        {
            if (_instance != null) return _instance;

            var go = new GameObject("Cinnamon.CursorOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CursorOverlayHost>();
            return _instance;
        }

        void Awake()
        {
            RegisterGlobalReceiver();
        }

        public bool IsCursorReady()
        {
            EnsureCursorRefs(forLogPass: false);
            return _cursorRoot != null;
        }

        public void Request()
        {
            _requestedFrame = Time.frameCount;
            RegisterGlobalReceiver();
        }

        void LateUpdate()
        {
            bool wantActive = _requestedFrame >= Time.frameCount - 1;

            if (wantActive)
                EnterOrUpdateActive();
            else if (_active)
                ExitActive();
        }

        void EnterOrUpdateActive()
        {
            RestoreHiddenRenderers();
            EnsureCursorRefs(forLogPass: false);

            if (_cursorRoot == null)
            {
                ExitActive();
                return;
            }

            _active = true;
            UnityEngine.Cursor.visible = false;

            CollectSourceRenderers();
            if (_sourceRenderers.Count == 0)
            {
                _hasDrawRect = false;
                return;
            }

            Bounds worldBounds = _sourceRenderers[0].bounds;
            for (int i = 1; i < _sourceRenderers.Count; i++)
                worldBounds.Encapsulate(_sourceRenderers[i].bounds);

            if (!PrepareDrawRect(worldBounds))
            {
                _hasDrawRect = false;
                return;
            }

            EnsureMirrorObjects(_sourceRenderers.Count);
            SyncMirror(worldBounds);
            if (RenderMirror(worldBounds))
                HideSourceRenderers();
            else
                _hasDrawRect = false;
        }

        void ExitActive()
        {
            RestoreHiddenRenderers();
            ClearMirrorRenderers();
            ReleaseRenderTexture();
            _hasDrawRect = false;
            _active = false;
            _acceptHeld = false;

            UnityEngine.Cursor.visible = true;
        }

        void CollectSourceRenderers()
        {
            _sourceRenderers.Clear();
            var renderers = _cursorRoot.GetComponentsInChildren<SpriteRenderer>(false);
            SpriteRenderer main = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                var sr = renderers[i];
                if (sr == null || !sr.enabled || sr.sprite == null) continue;
                main = sr;
                break;
            }

            if (main == null) return;

            _sourceRenderers.Add(main);
            for (int i = 0; i < renderers.Length; i++)
            {
                var sr = renderers[i];
                if (sr == null || sr == main || !sr.enabled || sr.sprite == null) continue;
                if (sr.transform.IsChildOf(main.transform))
                    _sourceRenderers.Add(sr);
            }
        }

        void HideSourceRenderers()
        {
            _hiddenColors.Clear();
            for (int i = 0; i < _sourceRenderers.Count; i++)
            {
                var sr = _sourceRenderers[i];
                if (sr == null) continue;

                Color original = sr.color;
                _hiddenColors[sr] = original;
                original.a = 0f;
                sr.color = original;
            }
        }

        void RestoreHiddenRenderers()
        {
            foreach (var kv in _hiddenColors)
            {
                if (kv.Key != null)
                    kv.Key.color = kv.Value;
            }
            _hiddenColors.Clear();
        }

        bool PrepareDrawRect(Bounds worldBounds)
        {
            var cam = _cursorCam != null ? _cursorCam : Camera.main;
            if (cam == null) return false;

            var sBL = cam.WorldToScreenPoint(new Vector3(worldBounds.min.x, worldBounds.min.y, worldBounds.center.z));
            var sTR = cam.WorldToScreenPoint(new Vector3(worldBounds.max.x, worldBounds.max.y, worldBounds.center.z));
            if (sBL.z < 0f || sTR.z < 0f) return false;

            var guiBL = GUIUtility.ScreenToGUIPoint(new Vector2(sBL.x, sBL.y));
            var guiTR = GUIUtility.ScreenToGUIPoint(new Vector2(sTR.x, sTR.y));

            float x = guiBL.x;
            float y = guiTR.y;
            float w = guiTR.x - guiBL.x;
            float h = guiBL.y - guiTR.y;
            if (w <= 0f || h <= 0f) return false;

            _drawRect = new Rect(x, y, w, h);
            _hasDrawRect = true;
            return true;
        }

        void EnsureMirrorObjects(int count)
        {
            if (_mirrorRoot == null)
            {
                _mirrorRoot = new GameObject("Cinnamon.CursorMirror.Root");
                _mirrorRoot.hideFlags = HideFlags.HideAndDontSave;
                _mirrorRoot.layer = MirrorLayer;
            }

            while (_mirrorRenderers.Count < count)
            {
                var go = new GameObject("Cinnamon.CursorMirror.Sprite");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = MirrorLayer;
                go.transform.parent = _mirrorRoot.transform;
                var sr = go.AddComponent<SpriteRenderer>();
                _mirrorRenderers.Add(new MirrorRenderer { GameObject = go, Renderer = sr });
            }

            for (int i = 0; i < _mirrorRenderers.Count; i++)
                _mirrorRenderers[i].GameObject.SetActive(i < count);

            if (_mirrorCam == null)
            {
                var camGo = new GameObject("Cinnamon.CursorMirror.Camera");
                camGo.hideFlags = HideFlags.HideAndDontSave;
                _mirrorCam = camGo.AddComponent<Camera>();
                _mirrorCam.enabled = false;
                _mirrorCam.orthographic = true;
                _mirrorCam.cullingMask = 1 << MirrorLayer;
                _mirrorCam.clearFlags = CameraClearFlags.SolidColor;
                _mirrorCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _mirrorCam.nearClipPlane = 0.01f;
                _mirrorCam.farClipPlane = 100f;
                _mirrorCam.allowHDR = false;
                _mirrorCam.allowMSAA = false;
            }
        }

        void SyncMirror(Bounds worldBounds)
        {
            Vector3 offset = MirrorOrigin - worldBounds.center;

            for (int i = 0; i < _sourceRenderers.Count; i++)
            {
                var source = _sourceRenderers[i];
                var mirror = _mirrorRenderers[i].Renderer;
                var mt = mirror.transform;

                mt.position = source.transform.position + offset;
                mt.rotation = source.transform.rotation;
                mt.localScale = source.transform.lossyScale;

                mirror.sprite = source.sprite;
                mirror.color = source.color;
                mirror.flipX = source.flipX;
                mirror.flipY = source.flipY;
                mirror.sortingLayerID = source.sortingLayerID;
                mirror.sortingOrder = source.sortingOrder;
                if (source.sharedMaterial != null)
                    mirror.sharedMaterial = source.sharedMaterial;
            }
        }

        bool RenderMirror(Bounds worldBounds)
        {
            if (_mirrorCam == null || !_hasDrawRect) return false;

            int width = Mathf.Clamp(Mathf.CeilToInt(_drawRect.width), 1, 1024);
            int height = Mathf.Clamp(Mathf.CeilToInt(_drawRect.height), 1, 1024);
            EnsureRenderTexture(width, height);
            if (_mirrorRt == null) return false;

            Vector3 mirrorCenter = MirrorOrigin;
            float boundsW = Mathf.Max(0.001f, worldBounds.size.x);
            float boundsH = Mathf.Max(0.001f, worldBounds.size.y);

            _mirrorCam.transform.position = new Vector3(mirrorCenter.x, mirrorCenter.y, mirrorCenter.z - 10f);
            _mirrorCam.transform.rotation = Quaternion.identity;
            _mirrorCam.orthographicSize = boundsH * 0.5f;
            _mirrorCam.aspect = boundsW / boundsH;
            _mirrorCam.targetTexture = _mirrorRt;

            _mirrorCam.Render();
            return true;
        }

        void EnsureRenderTexture(int width, int height)
        {
            if (_mirrorRt != null && _mirrorRt.width == width && _mirrorRt.height == height) return;

            ReleaseRenderTexture();
            _mirrorRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _mirrorRt.hideFlags = HideFlags.HideAndDontSave;
            _mirrorRt.Create();
        }

        void ReleaseRenderTexture()
        {
            if (_mirrorCam != null)
                _mirrorCam.targetTexture = null;
            if (_mirrorRt != null)
            {
                _mirrorRt.Release();
                Destroy(_mirrorRt);
                _mirrorRt = null;
            }
        }

        void ClearMirrorRenderers()
        {
            for (int i = 0; i < _mirrorRenderers.Count; i++)
            {
                if (_mirrorRenderers[i].GameObject != null)
                    Destroy(_mirrorRenderers[i].GameObject);
            }
            _mirrorRenderers.Clear();

            if (_mirrorRoot != null)
            {
                Destroy(_mirrorRoot);
                _mirrorRoot = null;
            }
            if (_mirrorCam != null)
            {
                Destroy(_mirrorCam.gameObject);
                _mirrorCam = null;
            }
        }

        void EnsureCursorRefs(bool forLogPass)
        {
            if (_refsFrame == Time.frameCount) return;
            _refsFrame = Time.frameCount;

            global::Cursor bestCursor = null;
            Transform bestRoot = null;
            Camera bestCam = null;
            int bestSortOrder = int.MinValue;

            var candidates = Object.FindObjectsOfType<global::Cursor>();
            if (forLogPass) Plugin.Log?.LogInfo("[Cinnamon] candidates: " + candidates.Length + " Cursor object(s)");

            for (int i = 0; i < candidates.Length; i++)
            {
                var cur = candidates[i];
                if (cur == null) continue;
                var t = ((Component)cur).transform;
                if (!t.gameObject.activeInHierarchy) continue;

                var sr = t.GetComponentInChildren<SpriteRenderer>(false);
                bool srOk = sr != null && sr.enabled && sr.sprite != null;
                bool isCurrent = _cursorRoot == t;
                Camera cam = cur.UseCamera != null && cur.UseCamera.enabled ? cur.UseCamera : Camera.main;
                if (cam == null && srOk)
                {
                    int layerMask = 1 << sr.gameObject.layer;
                    foreach (var c in Camera.allCameras)
                    {
                        if (c != null && c.enabled && (c.cullingMask & layerMask) != 0)
                        {
                            cam = c;
                            break;
                        }
                    }
                }
                if (cam == null) continue;

                int sort = srOk ? sr.sortingOrder : int.MinValue + 1;
                bool bestIsCurrent = bestRoot == _cursorRoot;
                bool winsByCurrent = isCurrent && !bestIsCurrent;
                bool winsBySort = sort > bestSortOrder && !bestIsCurrent;

                if (bestRoot == null || winsByCurrent || winsBySort)
                {
                    bestCursor = cur;
                    bestRoot = t;
                    bestCam = cam;
                    bestSortOrder = sort;
                }
            }

            if (bestRoot != null)
            {
                _cursor = bestCursor;
                _cursorRoot = bestRoot;
                _cursorCam = bestCam;
            }
            else if (_cursorRoot != null && !_cursorRoot.gameObject.activeInHierarchy)
            {
                _cursor = null;
                _cursorRoot = null;
                _cursorCam = null;
            }
        }

        void OnGUI()
        {
            if (!_active || !_hasDrawRect || _mirrorRt == null) return;

            GUI.depth = -32000;
            GUI.DrawTexture(_drawRect, _mirrorRt);
        }

        public bool AcceptHeld { get { return _acceptHeld; } }
        public bool AcceptReleased { get { return _acceptReleasedFrame >= Time.frameCount - 1; } }

        public bool ConsumeAcceptPressed()
        {
            if (_acceptPressedFrame < Time.frameCount - 1) return false;
            if (_acceptConsumedFrame == _acceptPressedFrame) return false;
            _acceptConsumedFrame = _acceptPressedFrame;
            return true;
        }

        public bool TryGetPointerPosition(out Vector2 guiPos)
        {
            guiPos = default(Vector2);
            if (_requestedFrame < Time.frameCount - 1) return false;

            EnsureCursorRefs(forLogPass: false);
            if (_cursorRoot == null) return false;

            var cam = _cursorCam != null ? _cursorCam : Camera.main;
            if (cam == null) return false;

            var pc = _cursorRoot.GetComponent<PickCursor>();
            Vector3 world = pc != null && pc.cursorPoint != null ? pc.cursorPoint.position : _cursorRoot.position;
            var sp = cam.WorldToScreenPoint(world);
            if (sp.z < 0f) return false;

            guiPos = GUIUtility.ScreenToGUIPoint(new Vector2(sp.x, sp.y));
            return true;
        }

        public void ReceiveEvent(InputEvent e)
        {
            if (e == null || e.Key != InputEvent.InputKey.Accept) return;
            if (_requestedFrame < Time.frameCount - 1) return;
            if (e.Sender == null || e.Sender.IsKeyboard) return;

            var activeController = _cursor != null && _cursor.LocalPlayer != null ? _cursor.LocalPlayer.UseController : null;
            if (activeController != null && e.Sender != activeController) return;

            _acceptHeld = e.Valueb;
            if (!e.Changed) return;

            if (e.Valueb)
                _acceptPressedFrame = Time.frameCount;
            else
                _acceptReleasedFrame = Time.frameCount;
        }

        void RegisterGlobalReceiver()
        {
            if (_registeredGlobalReceiver) return;
            Controller.AddGlobalReceiver(this);
            _registeredGlobalReceiver = true;
        }

        void OnDestroy()
        {
            ExitActive();
            if (_registeredGlobalReceiver)
            {
                Controller.RemoveGlobalReceiver(this);
                _registeredGlobalReceiver = false;
            }
            if (_instance == this) _instance = null;
        }

        class MirrorRenderer
        {
            public GameObject GameObject;
            public SpriteRenderer Renderer;
        }
    }
}
