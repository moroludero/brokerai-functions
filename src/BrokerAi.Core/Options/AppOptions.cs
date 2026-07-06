namespace BrokerAi.Core.Options;

public sealed class MetaOptions
{
    public const string Section = "Meta";
    public string AccessToken { get; set; } = "";
    public string WebhookVerifyToken { get; set; } = "";
    public string GraphApiVersion { get; set; } = "v21.0";
}

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-4-5";
}

public sealed class FacebookOptions
{
    public const string Section = "Facebook";
    public string PageId { get; set; } = "";
    public string PageAccessToken { get; set; } = "";
    public string AdAccountId { get; set; } = "";
    public double AdMarkupPercent { get; set; } = 20;
}

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Shared pilot bot number (E.164 without '+'), used for wa.me QR links when the broker has no dedicated number.</summary>
    public string SharedWhatsAppNumber { get; set; } = "";

    /// <summary>Owner's personal WhatsApp for the admin weekly digest.</summary>
    public string OwnerAlertNumber { get; set; } = "";

    public string BlobContainerName { get; set; } = "property-images";
}
