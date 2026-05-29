using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Identity;

namespace Mazda.Application.Modules.Identity;

public record AuthResponse(Guid UserId, string Email, string DisplayName, string Token);

// ── Register ──────────────────────────────────────────────────────
public record RegisterCommand(string Email, string Password, string? DisplayName) : IRequest<Result<AuthResponse>>;

public class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
    }
}

public class RegisterHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IJwtIssuer jwt,
    IUnitOfWork uow) : IRequestHandler<RegisterCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(RegisterCommand req, CancellationToken ct)
    {
        var existing = await users.FindByEmailAsync(req.Email, ct);
        if (existing is not null) return Result.Failure<AuthResponse>(Error.Conflict("Email already registered."));

        var user = User.Create(req.Email, req.DisplayName ?? string.Empty, hasher.Hash(req.Password));
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        var token = jwt.Issue(user.Id, user.Email);
        return Result.Success(new AuthResponse(user.Id, user.Email, user.DisplayName, token));
    }
}

// ── Login ─────────────────────────────────────────────────────────
public record LoginCommand(string Email, string Password) : IRequest<Result<AuthResponse>>;

public class LoginHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IJwtIssuer jwt) : IRequestHandler<LoginCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(LoginCommand req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email, ct);
        if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
            return Result.Failure<AuthResponse>(Error.Validation("Invalid email or password."));

        var token = jwt.Issue(user.Id, user.Email);
        return Result.Success(new AuthResponse(user.Id, user.Email, user.DisplayName, token));
    }
}
