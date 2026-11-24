namespace ReviewGuard.Functions;

public sealed class WebhookOptions
{
    public const string SectionName = "ReviewGuard";

    /// <summary>Header the service hook sends. Value must match <see cref="WebhookSecret"/>.</summary>
    public const string SecretHeader = "X-ReviewGuard-Secret";

    /// <summary>Shared secret set in the service hook. In Azure it comes from Key Vault.</summary>
    public string WebhookSecret { get; set; } = "";
}
