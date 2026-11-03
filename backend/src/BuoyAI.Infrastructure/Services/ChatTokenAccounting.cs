namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Running token totals for one chat turn, which can take several model calls (tool use). Gemini reports the prompt tokens
/// and the output tokens (thinking included) of every call, and those are what the usage row gets. Only a call that came
/// back with no counts falls back to the old length/4 guess, and it marks the totals as estimated so the row says so.
/// </summary>
internal static class ChatTokenAccounting
{
    public static void AddCall(
        int? reportedInput, int promptEstimate, int? reportedOutput,
        ref int input, ref int output, ref bool estimated)
    {
        if (reportedInput is { } real) input += real;
        else { input += promptEstimate; estimated = true; }

        // A call with no reported output is counted by the caller from the text it produced (see the final-text branch).
        if (reportedOutput is { } realOutput) output += realOutput;
    }
}
