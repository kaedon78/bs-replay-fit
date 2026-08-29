using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace ControllerAutoAdjust
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

        /// <summary>Read the live settings, or false if the controllers are not up yet.</summary>
        internal static bool TryRead(out Reading reading)
        {
            reading = default;
            reading.PlatformHelper = "none";

            var controllers = Resources.FindObjectsOfTypeAll<VRController>()
                .Where(c => c != null)
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
