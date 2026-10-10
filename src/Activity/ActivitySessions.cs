using Microsoft.AspNetCore.DataProtection;

namespace THOBOTTO.Activity;

// Who is using an Activity: a short-lived token the page sends with each request (cookies don't work reliably
// inside Discord's frame), protected with the data protection keys.
public sealed class ActivitySessions(IDataProtectionProvider protection)
{
    private static readonly TimeSpan Lasts = TimeSpan.FromHours(12);
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("THOBOTTO.Activity").ToTimeLimitedDataProtector();

    public string Issue(ulong userId) => _protector.Protect(userId.ToString(), Lasts);

    public ulong? UserOf(string? session)
    {
        if (string.IsNullOrEmpty(session))
            return null;
        try
        {
            return ulong.TryParse(_protector.Unprotect(session), out var userId) ? userId : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
