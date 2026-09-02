using System;
using UnityEditor;

namespace clibridge4unity
{
    /// <summary>
    /// Who put the editor into play mode: the person at the keyboard, or an agent through
    /// the bridge — and which agent.
    ///
    /// Unity does not report this. `playModeStateChanged` says the state changed, never why.
    /// The only way to know is to record intent immediately before causing it: PLAY stamps a
    /// short-lived claim, and if the editor enters play mode without a fresh claim, a human
    /// pressed the button.
    ///
    /// Everything lives in SessionState because entering play mode can trigger a domain
    /// reload, which destroys statics — the reload happens between the claim and the state
    /// change we resolve it in.
    ///
    /// Ownership matters because one editor is shared: several agent windows and a person all
    /// drive it. STOP from an agent kills a play session someone else is watching, and until
    /// now nothing could tell the difference between "my session" and "someone else's".
    /// </summary>
    public static class PlayOwnership
    {
        /// <summary>
        /// How long a claim stays credible. Play entry is near-instant, so this only has to
        /// cover the hop from the command to the state change; anything older is a stale claim
        /// from an earlier session and must not be believed.
        /// </summary>
        const int ClaimWindowSeconds = 15;

        public const string User = "user";

        /// <summary>Called by PLAY immediately before it sets isPlaying.</summary>
        public static void Claim(string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) agentId = "agent";
            SessionState.SetString(SessionKeys.PlayClaim,
                                   agentId.Trim() + "|" + DateTime.UtcNow.Ticks);
        }

        /// <summary>Resolve the claim when the transition actually happens.</summary>
        public static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                string owner = ConsumeClaim() ?? User;
                SessionState.SetString(SessionKeys.PlayOwner, owner);
                SessionState.SetString(SessionKeys.PlayOwnerSince, DateTime.UtcNow.Ticks.ToString());
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.EraseString(SessionKeys.PlayOwner);
                SessionState.EraseString(SessionKeys.PlayOwnerSince);
                SessionState.EraseString(SessionKeys.PlayClaim);
            }
        }

        static string ConsumeClaim()
        {
            string raw = SessionState.GetString(SessionKeys.PlayClaim, "");
            SessionState.EraseString(SessionKeys.PlayClaim);
            if (string.IsNullOrEmpty(raw)) return null;

            int split = raw.LastIndexOf('|');
            if (split <= 0) return null;
            if (!long.TryParse(raw.Substring(split + 1), out long ticks)) return null;

            var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (age.TotalSeconds < 0 || age.TotalSeconds > ClaimWindowSeconds) return null;
            return raw.Substring(0, split);
        }

        /// <summary>"user", or an agent id. Null when not in play mode.</summary>
        public static string Owner
        {
            get
            {
                if (!EditorApplication.isPlaying) return null;
                string owner = SessionState.GetString(SessionKeys.PlayOwner, "");
                // Play mode entered before the bridge loaded, or state lost: a person is the
                // only actor that could have done it without leaving a claim.
                return string.IsNullOrEmpty(owner) ? User : owner;
            }
        }

        public static bool IsUserOwned => Owner == User;

        public static DateTime? Since
        {
            get
            {
                string raw = SessionState.GetString(SessionKeys.PlayOwnerSince, "");
                return long.TryParse(raw, out long ticks)
                     ? new DateTime(ticks, DateTimeKind.Utc)
                     : (DateTime?)null;
            }
        }

        /// <summary>True when someone other than <paramref name="agentId"/> owns the session.</summary>
        public static bool OwnedByOther(string agentId)
        {
            string owner = Owner;
            if (owner == null) return false;
            if (owner == User) return true;
            return !string.Equals(owner, agentId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One line for PLAYMODE / STATUS.</summary>
        public static string Describe()
        {
            string owner = Owner;
            if (owner == null) return "owner: (not in play mode)";
            string age = "";
            var since = Since;
            if (since.HasValue)
            {
                var elapsed = DateTime.UtcNow - since.Value;
                age = elapsed.TotalMinutes >= 1
                    ? string.Format(" ({0:0}m ago)", elapsed.TotalMinutes)
                    : string.Format(" ({0:0}s ago)", elapsed.TotalSeconds);
            }
            return owner == User
                ? "owner: user (entered manually)" + age
                : "owner: agent " + owner + age;
        }
    }
}
