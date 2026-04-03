using System.Threading.Tasks;
using ODDGames.UIAutomation;
using UnityEngine;

namespace clibridge4unity.Commands
{
    public static class UIActionCommand
    {
        [BridgeCommand("UIACTION", "Execute a UI automation action (JSON format)",
            Category = "UIAutomation",
            Usage = "UIACTION {\"action\":\"click\", \"text\":\"Settings\"}\n" +
                    "  UIACTION {\"action\":\"type\", \"name\":\"InputField\", \"value\":\"hello\"}\n" +
                    "  UIACTION {\"action\":\"swipe\", \"direction\":\"left\"}\n" +
                    "  UIACTION {\"action\":\"wait\", \"seconds\":2}\n" +
                    "  UIACTION {\"action\":\"key\", \"key\":\"escape\"}\n" +
                    "  UIACTION {\"action\":\"drag\", \"from\":{\"name\":\"A\"}, \"to\":{\"name\":\"B\"}}\n" +
                    "  UIACTION {\"action\":\"dropdown\", \"name\":\"DD\", \"option\":2}",
            RequiresMainThread = false,
            TimeoutSeconds = 30)]
        public static async Task<string> UIAction(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return Response.Error("Expected JSON. Example: {\"action\":\"click\", \"text\":\"Settings\"}");

            // ActionExecutor must start on the main thread (accesses Time.frameCount, Input System, etc.)
            // Use RunOnMainThreadAsync to kick it off, then let it manage its own async continuations
            var tcs = new TaskCompletionSource<ActionResult>();
            await CommandRegistry.RunOnMainThreadAsync<int>(() =>
            {
                // Start the async operation on main thread — continuations will marshal back as needed
                ActionExecutor.Execute(data).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        tcs.SetException(t.Exception.InnerException ?? t.Exception);
                    else
                        tcs.SetResult(t.Result);
                });
                return 0;
            });

            var result = await tcs.Task;

            if (!result.Success)
                return Response.Error($"{result.Error} ({result.ElapsedMs:F0}ms)");

            return Response.Success($"OK ({result.ElapsedMs:F0}ms)");
        }
    }
}
