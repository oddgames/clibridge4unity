using System;
using System.Runtime.InteropServices;

namespace clibridge4unity;

/// <summary>
/// The desktop prompt the play-mode gate raises: a real Windows dialog with action buttons,
/// shown to whoever owns the running play session.
///
/// Uses TaskDialogIndirect (comctl32 v6) rather than a toast notification. A toast from an
/// unpackaged single-file exe needs an AppUserModelID plus a Start Menu shortcut, and its
/// buttons need either a registered COM activator or a URI protocol handler in the registry —
/// setup that can silently rot and leaves buttons dead. TaskDialog needs only the manifest
/// dependency already declared, works the first time on any machine, and cannot be missed
/// the way a toast that fired while you were elsewhere can.
///
/// The dialog is best-effort. If it cannot be shown — no interactive desktop, comctl32
/// missing, a service context — the caller falls back to waiting on the request file, which
/// any other surface (CLI, editor window) can answer.
/// </summary>
internal static class GateNotify
{
    // TaskDialogIndirect callback and config, trimmed to the fields used here.
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
        public IntPtr hMainIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszVerificationText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedInformation;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedControlText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszCollapsedControlText;
        public IntPtr hFooterIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    const uint TDF_USE_COMMAND_LINKS = 0x0010;
    const uint TDF_POSITION_RELATIVE_TO_WINDOW = 0x1000;
    const uint TDCBF_CANCEL_BUTTON = 0x0008;
    const int IDCANCEL = 2;

    // Button ids — arbitrary, just distinct from the standard ones.
    const int ID_RUN_NOW = 101;
    const int ID_YIELD = 102;
    const int ID_DENY = 103;
    const int ID_ALWAYS = 104;

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG pTaskConfig,
                                         out int pnButton, IntPtr pnRadioButton,
                                         out int pfVerificationFlagChecked);

    /// <summary>
    /// Ask the owner what to do. Returns a PlayGate decision, or null if the dialog could
    /// not be shown or was dismissed without choosing.
    /// <paramref name="remember"/> is true when the owner ticked "don't ask again", meaning
    /// the answer should stand for the rest of this play session instead of being asked
    /// again on the very next command.
    /// </summary>
    public static string Ask(PlayGate.Request request, string ownerLabel, out bool remember)
    {
        remember = false;
        try
        {
            var buttons = new[]
            {
                new TASKDIALOG_BUTTON
                {
                    nButtonID = ID_RUN_NOW,
                    pszButtonText = "Run it now in my play session\n" +
                                    "Play mode keeps running. Best for CODE_EXEC — it carries " +
                                    "its own compiler and does not need edit mode.",
                },
                new TASKDIALOG_BUTTON
                {
                    nButtonID = ID_YIELD,
                    pszButtonText = "Exit play mode and let the agent take over\n" +
                                    "Ends your play session, then runs the command.",
                },
                new TASKDIALOG_BUTTON
                {
                    nButtonID = ID_ALWAYS,
                    pszButtonText = "Always allow this window\n" +
                                    "Stop asking for this window entirely, across play sessions. " +
                                    "Revoke later with  clibridge4unity REQUESTS --forget.",
                },
                new TASKDIALOG_BUTTON
                {
                    nButtonID = ID_DENY,
                    pszButtonText = "Not now\n" +
                                    "The agent is told no and changes nothing.",
                },
            };

            int size = Marshal.SizeOf<TASKDIALOG_BUTTON>();
            IntPtr array = Marshal.AllocHGlobal(size * buttons.Length);
            try
            {
                for (int i = 0; i < buttons.Length; i++)
                    Marshal.StructureToPtr(buttons[i], array + (i * size), false);

                var config = new TASKDIALOGCONFIG
                {
                    cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                    dwFlags = TDF_USE_COMMAND_LINKS | TDF_POSITION_RELATIVE_TO_WINDOW,
                    dwCommonButtons = TDCBF_CANCEL_BUTTON,
                    pszWindowTitle = "Unity bridge",
                    pszMainInstruction = "An agent wants to use the editor",
                    pszContent =
                        $"The editor is in play mode — {ownerLabel}.\n\n" +
                        $"Window {request.From} wants to run:\n{request.Summary()}",
                    pszExpandedControlText = "Details",
                    pszCollapsedControlText = "Details",
                    pszExpandedInformation =
                        $"Request {request.Id}\nFrom process {request.FromPid}\n\n" +
                        "Answer later from any terminal:\n" +
                        $"  clibridge4unity ALLOW {request.Id}\n" +
                        $"  clibridge4unity ALLOW {request.Id} yield\n" +
                        $"  clibridge4unity DENY {request.Id}",
                    pszVerificationText = "Don't ask again while this play session lasts",
                    cButtons = (uint)buttons.Length,
                    pButtons = array,
                    nDefaultButton = ID_RUN_NOW,
                };

                int pressed;
                int verified;
                int hr = TaskDialogIndirect(ref config, out pressed, IntPtr.Zero, out verified);
                if (hr != 0) return null;          // no interactive desktop, or comctl32 unavailable
                remember = verified != 0;

                return pressed switch
                {
                    ID_RUN_NOW => PlayGate.RunNow,
                    ID_YIELD => PlayGate.Yield,
                    ID_ALWAYS => PlayGate.Always,
                    ID_DENY => PlayGate.Deny,
                    IDCANCEL => null,              // dismissed — leave it pending for another surface
                    _ => null,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(array);
            }
        }
        catch
        {
            // Never let the prompt itself break the command path.
            return null;
        }
    }
}
