using System;
using System.IO;
using UnityEngine;

namespace ReplayFit
{
    /// <summary>Where this mod keeps its own files, inside the game's UserData.</summary>
    internal static class Paths
    {
        private static string _dataDir;

        /// <summary>
        /// <c>&lt;install&gt;/UserData/ReplayFit</c>.
        /// </summary>
        /// <remarks>
        /// Derived from <c>Application.dataPath</c> rather than the working directory: BSIPA
        /// launches the game from its own folder in some configurations, and a recorder that
        /// silently writes somewhere else is indistinguishable from one that never ran.
        /// </remarks>
        internal static string DataDir
        {
            get
            {
                if (_dataDir == null)
                {
                    var userData = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", "UserData"));
                    _dataDir = Path.Combine(userData, "ReplayFit");
                    Inherit(Path.Combine(userData, "ControllerAutoAdjust"), _dataDir);
                    Directory.CreateDirectory(_dataDir);
                }
                return _dataDir;
            }
        }

        /// <summary>
        /// Take over the folder this mod used under its previous name.
        /// </summary>
        /// <remarks>
        /// Renaming moved where everything is kept, and one of those files cannot be made
        /// again. The cut cache rebuilds from the replays and the assigned ranges can be
        /// re-entered, but the offset journal is a record of when the settings changed, built
        /// up as it happened; lose it and every replay played before the rename becomes a run
        /// whose grip nobody can name.
        ///
        /// Moved rather than copied, so this happens once and a later run does not resurrect
        /// a folder the player has since cleared. If anything goes wrong the old folder is
        /// left exactly where it was, which is the outcome worth protecting.
        /// </remarks>
        private static void Inherit(string old, string current)
        {
            try
            {
                if (!Directory.Exists(old) || Directory.Exists(current))
                {
                    return;
                }
                Directory.Move(old, current);
                Plugin.Log.Info($"carried over the data folder from {Path.GetFileName(old)}");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn(
                    $"could not carry over the old data folder, starting fresh: {e.Message}");
            }
        }
    }
}
