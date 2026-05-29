using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Identity;

public class User : Entity
{
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;

    private User() { } // EF

    public static User Create(string email, string displayName, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email required.");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new ArgumentException("Password hash required.");
        return new User
        {
            Email = email.Trim().ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim(),
            PasswordHash = passwordHash
        };
    }

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        DisplayName = displayName.Trim();
        Touch();
    }
}
