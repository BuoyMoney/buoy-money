namespace BuoyAI.Core.Configuration;

/// <summary>
/// The product name people read in emails, push text, the MCP server, the authenticator app and the AI's own wording.
/// Display only: namespaces, the database name, the JWT issuer and audience, the passkey relying-party id, hosts and OAuth
/// scopes keep their old spelling because changing them would sign people out or orphan stored data.
///
/// The default is "Buoy Money". A self-hoster who wants their own name sets <c>Brand:Name</c> (env <c>Brand__Name</c>);
/// <see cref="Configure"/> runs once at startup, before anything is sent. Text written with the default name is rewritten
/// to the configured one by <see cref="Apply"/> where it leaves the server (email), so a template never needs the setting.
/// </summary>
public static class Brand
{
    public const string DefaultName = "Buoy Money";

    public static string Name { get; private set; } = DefaultName;

    public static void Configure(string? name) =>
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();

    /// <summary>Swaps the default name in <paramref name="text"/> for the configured one. A no-op for the default.</summary>
    public static string Apply(string text) =>
        Name == DefaultName || string.IsNullOrEmpty(text) ? text : text.Replace(DefaultName, Name, StringComparison.Ordinal);
}
