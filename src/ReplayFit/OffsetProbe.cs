using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace ReplayFit
{
    /// <summary>
    /// Logs what the game actually does with the controller offset settings.
    /// </summary>
    /// <remarks>
    /// This exists because the mapping from "the numbers in the settings screen" to "where
    /// the blade ends up" cannot be worked out reliably from outside the game, and getting
    /// it wrong is invisible: the fit still produces confident numbers, they just describe a
    /// different rotation than the one the player will get.
    ///
    /// Reading 1.45.0's <c>VRController.TryGetControllerOffset</c>, with alternative handling
    /// on, the game adds a per-manufacturer legacy offset to the typed setting before
    /// building the quaternion -- -16.3 degrees of X for a Valve Index -- and negates Y and Z
    /// for the left hand. Two plausible readings of that code differ by 3 to 8 degrees in the
    /// saber's frame, and replay data could not tell them apart: the run-to-run spread of the
    /// fitted angle is around 1.8 degrees, so separating them would take more sessions than
    /// it takes to just ask the game.
    ///
    /// So this asks the game. One launch logs the offset pose it computed for each hand
    /// alongside the settings that produced it, which pins the mapping exactly.
    /// </remarks>
    internal class OffsetProbe : MonoBehaviour
    {
        private const float RetrySeconds = 2f;
        private const int MaxAttempts = 30;

        private int _attempts;
        private int _reported;
        private float _next;
        private bool _done;

        private void Update()
        {
            if (_done || Time.unscaledTime < _next)
            {
                return;
            }
            _next = Time.unscaledTime + RetrySeconds;
            if (++_attempts > MaxAttempts)
            {
                Plugin.Log.Warn($"gave up looking for VRControllers after {MaxAttempts} tries");
                _done = true;
                return;
            }

            var controllers = Resources.FindObjectsOfTypeAll<VRController>();
            if (controllers == null || controllers.Length == 0)
            {
                return;
            }

            try
            {
                // Under fpfc there are no real controllers, so the platform reports no
                // legacy offset and the settings pass through unchanged -- which reads
                // exactly like "there is no legacy offset" and is not the same thing. Keep
                // looking until the platform answers, and say which case this was.
                var full = _reported == 0;
                var settled = Report(controllers, full);
                _reported++;
                if (settled)
                {
                    _done = true;
                }
                else if (_attempts >= MaxAttempts)
                {
                    Plugin.Log.Warn(
                        "platform never reported a legacy pose offset -- if this run was " +
                        "fpfc that is expected, and the numbers above are not the VR case");
                    _done = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"probe failed: {e}");
                _done = true;
            }
        }

        /// <returns>whether the platform gave a real answer, so retrying would add nothing.</returns>
        private static bool Report(VRController[] controllers, bool full)
        {
            if (full)
            {
                Plugin.Log.Info($"---- offset probe: {controllers.Length} VRController(s) ----");
            }

            var offsetField = typeof(VRController).GetField(
                "_transformOffset", BindingFlags.Instance | BindingFlags.NonPublic);
            var helperField = typeof(VRController).GetField(
                "_vrPlatformHelper", BindingFlags.Instance | BindingFlags.NonPublic);

            var anyLegacy = false;
            foreach (var controller in controllers)
            {
                if (controller == null)
                {
                    continue;
                }

                // The legacy offset is the whole reason the settings cannot be converted
                // outside the game: it is added to the typed value before the quaternion is
                // built, so it does not cancel out of a difference between two settings.
                var helper = helperField?.GetValue(controller) as IVRPlatformHelper;
                if (helper != null
                    && helper.TryGetLegacyPoseOffsetForNode(controller.node, out var lpos, out var lrot))
                {
                    anyLegacy = true;
                    Plugin.Log.Info(
                        $"{controller.node} legacy: pos {Fmt(lpos)} rot {Fmt(lrot)} " +
                        $"via {helper.GetType().Name}");
                    var root = helper.GetRootPositionOffsetForLegacyNodePose(controller.node);
                    Plugin.Log.Info(
                        $"{controller.node} rootPose: pos {Fmt(root.position)} " +
                        $"rotEuler {Fmt(root.rotation.eulerAngles)}");
                }
                else if (full)
                {
                    Plugin.Log.Info(
                        $"{controller.node} legacy: none reported " +
                        $"(helper={(helper == null ? "null" : helper.GetType().Name)})");
                }

                if (!full)
                {
                    continue;
                }

                var settings = offsetField?.GetValue(controller) as VRControllerTransformOffset;
                var isLeft = controller.node == XRNode.LeftHand;

                if (settings != null)
                {
                    var pos = isLeft ? settings.leftPositionOffset : settings.rightPositionOffset;
                    var rot = isLeft ? settings.leftRotationOffset : settings.rightRotationOffset;
                    Plugin.Log.Info(
                        $"{controller.node} setting: pos {Fmt(pos)} rot {Fmt(rot)} " +
                        $"alternativeHandling={settings.alternativeHandling}");
                }
                else
                {
                    Plugin.Log.Warn($"{controller.node}: no VRControllerTransformOffset reachable");
                }

                // The result is what settles it: whatever the branch and the platform
                // constants are, this is the pose the game arrived at.
                if (controller.TryGetControllerOffset(out var pose))
                {
                    var e = pose.rotation.eulerAngles;
                    Plugin.Log.Info(
                        $"{controller.node} applied: pos {Fmt(pose.position)} " +
                        $"rotEuler {Fmt(e)} " +
                        $"quat ({pose.rotation.x:F6}, {pose.rotation.y:F6}, " +
                        $"{pose.rotation.z:F6}, {pose.rotation.w:F6})");
                }
                else
                {
                    Plugin.Log.Warn($"{controller.node}: TryGetControllerOffset returned false");
                }
            }

            if (full)
            {
                Plugin.Log.Info("---- offset probe: end ----");
            }
            return anyLegacy;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";
    }
}
