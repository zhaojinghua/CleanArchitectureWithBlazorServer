﻿using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Domain.Features.Identity;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.IdentityModel.Tokens;

namespace CleanArchitecture.Blazor.Infrastructure.Services.JWT;
public class AccessTokenProvider : IAccessTokenProvider
{
    private readonly string _tokenKey = nameof(_tokenKey);
    private readonly string _refreshTokenKey = nameof(_refreshTokenKey);
    private readonly ProtectedLocalStorage _localStorage;
    private readonly ILoginService _loginService;
    private readonly IAccessTokenValidator _tokenValidator;
    private readonly IRefreshTokenValidator _refreshTokenValidator;
    private readonly IAccessTokenGenerator _tokenGenerator;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }

    public AccessTokenProvider(IServiceScopeFactory scopeFactory, 
        ProtectedLocalStorage localStorage,
        ILoginService loginService, 
        IAccessTokenValidator tokenValidator, 
        IRefreshTokenValidator refreshTokenValidator,
        IAccessTokenGenerator tokenGenerator,
        ITenantProvider tenantProvider,
        ICurrentUserService currentUser)
    {
        var scope = scopeFactory.CreateScope();
        _userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        _localStorage = localStorage;
        _loginService = loginService;
        _tokenValidator = tokenValidator;
        _refreshTokenValidator = refreshTokenValidator;
        _tokenGenerator = tokenGenerator;
        _tenantProvider = tenantProvider;
        _currentUser = currentUser;
    }
    public async Task Login(ApplicationUser applicationUser)
    {
        var token = await _loginService.LoginAsync(applicationUser);
        await  _localStorage.SetAsync(_tokenKey, token);
        AccessToken = token.AccessToken;
        RefreshToken = token.RefreshToken;
        SetUserPropertiesFromApplicationUser(applicationUser);
    }
    public async Task<ClaimsPrincipal> GetClaimsPrincipal()
    {
        try
        {
            var token = await _localStorage.GetAsync<AuthenticatedUserResponse>(_tokenKey);
            if (token.Success && token.Value is not null)
            {
                AccessToken = token.Value.AccessToken;
                RefreshToken = token.Value.RefreshToken;
                var validationResult = await _tokenValidator.ValidateTokenAsync(AccessToken!);
                if (validationResult.IsValid)
                {
                    
                    return SetUserPropertiesFromClaims(validationResult.ClaimsIdentity);
                }
                else
                {
                    var validationRefreshResult = await _refreshTokenValidator.ValidateTokenAsync(RefreshToken!);
                    if (validationRefreshResult.IsValid)
                    {

                        return SetUserPropertiesFromClaims(validationRefreshResult.ClaimsIdentity);
                    }

                }
            }
        }
        catch (CryptographicException)
        {
            await RemoveAuthDataFromStorage();
        }
        catch (Exception)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
        return new ClaimsPrincipal(new ClaimsIdentity());
    }

    private ClaimsPrincipal SetUserPropertiesFromClaims(ClaimsIdentity identity)
    {
        var principal = new ClaimsPrincipal(identity);
        _tenantProvider.TenantId = principal.GetTenantId();
        _tenantProvider.TenantName = principal.GetTenantName();
        _currentUser.UserId = principal.GetUserId();
        _currentUser.UserName = principal.GetUserName();
        _currentUser.TenantId = principal.GetTenantId();
        _currentUser.TenantName = principal.GetTenantName();  // This seems to be an error in original code. Fixing it here.
        return principal;
    }

    private void SetUserPropertiesFromApplicationUser(ApplicationUser user)
    {
        _tenantProvider.TenantId = user.TenantId;
        _tenantProvider.TenantName = user.TenantName;
        _currentUser.UserId = user.Id;
        _currentUser.UserName = user.UserName;
        _currentUser.TenantId = user.TenantId;
        _currentUser.TenantName = user.TenantName;
    }
    public ValueTask RemoveAuthDataFromStorage() => _localStorage.DeleteAsync(_tokenKey);

    public async Task<string> Refresh(string refreshToken)
    {
        TokenValidationResult validationResult = await _tokenValidator.ValidateTokenAsync(refreshToken!);
        if (!validationResult.IsValid)
        {
            throw validationResult.Exception;
        }
        JwtSecurityToken? jwt = validationResult.SecurityToken as JwtSecurityToken;
        string userId = jwt!.Claims.First(claim => claim.Type == "id").Value;
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            throw new Exception($"no found user by userId:{userId}");
        }
        string accessToken = await _tokenGenerator.GenerateAccessToken(user);
        return accessToken;
    }
}
