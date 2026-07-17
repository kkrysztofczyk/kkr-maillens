using System.Security.Cryptography;

namespace KKR.MailLens;

/// <summary>
/// Wyprowadzanie klucza SQLCipher. 2FA: PIN + odpowiedz YubiKey (challenge-response HMAC-SHA1).
/// Klucz = PBKDF2-SHA256(PIN [+ ":" + hex(odpowiedz YubiKey)], sol, 200k). Bez PINu I bez klucza
/// nie da sie zderywowac. Sol NIE jest tajna (chroni przed rainbow-tables) i sluzy tez za wyzwanie YubiKey.
/// </summary>
static class Crypto
{
    const int Pbkdf2Iterations = 200_000;

    /// <summary>Czyta istniejaca sol. RZUCA gdy brak - unlock/derive NIE tworzy soli (to by
    /// nieodwracalnie przewiazalo istniejacy korpus na nowy, zly klucz). Sol tworzy tylko Setup.Init.</summary>
    public static byte[] ReadSaltOrThrow()
    {
        string path = Paths.SaltFile;
        if (!File.Exists(path))
            throw new FileNotFoundException("Brak salt.bin - korpus niezainicjowany albo uszkodzony. Uzyj Inicjuj.", path);
        return File.ReadAllBytes(path);
    }

    /// <summary>Losuje NOWA sol w PAMIECI (nie zapisuje). Zapis dopiero po sukcesie przez WriteSalt.</summary>
    public static byte[] NewSaltBytes() => RandomNumberGenerator.GetBytes(16);

    /// <summary>Utrwala sol na dysku (wolane przez Setup.Init dopiero po zbudowaniu nowej bazy).</summary>
    public static void WriteSalt(byte[] salt)
    {
        Directory.CreateDirectory(Paths.Base);
        File.WriteAllBytes(Paths.SaltFile, salt);
    }

    /// <summary>Klucz SQLCipher (hex 64 znakow = 32 bajty). yubiResponse=null => sam PIN; podane => 2FA.
    /// Ten sam PIN (+ ta sama odpowiedz YubiKey) + sol => ten sam klucz. Uzywa istniejacej soli (read-only).</summary>
    public static string DeriveKeyHex(string pin, byte[]? yubiResponse = null)
        => DeriveKeyHex(pin, ReadSaltOrThrow(), yubiResponse);

    /// <summary>Wariant z jawna sola (Setup.Init - sol jeszcze nie na dysku).</summary>
    public static string DeriveKeyHex(string pin, byte[] salt, byte[]? yubiResponse)
    {
        string material = yubiResponse is null || yubiResponse.Length == 0
            ? pin
            : pin + ":" + Convert.ToHexString(yubiResponse);
        using var kdf = new Rfc2898DeriveBytes(material, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return Convert.ToHexString(kdf.GetBytes(32));
    }
}

/// <summary>Tryb czynnika: "pin" albo "pin+yubi". Sticky - raz ustawiony przez unlock --yubi, obowiazuje dalej.</summary>
static class Mode
{
    public static string Read() => File.Exists(Paths.ModeFile) ? File.ReadAllText(Paths.ModeFile).Trim() : "";
    public static void Write(string m) { Directory.CreateDirectory(Paths.Base); File.WriteAllText(Paths.ModeFile, m); }
    public static bool Yubi => Read() == "pin+yubi";
}
