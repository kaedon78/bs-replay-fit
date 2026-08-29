using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Drives the sabers along a scripted sweep so cuts happen without a headset.
    /// </summary>
    /// <remarks>
    /// Under <c>fpfc</c> there is no controller pose at all -- the platform falls back to
    /// <c>DevicelessVRHelper</c> and the sabers sit at their default positions -- so no note
    /// is ever cut and nothing downstream of the cut event can be exercised. That would leave
    /// the recorder unvalidated until a headset is available, which is the wrong dependency
    /// for a piece of code whose whole job is arithmetic.
    ///
    /// The game already has the seam: <c>SetupAutoplayForAllControllers</c> disables the
    /// <c>VRController</c> components precisely so something else can drive them. Moving the
    /// controller transform rather than the saber avoids fighting Unity's execution order --
    /// the sabers are children, so blade transforms and movement data follow on their own,
    /// and the cut detection sees exactly what it would see from a real swing.
    ///
    /// What this validates and what it does not: the cuts it produces are real cuts through
    /// the game's own geometry, so they prove the recorder's arithmetic. They say nothing
    /// whatever about how a person swings, so no fit, threshold or recommendation may ever be
    /// drawn from them.
    ///
    /// Off unless <c>UserData/ControllerAutoAdjust/synthetic-swings.on</c> exists.
    /// </remarks>
    internal class SwingHarness : MonoBehaviour
    {
        internal const string MarkerName = "synthetic-swings.on";

        private const float LookEvery = 1f;

        private PlayerVRControllersManager _manager;
        private float _nextLook;
        private float _startedAt;

        private void Update()
        {
            if (_manager != null)
            {
                Drive();
                return;
            }
            if (Time.unscaledTime < _nextLook || !Enabled())
            {
                return;
            }
            _nextLook = Time.unscaledTime + LookEvery;

            var found = Resources.FindObjectsOfTypeAll<PlayerVRControllersManager>()
                .FirstOrDefault(m => m.gameObject.scene.isLoaded);
            if (found == null)
            {
                return;
            }
            try
            {
                if (!GameApi.TryStartAutoplay(found))
                {
                    enabled = false;
                    return;
                }
                _manager = found;
                _startedAt = Time.time;
                Plugin.Log.Warn("SYNTHETIC SWINGS ACTIVE -- cuts from this run are not a player");
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not take over the controllers: {e.Message}");
                enabled = false;
            }
        }

        internal static bool Enabled()
        {
            try
            {
                return File.Exists(Path.Combine(Paths.DataDir, MarkerName));
            }
            catch
            {
                return false;
            }
        }

        // Beat Saber's note grid, which is what the blade has to be swept through.
        private static readonly Vector2 GridX = new Vector2(-0.9f, 0.9f);
        private static readonly Vector2 GridY = new Vector2(0.85f, 1.9f);

        private void Drive()
        {
            var t = Time.time - _startedAt;
            Aim(_manager.leftHandVRController, -1f, t);
            Aim(_manager.rightHandVRController, 1f, t);
        }

        /// <summary>
        /// Point the blade at a target that scans the note grid, from a fixed shoulder.
        /// </summary>
        /// <remarks>
        /// Driving Euler angles directly was the first attempt and it barely touched a note:
        /// the blade's reach depends on the angles through a sine, so "sweep 35 degrees"
        /// says nothing about where the tip actually goes, and it swept a metre in front of
        /// the plane the notes are cut on. Aiming at a point is the same motion described in
        /// the coordinates that matter.
        ///
        /// The blade is aimed, not the controller. They differ by the player's own grip
        /// rotation -- around 47 degrees of pitch here -- which is exactly the amount that
        /// would put every swing above the notes.
        /// </remarks>
        private static void Aim(VRController controller, float side, float t)
        {
            if (controller == null)
            {
                return;
            }

            // Unrelated rates on the two axes, so the target crosses the whole grid instead
            // of retracing one path and only ever meeting the same few notes.
            var phase = side > 0 ? 0f : 1.7f;
            var target = new Vector3(
                Mathf.Lerp(GridX.x, GridX.y, 0.5f + 0.5f * Mathf.Sin(2.7f * t + phase)),
                Mathf.Lerp(GridY.x, GridY.y, 0.5f + 0.5f * Mathf.Sin(1.9f * t + phase * 0.6f)),
                0f);

            // Behind the note plane and out to the side, so the blade crosses z = 0 partway
            // along its length rather than reaching the notes with its very tip.
            var shoulder = new Vector3(side * 0.28f, 1.25f, -0.45f);

            var look = Quaternion.LookRotation(target - shoulder, Vector3.up);
            if (!controller.TryGetControllerOffset(out var offset))
            {
                offset = Pose.identity;
            }

            // saber = controller * offset, so to put the saber where `look` wants it the
            // controller has to sit back by the offset, both ways round.
            var rotation = look * Quaternion.Inverse(offset.rotation);
            controller.transform.SetPositionAndRotation(
                shoulder - rotation * offset.position, rotation);
        }
    }
}
