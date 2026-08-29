using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Records, per scored cut, the five numbers an offset can act on.
    /// </summary>
    /// <remarks>
    /// The offline version of this reconstructed each note's centre from the grid and a
    /// fitted note speed, which is where its two worst traps lived -- chain links are not at
    /// the grid cell their id names, and the replay's timing error runs positive when early,
    /// opposite to how it reads. Neither survives here: <c>NoteCutInfo.notePosition</c> is
    /// the note's actual centre, so the signed distance is exact rather than reconstructed.
    ///
    /// That gives a free self-check. The game reports <c>cutDistanceToCenter</c> for the same
    /// cut, and it must equal the magnitude of the signed distance computed here. Offline
    /// that check carried 5-10 mm of reconstruction error; here any disagreement at all means
    /// this file is wrong.
    ///
    /// The sign is the point of the exercise. An offset that rescues a cut which passed left
    /// of centre ruins one that passed right, so a magnitude alone -- which is all the game
    /// displays -- cannot say which way to move.
    /// </remarks>
    internal class CutRecorder : MonoBehaviour
    {
        private const float LookForControllerEvery = 1f;

        private ScoreController _controller;
        private Saber _left;
        private Saber _right;
        private float _nextLook;

        /// <summary>Geometry captured at the cut, waiting for the multiplier that only
        /// arrives once the follow-through has been scored.</summary>
        private readonly Dictionary<ScoringElement, Pending> _inFlight =
            new Dictionary<ScoringElement, Pending>();

        private readonly List<string> _lines = new List<string>();
        private string _sessionFile;

        private struct Pending
        {
            public float Time;
            public int Hand, Column, Row, Direction, ScoringType;
            public float Signed, AcrossX, AcrossY, Lever, ReportedDistance;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextLook)
            {
                return;
            }
            _nextLook = Time.unscaledTime + LookForControllerEvery;

            // No Zenject dependency for this: one lookup a second while idle is cheaper than
            // taking on SiraUtil before the shape of the mod is settled.
            var found = Live<ScoreController>();
            if (found == _controller)
            {
                return;
            }
            Detach();
            if (found != null)
            {
                Attach(found);
            }
        }

        private static T Live<T>() where T : UnityEngine.Object
        {
            // FindObjectsOfTypeAll also returns prefabs and assets, which are not in a scene.
            return Resources.FindObjectsOfTypeAll<T>()
                .FirstOrDefault(o => o is Component c && c.gameObject.scene.isLoaded);
        }

        private void Attach(ScoreController controller)
        {
            var sabers = Resources.FindObjectsOfTypeAll<Saber>()
                .Where(s => s.gameObject.scene.isLoaded)
                .ToList();
            _left = sabers.FirstOrDefault(s => s.saberType == SaberType.SaberA);
            _right = sabers.FirstOrDefault(s => s.saberType == SaberType.SaberB);
            if (_left == null || _right == null)
            {
                Plugin.Log.Warn("found a ScoreController but not both sabers; not recording");
                return;
            }

            _controller = controller;
            _controller.scoringForNoteStartedEvent += OnCut;
            _controller.scoringForNoteFinishedEvent += OnScored;
            _sessionFile = null;
            Plugin.Log.Info("recording cuts");
        }

        private void Detach()
        {
            if (_controller != null)
            {
                _controller.scoringForNoteStartedEvent -= OnCut;
                _controller.scoringForNoteFinishedEvent -= OnScored;
                _controller = null;
            }
            _inFlight.Clear();
            Flush();
        }

        private void OnCut(ScoringElement element)
        {
            if (!(element is GoodCutScoringElement good))
            {
                return;
            }
            try
            {
                var cut = good.cutScoreBuffer.noteCutInfo;
                var saber = cut.saberType == SaberType.SaberA ? _left : _right;
                if (saber == null)
                {
                    return;
                }

                var normal = cut.cutNormal.normalized;
                var toCentre = cut.notePosition - cut.cutPoint;

                // Where the blade was, in the saber's own frame: the offset is expressed in
                // that frame, so the plane normal has to be too.
                var rotation = saber.transform.rotation;
                var across = Quaternion.Inverse(rotation) * normal;
                var blade = (saber.saberBladeTopPos - saber.saberBladeBottomPos).normalized;

                var note = element.noteData;
                _inFlight[element] = new Pending
                {
                    Time = note.time,
                    Hand = cut.saberType == SaberType.SaberA ? 0 : 1,
                    Column = note.lineIndex,
                    Row = (int)note.noteLineLayer,
                    Direction = (int)note.cutDirection,
                    ScoringType = (int)note.scoringType,
                    Signed = Vector3.Dot(toCentre, normal),
                    AcrossX = across.x,
                    AcrossY = across.y,
                    // How far along the blade the note sat, which is what scales a rotation's
                    // effect where a translation's is flat.
                    Lever = Vector3.Dot(cut.notePosition - saber.transform.position, blade),
                    ReportedDistance = cut.cutDistanceToCenter,
                };
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"capture failed: {e.Message}");
            }
        }

        private void OnScored(ScoringElement element)
        {
            if (!_inFlight.TryGetValue(element, out var p))
            {
                return;
            }
            _inFlight.Remove(element);

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"t\":{0:F4},\"hand\":{1},\"col\":{2},\"row\":{3},\"dir\":{4},\"st\":{5}," +
                "\"signed\":{6:F6},\"mx\":{7:F6},\"my\":{8:F6},\"lever\":{9:F5}," +
                "\"mult\":{10},\"dist\":{11:F6}}}",
                p.Time, p.Hand, p.Column, p.Row, p.Direction, p.ScoringType,
                p.Signed, p.AcrossX, p.AcrossY, p.Lever, element.multiplier, p.ReportedDistance);
            _lines.Add(line);

            if (_lines.Count >= 200)
            {
                Flush();
            }
        }

        internal void Flush()
        {
            if (_lines.Count == 0)
            {
                return;
            }
            try
            {
                if (_sessionFile == null)
                {
                    var dir = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", "UserData", "ControllerAutoAdjust"));
                    Directory.CreateDirectory(dir);
                    _sessionFile = Path.Combine(
                        dir, $"cuts-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                }
                File.AppendAllText(_sessionFile, string.Join("\n", _lines) + "\n", Encoding.UTF8);
                Plugin.Log.Info($"wrote {_lines.Count} cuts to {Path.GetFileName(_sessionFile)}");
                _lines.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write cuts: {e.Message}");
            }
        }

        private void OnDestroy() => Detach();
    }
}
