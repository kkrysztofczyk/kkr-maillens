using Yubico.YubiKey;
using Yubico.YubiKey.Otp;

namespace KKR.MailLens;

/// <summary>
/// Drugi czynnik: YubiKey OTP challenge-response (HMAC-SHA1, slot 2 / LongPress) BEZ wymogu dotyku.
/// Slot skonfigurowany `ykman otp chalresp -g -f 2` (bez -t) - YubiKey 4 (stary fw) + SDK timeoutuja
/// na oknie require-touch, wiec 2FA = klucz wpiety + PIN (bez gestu). Uzywa SDK Yubico.YubiKey (jedna
/// sesja urzadzenia w procesie). Sekret zostaje na kluczu; my wysylamy tylko wyzwanie (= sol).
/// </summary>
static class YubiKey
{
    static IYubiKeyDevice? Find()
    {
        try
        {
            foreach (var d in YubiKeyDevice.FindAll())
                if (d.AvailableUsbCapabilities.HasFlag(YubiKeyCapabilities.Otp)) return d;
            return YubiKeyDevice.FindAll().FirstOrDefault();
        }
        catch { return null; }
    }

    public static bool TryInfo(out string info)
    {
        var d = Find();
        if (d is null) { info = "nie znaleziono"; return false; }
        info = $"fw {d.FirmwareVersion}, serial {(d.SerialNumber?.ToString() ?? "?")}";
        return true;
    }

    /// <summary>HMAC-SHA1 odpowiedz slotu 2 na wyzwanie. Slot bez require-touch, wiec zwraca od razu
    /// (onTouch zwykorzystywany tylko gdyby slot mial gest - u nas nie ma).</summary>
    public static byte[] ChallengeResponse(byte[] challenge, Action? onTouch = null)
    {
        var dev = Find() ?? throw new InvalidOperationException("Nie znaleziono YubiKey (OTP).");
        using var otp = new OtpSession(dev);
        var op = otp.CalculateChallengeResponse(Slot.LongPress)
            .UseChallenge(challenge)
            .UseYubiOtp(false); // false = HMAC-SHA1 challenge-response
        if (onTouch != null) op = op.UseTouchNotifier(onTouch);
        return op.GetDataBytes().ToArray();
    }
}
