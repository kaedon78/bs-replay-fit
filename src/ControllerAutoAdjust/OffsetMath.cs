using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// The settings screen's numbers, translated into the rotation the saber actually gets.
    /// </summary>
    /// <remarks>
    /// This is the step that has to be right, and the one that is easiest to get wrong
    /// quietly. It mirrors <c>VRController.TryGetControllerOffset</c>, alternative-handling
    /// branch:
    /// <code>
    ///     vector4 = legacyRotationOffset + typedRotation
    ///     if left: vector4 = vector4.MirrorEulerAnglesOnYZPlane()   // (x, -y, -z)
    ///     rotation = rootPose.rotation * Quaternion.Euler(vector4)
    /// </code>
    /// Two properties of that are worth stating outright, because both were arrived at the
    /// expensive way. The legacy offset is added to the typed value *before* the quaternion
    /// is built, so it does not cancel out of a difference between two settings and cannot be
    /// assumed away. The root pose left-multiplies, so it does cancel, which is the only
    /// reason a candidate can be scored without the game running.
    ///
    /// Getting the left hand's mirror wrong does not look like an error. It flips the sign of
    /// whichever term is doing the work, and a sign-flipped correction reads as a setting
    /// that simply did nothing.
    /// </remarks>
    internal static class OffsetMath
    {
        /// <summary>
        /// Below this, two settings put the blade in the same place and are one epoch.
        /// </summary>
        /// <remarks>
        /// Shared rather than restated, because a test asserting against a different number
        /// than the code uses passes and proves nothing. One degree of typed Y turns out to
        /// be 0.66 degrees of real rotation, so the two thresholds are not interchangeable.
        /// </remarks>
        internal const float SameGripDegrees = 0.25f;

        /// <summary>The Euler the game feeds to <c>Quaternion.Euler</c> for this hand.</summary>
        internal static Vector3 AppliedEuler(
            Vector3 typed, bool left, Vector3 legacyRotation, bool alternativeHandling)
        {
            if (!alternativeHandling)
            {
                // The other branch applies the typed value unmixed and mirrors the whole
                // pose afterwards instead.
                return typed;
            }
            var total = legacyRotation + typed;
            return left ? new Vector3(total.x, -total.y, -total.z) : total;
        }

        internal static Quaternion Applied(
            Vector3 typed, bool left, Vector3 legacyRotation, bool alternativeHandling)
        {
            return Quaternion.Euler(
                AppliedEuler(typed, left, legacyRotation, alternativeHandling));
        }

        /// <summary>
        /// The rotation, in the saber's own frame, that moving from one setting to another
        /// produces. This is what a candidate offset is scored on.
        /// </summary>
        internal static Quaternion Turn(
            Vector3 from, Vector3 to, bool left, Vector3 legacyRotation, bool alternativeHandling)
        {
            var a = Applied(from, left, legacyRotation, alternativeHandling);
            var b = Applied(to, left, legacyRotation, alternativeHandling);
            return Quaternion.Inverse(a) * b;
        }

        /// <summary>
        /// A small rotation as an axis-angle vector in radians, which is the form the cut
        /// arithmetic uses.
        /// </summary>
        /// <remarks>
        /// Only the two components across the blade appear in the result. Roll about the
        /// blade axis drops out of the geometry: rolling the saber leaves the blade where it
        /// was, and the cut plane is fixed by the blade and the swing.
        /// </remarks>
        internal static Vector2 TurnVector(Quaternion turn)
        {
            if (turn.w < 0f)
            {
                turn = new Quaternion(-turn.x, -turn.y, -turn.z, -turn.w);
            }
            var w = Mathf.Clamp(turn.w, -1f, 1f);
            var angle = 2f * Mathf.Acos(w);
            var sin = Mathf.Sqrt(Mathf.Max(1f - w * w, 0f));
            if (sin < 1e-7f || angle < 1e-7f)
            {
                return Vector2.zero;
            }
            var scale = angle / sin;
            return new Vector2(turn.x * scale, turn.y * scale);
        }

        internal static Vector2 TurnVector(
            Vector3 from, Vector3 to, bool left, Vector3 legacyRotation, bool alternativeHandling)
        {
            return TurnVector(Turn(from, to, left, legacyRotation, alternativeHandling));
        }
    }
}
