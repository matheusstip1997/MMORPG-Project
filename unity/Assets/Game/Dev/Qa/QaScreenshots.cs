#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// PNG screenshots for the QA smoke run. In the editor's batch mode there is no Game view, so
    /// <see cref="ScreenCapture"/> has nothing to read: the enabled cameras are rendered into a texture by depth, and the
    /// UI Toolkit panels (which <see cref="RedirectPanels"/> points at a texture of their own) are blended on top.
    /// With a window (no <c>-batchmode</c>) the screen is captured as it is. Every PNG is opaque, like the frame on screen.
    /// </summary>
    public sealed class QaScreenshots : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private RenderTexture? _ui;

        public QaScreenshots(int width, int height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>True when the frame has to be rendered by hand (batch mode).</summary>
        public static bool Offscreen => Application.isBatchMode;

        /// <summary>
        /// Batch mode only: draws every UI document of the loaded scenes into the shared UI texture from now on (cleared to
        /// transparent each frame). Call again after a scene load; panels already redirected are left alone.
        /// </summary>
        public void RedirectPanels()
        {
            if (!Offscreen)
                return;
            MatchCameras();
            foreach (var document in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var panel = document.panelSettings;
                if (panel == null)
                    continue;
                if (_ui == null)
                {
                    _ui = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32) { name = "QA UI" };
                    _ui.Create();
                }
                if (panel.targetTexture == _ui)
                    continue;
                panel.targetTexture = _ui;
                panel.clearColor = true;
                panel.colorClearValue = Color.clear;
            }
        }

        /// <summary>Writes the current frame to <paramref name="path"/>; <paramref name="done"/> gets "" or the error.</summary>
        public IEnumerator Capture(string path, Action<string> done)
        {
            // WaitForEndOfFrame never comes in batch mode; there the frame is rendered by hand, so the next frame will do.
            if (Offscreen)
                yield return null;
            else
                yield return new WaitForEndOfFrame();
            Texture2D? image = null;
            string error = "";
            try
            {
                image = Offscreen ? RenderOffscreen() : ScreenCapture.CaptureScreenshotAsTexture();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, OpaquePng(image));
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (image != null)
                    UnityEngine.Object.Destroy(image);
            }
            done(error);
        }

        public void Dispose()
        {
            if (_ui == null)
                return;
            foreach (var document in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (document.panelSettings != null && document.panelSettings.targetTexture == _ui)
                    document.panelSettings.targetTexture = null;
            }
            _ui.Release();
            UnityEngine.Object.Destroy(_ui);
            _ui = null;
        }

        /// <summary>
        /// Batch mode: every camera projects at the screenshot's aspect (QA wave 5 H5). A camera follows the batch window's
        /// aspect, but the shot renders it into a <c>_width</c> x <c>_height</c> texture: the world was then drawn wider than
        /// the HUD's world overlays projected it (name plates and damage numbers about 1.33 times farther from the center,
        /// horizontally). In a window the camera, the screen and the HUD panel share one aspect, so the game is not affected.
        /// </summary>
        public void MatchCameras()
        {
            if (!Offscreen)
                return;
            float aspect = (float)_width / _height;
            foreach (var camera in Camera.allCameras)
            {
                if (!Mathf.Approximately(camera.aspect, aspect))
                    camera.aspect = aspect;
            }
        }

        private Texture2D RenderOffscreen()
        {
            MatchCameras(); // a camera created since the last scene load
            var world = RenderTexture.GetTemporary(_width, _height, 24, RenderTextureFormat.ARGB32);
            try
            {
                var previous = RenderTexture.active;
                RenderTexture.active = world;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = previous;

                var cameras = Camera.allCameras;
                Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));
                foreach (var camera in cameras)
                    RenderCamera(camera, world);

                var image = Read(world);
                // The texture keeps the last frame of a panel that is gone (the login scene unloads on enter world).
                if (_ui != null && PanelsDrawInto(_ui))
                    BlendOver(image, Read(_ui));
                return image;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(world);
            }
        }

        private static bool PanelsDrawInto(RenderTexture texture)
        {
            foreach (var document in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (document.isActiveAndEnabled && document.panelSettings != null && document.panelSettings.targetTexture == texture)
                    return true;
            }
            return false;
        }

        private static void RenderCamera(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
                return;
            }
            var previous = camera.targetTexture;
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = previous;
        }

        private static Texture2D Read(RenderTexture source)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            return image;
        }

        /// <summary>
        /// QA wave 6 F4: the render target keeps the scene's alpha (a telegraph area leaves A = 47 where it blended), and an
        /// image viewer composites such pixels over its own background, so telegraphs looked almost white. The RGB already is
        /// the blended frame the player sees: only the alpha goes.
        /// </summary>
        public static void Opaque(Color32[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 255;
        }

        /// <summary>The PNG of <paramref name="image"/> with every pixel opaque (changes the texture).</summary>
        public static byte[] OpaquePng(Texture2D image)
        {
            var pixels = image.GetPixels32();
            Opaque(pixels);
            image.SetPixels32(pixels);
            image.Apply();
            return image.EncodeToPNG();
        }

        /// <summary>Straight alpha "over": the UI on top of the world. Consumes <paramref name="top"/>.</summary>
        private static void BlendOver(Texture2D bottom, Texture2D top)
        {
            var below = bottom.GetPixels32();
            var above = top.GetPixels32();
            for (int i = 0; i < below.Length && i < above.Length; i++)
            {
                int a = above[i].a;
                if (a == 0)
                    continue;
                int inverse = 255 - a;
                below[i] = new Color32(
                    (byte)((above[i].r * a + below[i].r * inverse) / 255),
                    (byte)((above[i].g * a + below[i].g * inverse) / 255),
                    (byte)((above[i].b * a + below[i].b * inverse) / 255),
                    255);
            }
            bottom.SetPixels32(below);
            bottom.Apply();
            UnityEngine.Object.Destroy(top);
        }
    }
}
