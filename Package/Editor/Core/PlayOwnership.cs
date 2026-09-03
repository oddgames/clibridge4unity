using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

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
                // Three sources, most specific first:
                //   1. an explicit claim — the PLAY command said who it was
                //   2. a bridge command in flight — something like CODE_EXEC set isPlaying,
                //      so the transition belongs to whoever issued that command
                //   3. nothing — no agent was involved, so a person started it
                string owner = ConsumeClaim() ?? ActiveCaller() ?? User;
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

        /// <summary>
        /// How long an in-flight command marker is trusted for attribution. Deliberately much
        /// tighter than the ledger's own 300s ceiling: this is answering "was a command running
        /// at this instant", not "has this window been busy recently".
        /// </summary>
        const int ActiveMarkerWindowSeconds = 120;

        /// <summary>
        /// The window whose bridge command is executing right now, or null.
        ///
        /// The CLI writes `{project}/.clibridge4unity/peers/{id}.active` before sending any
        /// command and removes it after, so a play-mode transition that happens while one of
        /// those exists was caused by that window — whatever route it took. This is what makes
        /// `CODE_EXEC EditorApplication.isPlaying = true` attributable, when only the PLAY
        /// command can leave an explicit claim.
        ///
        /// Reading a CLI-side file from the package is a layering compromise, taken because the
        /// wire protocol has no field for caller identity and adding one would break older
        /// clients. Best-effort throughout: any failure just falls through to "user".
        /// </summary>
        static string ActiveCaller()
        {
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (string.IsNullOrEmpty(root)) return null;
                string peers = Path.Combine(root, ".clibridge4unity", "peers");
                if (!Directory.Exists(peers)) return null;

                string best = null;
                DateTime bestStarted = DateTime.MinValue;

                foreach (string file in Directory.GetFiles(peers, "*.active"))
                {
                    DateTime started = DateTime.MinValue;
                    int pid = 0;
                    foreach (string line in File.ReadAllLines(file))
                    {
                        if (line.StartsWith("startedAt=", StringComparison.Ordinal))
                            DateTime.TryParse(line.Substring(10), CultureInfo.InvariantCulture,
                                              DateTimeStyles.RoundtripKind, out started);
                        else if (line.StartsWith("pid=", StringComparison.Ordinal))
                            int.TryParse(line.Substring(4), out pid);
                    }

                    if (started == DateTime.MinValue) continue;
                    double age = (DateTime.UtcNow - started.ToUniversalTime()).TotalSeconds;
                    if (age < 0 || age > ActiveMarkerWindowSeconds) continue;
                    // A marker left behind by a crashed client must not claim the session.
                    if (pid > 0 && !ProcessAlive(pid)) continue;

                    if (started > bestStarted)
                    {
                        bestStarted = started;
                        best = Path.GetFileNameWithoutExtension(file);
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        static bool ProcessAlive(int pid)
        {
            try { System.Diagnostics.Process.GetProcessById(pid); return true; }
            catch { return false; }
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
            // Say what is actually known. "user" is an inference from the absence of any claim
            // and any in-flight command — not something the editor reports — so it must not be
            // stated as fact that a person pressed Play.
            return owner == User
                ? "owner: no agent claimed this session (Play button, or something outside the bridge)" + age
                : "owner: agent " + owner + age;
        }
    }
}
