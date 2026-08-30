using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace ReplayFit
{
    /// <summary>What the controller offset settings are, and what the game makes of them.</summary>
    /// <remarks>
    /// Read once and shared, because two different things need it and they must not disagree:
    /// the probe reports it, and every recorded cut is stamped with it. That stamp is the
    /// whole reason this can become a loop. Offline, which settings a session was played on
    /// had to be inferred from "nothing changed for two months"; the moment an auto-adjuster
    /// starts moving them, every session is its own epoch and inference stops working.
    /// </remarks>
    internal static class OffsetState
    {
        internal struct Hand
        {
            public Vector3 TypedPosition;
            public Vector3 TypedRotation;
            public Pose Applied;
            public bool AppliedValid;
        }

        internal struct Reading
        {
            public Hand Left;
            public Hand Right;
            public Vector3 LegacyPosition;
            public Vector3 LegacyRotation;
            public bool LegacyValid;
            public bool AlternativeHandling;
            public string PlatformHelper;

            public Hand For(bool left) => left ? Left : Right;
        }

        private static readonly FieldInfo OffsetField = typeof(VRController).GetField(
            "_transformOffset", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo HelperField = typeof(VRController).GetField(
            "_vrPlatformHelper", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>How many reads have failed, so a persistent one is not logged per frame.</summary>
        private static int _failures;

        /// <summary>Read the live settings, or false if the controllers are not up yet.</summary>
        /// <remarks>
        /// Nothing here throws, which is the contract a method named Try has and this one did
        /// not keep. The offset provider is reachable before it is usable: asked for a
        /// position offset while the controller is still setting itself up, it goes looking
        /// for a selected profile that is not there yet and throws. That escaped into the
        /// game's own setup and cost a player the use of their hands.
        ///
        /// A failure gives back false and no reading, never a partial one. Half a reading
        /// would be journalled as though it were the settings in force, which is a wrong
        /// answer recorded as fact rather than a step that did not happen.
        /// </remarks>
        internal static bool TryRead(out Reading reading)
        {
            try
            {
                return Read(out reading);
            }
            catch (Exception e)
            {
                reading = default;
                reading.PlatformHelper = "none";
                // Once, then rarely. This can fail every frame for as long as a scene takes
                // to build, and a log that scrolls past at that rate is the thing that was
                // wrong here in the first place.
                if (_failures++ % 600 == 0)
                {
                    Plugin.Log.Warn(
                        $"controller settings not readable yet ({e.GetType().Name}); "
                        + $"attempt {_failures}");
                }
                return false;
            }
        }

        private static bool Read(out Reading reading)
        {
            reading = default;
            reading.PlatformHelper = "none";

            // Scene-loaded only. FindObjectsOfTypeAll also returns prefabs, whose offset
            // provider is unset, and its ordering is not guaranteed -- so a prefab landing
            // last would overwrite a hand's real settings with zeroes on some frames and not
            // others.
            var controllers = Resources.FindObjectsOfTypeAll<VRController>()
                .Where(c => c != null && c.gameObject.scene.isLoaded)
                .ToList();
            if (controllers.Count == 0)
            {
                return false;
            }

            var any = false;
            foreach (var controller in controllers)
            {
                var isLeft = controller.node == XRNode.LeftHand;
                if (!isLeft && controller.node != XRNode.RightHand)
                {
                    continue;
                }
                any = true;

                var hand = new Hand();
                if (OffsetField?.GetValue(controller) is VRControllerTransformOffset settings)
                {
                    hand.TypedPosition = isLeft
                        ? settings.leftPositionOffset : settings.rightPositionOffset;
                    hand.TypedRotation = isLeft
                        ? settings.leftRotationOffset : settings.rightRotationOffset;
                    reading.AlternativeHandling = settings.alternativeHandling;
                }
                hand.AppliedValid = controller.TryGetControllerOffset(out hand.Applied);

                if (HelperField?.GetValue(controller) is IVRPlatformHelper helper)
                {
                    reading.PlatformHelper = helper.GetType().Name;
                    // Zero here is ambiguous and the ambiguity matters: under fpfc the
                    // deviceless helper returns zero whatever the hardware would have done,
                    // and a real Valve Index would otherwise contribute -16.3 degrees of X --
                    // more than the whole correction being searched for. LegacyValid says
                    // which of the two this was.
                    reading.LegacyValid |= helper.TryGetLegacyPoseOffsetForNode(
                        controller.node, out reading.LegacyPosition, out reading.LegacyRotation);
                }

                if (isLeft)
                {
                    reading.Left = hand;
                }
                else
                {
                    reading.Right = hand;
                }
            }
            return any;
        }

        internal static string Describe(Reading r)
        {
            return $"left typed rot {Fmt(r.Left.TypedRotation)} pos {Fmt(r.Left.TypedPosition)}; "
                 + $"right typed rot {Fmt(r.Right.TypedRotation)} pos {Fmt(r.Right.TypedPosition)}; "
                 + $"legacy rot {Fmt(r.LegacyRotation)} valid={r.LegacyValid}; "
                 + $"alt={r.AlternativeHandling}; via {r.PlatformHelper}";
        }

        internal static string Fmt(Vector3 v) =>
            $"({v.x:F4}, {v.y:F4}, {v.z:F4})";
    }
}
