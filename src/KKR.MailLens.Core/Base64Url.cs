namespace KKR.MailLens;

static class Base64Url
{
    public static byte[] Decode(string? value, string invalidMessage = "Nieprawidłowe dane Base64URL.")
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        try { return Convert.FromBase64String(normalized); }
        catch (FormatException ex) { throw new InvalidDataException(invalidMessage, ex); }
    }
}
