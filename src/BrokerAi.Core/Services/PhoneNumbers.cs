namespace BrokerAi.Core.Services;

/// <summary>Phone-number formatting helpers.</summary>
public static class PhoneNumbers
{
    /// <summary>
    /// Normalizes a Mexican WhatsApp id to a dialable E.164 number (no '+').
    /// WhatsApp reports Mexican senders as 521XXXXXXXXXX (legacy mobile '1'),
    /// but the dialable number — for contact cards and wa.me links — is
    /// 52XXXXXXXXXX. Non-Mexican numbers pass through unchanged.
    /// </summary>
    public static string ToDialableMx(string number) =>
        number.StartsWith("521") && number.Length == 13
            ? "52" + number[3..]
            : number;
}
