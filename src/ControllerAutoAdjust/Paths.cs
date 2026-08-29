using System.IO;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>Where this mod keeps its own files, inside the game's UserData.</summary>
    internal static class Paths
    {
        private static string _dataDir;

        /// <summary>
        /// <c>&lt;install&gt;/UserData/ControllerAutoAdjust</c>.
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
                    _dataDir = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", "UserData", "ControllerAutoAdjust"));
                    Directory.CreateDirectory(_dataDir);
                }
                return _dataDir;
            }
        }
    }
}
