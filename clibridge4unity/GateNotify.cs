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

    const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    const uint TDF_POSITION_RELATIVE_TO_WINDOW = 0x1000;

    // Button ids — arbitrary, just distinct from the standard ones.
    const int ID_ALLOW = 101;
    const int ID_SESSION = 102;
    const int ID_ALWAYS = 103;
    const int ID_DENY = 104;

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG pTaskConfig,
                                         out int pnButton, IntPtr pnRadioButton,
                                         out int pfVerificationFlagChecked);

    /// <summary>
    /// Ask the owner what to do. Returns a PlayGate decision, or null if the dialog could not
    /// be shown. Closing it counts as Deny. Four plain buttons and a two-line body: it pops up
    /// in the middle of someone's play session, so it has to be answerable at a glance.
    /// "Exit play mode and hand over" is deliberately not here — `ALLOW <id> yield` covers it.
    /// </summary>
    public static string Ask(PlayGate.Request request, string startedBy)
    {
        try
        {
            var buttons = new[]
            {
                new TASKDIALOG_BUTTON { nButtonID = ID_ALLOW, pszButtonText = "Allow" },
                new TASKDIALOG_BUTTON { nButtonID = ID_SESSION, pszButtonText = "Allow until play stops" },
                new TASKDIALOG_BUTTON { nButtonID = ID_ALWAYS, pszButtonText = "Always allow" },
                new TASKDIALOG_BUTTON { nButtonID = ID_DENY, pszButtonText = "Deny" },
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
                    dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_POSITION_RELATIVE_TO_WINDOW,
                    pszWindowTitle = "Unity bridge",
                    pszMainInstruction = "An agent wants to run " + request.Command,
                    pszContent = $"{Describe(request)}\nPlay mode (started by {startedBy}) keeps running.",
                    cButtons = (uint)buttons.Length,
                    pButtons = array,
                    nDefaultButton = ID_ALLOW,
                };

                int hr = TaskDialogIndirect(ref config, out int pressed, IntPtr.Zero, out _);
                if (hr != 0) return null;          // no interactive desktop, or comctl32 unavailable

                return pressed switch
                {
                    ID_ALLOW => PlayGate.RunNow,
                    ID_SESSION => PlayGate.Session,
                    ID_ALWAYS => PlayGate.Always,
                    _ => PlayGate.Deny,            // Deny, Esc, or the close box
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

    /// <summary>The argument, shortened: a script path shows as its file name.</summary>
    static string Describe(PlayGate.Request request)
    {
        string data = (request.Data ?? "").Trim();
        if (data.Length == 0) return request.Command;
        try { if (System.IO.File.Exists(data)) return System.IO.Path.GetFileName(data); } catch { }
        data = data.Replace("\r", " ").Replace("\n", " ");
        return data.Length > 80 ? data.Substring(0, 77) + "..." : data;
    }
}
