using System;
using System.IO;
using System.Text;

namespace ControllerAutoAdjust
{
    /// <summary>The few answers only the player can give.</summary>
    internal static class Preferences
    {
        internal const string FileName = "preferences.txt";

        /// <summary>
        /// Whether replays from before the journal were played on the settings in force now.
        /// </summary>
        /// <remarks>
        /// Unanswerable from the files themselves -- nothing in a replay records the offsets
        /// -- and the difference matters: months of history either count or they do not.
        /// Asking is the only way to know, and <see cref="Unanswered"/> is kept distinct from
        /// "no" so that a recommendation built on an unconfirmed assumption can say so rather
        /// than presenting itself as settled.
        /// </remarks>
        internal enum PriorReplays { Unanswered, SameConfig, DifferentConfig }

        private static string Path => System.IO.Path.Combine(Paths.DataDir, FileName);

        internal static PriorReplays PriorReplayAnswer
        {
            get
            {
                try
                {
                    if (!File.Exists(Path))
                    {
                        return PriorReplays.Unanswered;
                    }
                    foreach (var line in File.ReadAllLines(Path))
                    {
                        var text = line.Trim();
                        if (!text.StartsWith("priorReplays=", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var value = text.Substring("priorReplays=".Length).Trim();
                        if (value.Equals("same", StringComparison.OrdinalIgnoreCase))
                        {
                            return PriorReplays.SameConfig;
                        }
                        if (value.Equals("different", StringComparison.OrdinalIgnoreCase))
                        {
                            return PriorReplays.DifferentConfig;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"could not read preferences: {e.Message}");
                }
                return PriorReplays.Unanswered;
            }
            set
            {
                try
                {
                    var word = value == PriorReplays.SameConfig ? "same"
                        : value == PriorReplays.DifferentConfig ? "different" : "unanswered";
                    File.WriteAllText(
                        Path,
                        "# Were the replays recorded before this mod was installed played on\n" +
                        "# the controller settings in force now? same | different | unanswered\n" +
                        $"priorReplays={word}\n",
                        new UTF8Encoding(false));
                    Plugin.Log.Info($"preference saved: priorReplays={word}");
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"could not write preferences: {e.Message}");
                }
            }
        }
    }
}
