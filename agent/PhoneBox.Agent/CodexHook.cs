using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Codex PermissionRequest hook: Codex posts an approval request to the local agent and waits for the phone's
/// Allow / Deny decision. An empty response leaves the normal Codex approval prompt in place.
/// </summary>
public static class CodexHook
{
    public const string Endpoint = "/api/hooks/codex/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".codex", "hooks.json");

    public static Task<object?> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals,
        SessionMonitor sessions, CancellationToken aborted) =>
        ClaudeHook.HandlePermissionAsync(input, cfg, approvals, sessions, aborted, "Codex", "codex:", allowQuestions: false);

    public static bool Installed() => ClaudeHook.Installed(HooksPath, Endpoint);

    public static string? Configure(bool install, int port, int timeoutSec) =>
        ClaudeHook.Configure(install, port, timeoutSec, HooksPath, Endpoint);

    internal static bool Installed(string path) => ClaudeHook.Installed(path, Endpoint);

    internal static string? Configure(bool install, int port, int timeoutSec, string path) =>
        ClaudeHook.Configure(install, port, timeoutSec, path, Endpoint);
}
