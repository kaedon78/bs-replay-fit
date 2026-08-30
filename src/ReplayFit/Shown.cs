using System;

namespace ReplayFit
{
    /// <summary>
    /// Turns a stored moment into the one to show a player.
    /// </summary>
    /// <remarks>
    /// Everything this mod stores or compares is UTC, and stays that way: replay headers are
    /// UTC, the journal is UTC, and an assignment written on one side of a daylight-saving
    /// change has to still mean the same instant on the other. None of that is negotiable.
    ///
    /// What a player reads is a different question. "3 Feb 04:22" for a session they played
    /// at nine in the evening is not a date they can recognise as theirs, and recognising it
    /// is the entire job of the range list -- it is how they tell which sitting was which
    /// grip. So the conversion happens here, at the edge, on the way to the screen, and
    /// nowhere else.
    /// </remarks>
    internal static class Shown
    {
        /// <summary>The same instant, in whatever zone this machine is set to.</summary>
        /// <remarks>
        /// Unspecified is read as UTC rather than left alone. Everything written here is
        /// written UTC; a value that lost its kind in a round trip is still a UTC value, and
        /// treating it as local would shift it by the offset while looking untouched.
        /// </remarks>
        internal static DateTime At(DateTime when)
        {
            switch (when.Kind)
            {
                case DateTimeKind.Local:
                    return when;
                case DateTimeKind.Unspecified:
                    return DateTime.SpecifyKind(when, DateTimeKind.Utc).ToLocalTime();
                default:
                    return when.ToLocalTime();
            }
        }
    }
}
