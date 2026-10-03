using System;
using EFT.CameraControl;
using UnityEngine;

namespace HeadTracking.Game
{
    /// <summary>
    /// Lean-in zoom: narrows the main camera's field of view for the length of one render.
    ///
    /// EFT sets the FOV on events, not every frame: CameraManager.SetFov runs a coroutine on aiming
    /// changes (lerping from the camera's current value), ApplyFoV runs on settings, and a few
    /// effects write it while active. So the reduction is applied in Camera.onPreCull and put back
    /// in Camera.onPostRender. In between nothing of the game's runs, so its logic never reads or
    /// lerps from our value, and nothing accumulates. Scope cameras are separate cameras and are
    /// never touched; the zoom is also faded out while aiming.
    /// </summary>
    internal static class ZoomController
    {
        /// <summary>Degrees to take off the FOV this frame. Set by the camera hook in LateUpdate.</summary>
        internal static float Reduction;

        private static bool _installed;
        private static Camera _applyingTo;
        private static float _baseFov;
        private static float _appliedFov;
        private static int _missedRestores;

        internal static float LastBaseFov { get; private set; }
        internal static float LastApplied { get; private set; }

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }

            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            _installed = true;
            HeadTrackingPlugin.Log.LogInfo("Zoom: hooked Camera.onPreCull / onPostRender.");
        }

        internal static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            Restore();
            _installed = false;
        }

        private static void OnPreCull(Camera camera)
        {
            // Every camera passes through here; leave on the cheapest check first. Note that
            // CameraManager.Instance constructs the manager when there is none, so it is only
            // touched while a reduction is wanted, which needs a player in raid.
            if (Reduction <= 0.01f && _applyingTo == null)
            {
                return;
            }

            try
            {
                Camera main = CameraManager.Instance.Camera;
                if (main == null || camera != main)
                {
                    return;
                }

                if (_applyingTo != null)
                {
                    // onPostRender did not run for the last frame (camera disabled mid-frame).
                    // Put the base back before reading it, or the reduction would compound.
                    Restore();
                    if (++_missedRestores <= 3)
                    {
                        HeadTrackingPlugin.Log.LogWarning("Zoom: a frame ended without onPostRender; restored the FOV first.");
                    }
                }

                if (Reduction <= 0.01f)
                {
                    LastApplied = 0;
                    return;
                }

                _baseFov = camera.fieldOfView;
                _appliedFov = Mathf.Max(10f, _baseFov - Reduction);
                camera.fieldOfView = _appliedFov;
                _applyingTo = camera;
                LastBaseFov = _baseFov;
                LastApplied = _baseFov - _appliedFov;
            }
            catch (Exception e)
            {
                HeadTrackingPlugin.Log.LogError("Zoom: onPreCull failed, zoom switched off: " + e);
                Reduction = 0;
                Uninstall();
            }
        }

        private static void OnPostRender(Camera camera)
        {
            if (_applyingTo != null && camera == _applyingTo)
            {
                Restore();
            }
        }

        private static void Restore()
        {
            Camera camera = _applyingTo;
            _applyingTo = null;
            // Only undo our own write: if something set a new FOV in between, that one stands.
            if (camera != null && Mathf.Approximately(camera.fieldOfView, _appliedFov))
            {
                camera.fieldOfView = _baseFov;
            }
        }
    }
}
