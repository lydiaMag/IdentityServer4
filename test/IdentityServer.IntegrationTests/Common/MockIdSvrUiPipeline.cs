﻿// Copyright (c) Brock Allen & Dominick Baier. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.


using IdentityServer4.Extensions;
using IdentityServer4.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Linq;
using IdentityServer4.Services;
using System.Security.Claims;
using IdentityModel.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using FluentAssertions;
using System.Net;
using System.Net.Http;
using System;
using System.Threading;
using Microsoft.AspNetCore.Authentication;
using IdentityServer4.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace IdentityServer4.IntegrationTests.Common
{
    public class MockIdSvrUiPipeline : IdentityServerPipeline
    {
        public const string FederatedSignOutPath = "/signout-oidc";
        public const string FederatedSignOutUrl = "https://server" + FederatedSignOutPath;

        public RequestDelegate Login { get; set; }
        public RequestDelegate Logout { get; set; }
        public RequestDelegate Consent { get; set; }
        public RequestDelegate Error { get; set; }
        public RequestDelegate FederatedSignOut { get; set; }

        public BackChannelMessageHandler BackChannelMessageHandler { get; set; } = new BackChannelMessageHandler();

        public MockIdSvrUiPipeline()
        {
            Login = OnLogin;
            Logout = OnLogout;
            Consent = OnConsent;
            Error = OnError;
            FederatedSignOut = OnFederatedSignOut;

            this.OnPreConfigureServices += MockAuthorizationPipeline_OnPreConfigureServices;
            this.OnPostConfigureServices += MockAuthorizationPipeline_OnPostConfigureServices;
            this.OnPreConfigure += MockAuthorizationPipeline_OnPreConfigure;
            this.OnPostConfigure += MockAuthorizationPipeline_OnPostConfigure;
        }

        private void MockAuthorizationPipeline_OnPreConfigureServices(IServiceCollection services)
        {
            services.AddAuthentication()
                .AddCookie(cookies =>
                {
                    cookies.Cookie.Name = "cookie_authn";
                });
            // todo: fix when fix skipped FML tests
            //    .AddScheme<MockExternalProviderOptions, MockExternalProvider>("external", "External", options=> { });
            //services.AddTransient<MockExternalProvider>();
        }

        private void MockAuthorizationPipeline_OnPostConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(new BackChannelHttpClient(BackChannelMessageHandler));
        }

        private void MockAuthorizationPipeline_OnPreConfigure(IApplicationBuilder app)
        {
        }

        private void MockAuthorizationPipeline_OnPostConfigure(IApplicationBuilder app)
        {
            app.Map(Constants.UIConstants.DefaultRoutePaths.Login.EnsureLeadingSlash(), path =>
            {
                path.Run(ctx => Login(ctx));
            });

            app.Map(Constants.UIConstants.DefaultRoutePaths.Logout.EnsureLeadingSlash(), path =>
            {
                path.Run(ctx => Logout(ctx));
            });

            app.Map(Constants.UIConstants.DefaultRoutePaths.Consent.EnsureLeadingSlash(), path =>
            {
                path.Run(ctx => Consent(ctx));
            });

            app.Map(Constants.UIConstants.DefaultRoutePaths.Error.EnsureLeadingSlash(), path =>
            {
                path.Run(ctx => Error(ctx));
            });
        }

        public bool LoginWasCalled { get; set; }
        public AuthorizationRequest LoginRequest { get; set; }
        public ClaimsPrincipal Subject { get; set; }
        public bool FollowLoginReturnUrl { get; set; }

        async Task OnLogin(HttpContext ctx)
        {
            LoginWasCalled = true;
            await ReadLoginRequest(ctx);
            await IssueLoginCookie(ctx);
        }

        async Task ReadLoginRequest(HttpContext ctx)
        {
            var interaction = ctx.RequestServices.GetRequiredService<IIdentityServerInteractionService>();
            LoginRequest = await interaction.GetAuthorizationContextAsync(ctx.Request.Query["returnUrl"].FirstOrDefault());
        }

        async Task IssueLoginCookie(HttpContext ctx)
        {
            if (Subject != null)
            {
                var props = new AuthenticationProperties();
                await ctx.SignInAsync(Subject, props);
                Subject = null;
                var url = ctx.Request.Query[this.Options.UserInteraction.LoginReturnUrlParameter].FirstOrDefault();
                if (url != null)
                {
                    ctx.Response.Redirect(url);
                }
            }
        }

        public bool LogoutWasCalled { get; set; }
        public LogoutRequest LogoutRequest { get; set; }

        async Task OnLogout(HttpContext ctx)
        {
            LogoutWasCalled = true;
            await ReadLogoutRequest(ctx);
        }

        private async Task ReadLogoutRequest(HttpContext ctx)
        {
            var interaction = ctx.RequestServices.GetRequiredService<IIdentityServerInteractionService>();
            LogoutRequest = await interaction.GetLogoutContextAsync(ctx.Request.Query["logoutId"].FirstOrDefault());
        }

        public bool ConsentWasCalled { get; set; }
        public AuthorizationRequest ConsentRequest { get; set; }
        public ConsentResponse ConsentResponse { get; set; }

        async Task OnConsent(HttpContext ctx)
        {
            ConsentWasCalled = true;
            await ReadConsentMessage(ctx);
            await CreateConsentResponse(ctx);
        }

        async Task ReadConsentMessage(HttpContext ctx)
        {
            var interaction = ctx.RequestServices.GetRequiredService<IIdentityServerInteractionService>();
            ConsentRequest = await interaction.GetAuthorizationContextAsync(ctx.Request.Query["returnUrl"].FirstOrDefault());
        }

        async Task CreateConsentResponse(HttpContext ctx)
        {
            if (ConsentRequest != null && ConsentResponse != null)
            {
                var interaction = ctx.RequestServices.GetRequiredService<IIdentityServerInteractionService>();
                await interaction.GrantConsentAsync(ConsentRequest, ConsentResponse);
                ConsentResponse = null;

                var url = ctx.Request.Query[this.Options.UserInteraction.ConsentReturnUrlParameter].FirstOrDefault();
                if (url != null)
                {
                    ctx.Response.Redirect(url);
                }
            }
        }

        public bool ErrorWasCalled { get; set; }
        public ErrorMessage ErrorMessage { get; set; }

        async Task OnError(HttpContext ctx)
        {
            ErrorWasCalled = true;
            await ReadErrorMessage(ctx);
        }

        async Task ReadErrorMessage(HttpContext ctx)
        {
            var interaction = ctx.RequestServices.GetRequiredService<IIdentityServerInteractionService>();
            ErrorMessage = await interaction.GetErrorContextAsync(ctx.Request.Query["errorId"].FirstOrDefault());
        }

        Task OnFederatedSignOut(HttpContext ctx)
        {
            // simulate an external authentication handler signing out
            ctx.SignOutAsync();

            return Task.FromResult(0);
        }

        /* helpers */
        public async Task LoginAsync(ClaimsPrincipal subject)
        {
            var old = BrowserClient.AllowAutoRedirect;
            BrowserClient.AllowAutoRedirect = false;

            Subject = subject;
            await BrowserClient.GetAsync(LoginPage);

            BrowserClient.AllowAutoRedirect = old;
        }

        public async Task LoginAsync(string subject)
        {
            var user = Users.Single(x => x.SubjectId == subject);
            var name = user.Claims.Where(x => x.Type == "name").Select(x => x.Value).FirstOrDefault() ?? user.Username;
            await LoginAsync(IdentityServerPrincipal.Create(subject, name));
        }

        public void RemoveLoginCookie()
        {
            BrowserClient.RemoveCookie("https://server/", IdentityServerConstants.DefaultCookieAuthenticationScheme);
        }
        public void RemoveSessionCookie()
        {
            BrowserClient.RemoveCookie("https://server/", IdentityServerConstants.DefaultCheckSessionCookieName);
        }
        public Cookie GetSessionCookie()
        {
            return BrowserClient.GetCookie("https://server/", IdentityServerConstants.DefaultCheckSessionCookieName);
        }

        public string CreateAuthorizeUrl(
            string clientId,
            string responseType,
            string scope = null,
            string redirectUri = null,
            string state = null,
            string nonce = null,
            string loginHint = null,
            string acrValues = null,
            string responseMode = null,
            string codeChallenge = null,
            string codeChallengeMethod = null,
            object extra = null)
        {
            var url = new AuthorizeRequest(AuthorizeEndpoint).CreateAuthorizeUrl(
                clientId: clientId,
                responseType: responseType,
                scope: scope,
                redirectUri: redirectUri,
                state: state,
                nonce: nonce,
                loginHint: loginHint,
                acrValues: acrValues,
                responseMode: responseMode,
                codeChallenge: codeChallenge,
                codeChallengeMethod: codeChallengeMethod,
                extra: extra);
            return url;
        }

        public IdentityModel.Client.AuthorizeResponse ParseAuthorizationResponseUrl(string url)
        {
            return new IdentityModel.Client.AuthorizeResponse(url);
        }

        public async Task<IdentityModel.Client.AuthorizeResponse> RequestAuthorizationEndpointAsync(
            string clientId,
            string responseType,
            string scope = null,
            string redirectUri = null,
            string state = null,
            string nonce = null,
            string loginHint = null,
            string acrValues = null,
            string responseMode = null,
            string codeChallenge = null,
            string codeChallengeMethod = null,
            object extra = null)
        {
            var old = BrowserClient.AllowAutoRedirect;
            BrowserClient.AllowAutoRedirect = false;

            var url = CreateAuthorizeUrl(clientId, responseType, scope, redirectUri, state, nonce, loginHint, acrValues, responseMode, codeChallenge, codeChallengeMethod, extra);
            var result = await BrowserClient.GetAsync(url);
            result.StatusCode.Should().Be(HttpStatusCode.Found);

            BrowserClient.AllowAutoRedirect = old;

            var redirect = result.Headers.Location.ToString();
            if (redirect.StartsWith(IdentityServerPipeline.ErrorPage))
            {
                // request error page in pipeline so we can get error info
                await BrowserClient.GetAsync(redirect);
                
                // no redirect to client
                return null;
            }

            return new IdentityModel.Client.AuthorizeResponse(redirect);
        }
    }

    public class BackChannelMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task> OnInvoke { get; set; }
        public HttpResponseMessage Response { get; set; } = new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (OnInvoke != null)
            {
                await OnInvoke.Invoke(request);
            }
            return Response;
        }
    }

    public class MockExternalProviderOptions : AuthenticationSchemeOptions { }

    public class MockExternalProvider : AuthenticationHandler<MockExternalProviderOptions>, 
        IAuthenticationSignInHandler, 
        IAuthenticationRequestHandler
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        protected MockExternalProvider(
            IHttpContextAccessor httpContextAccessor,
            IOptionsMonitor<MockExternalProviderOptions> options,
            ILoggerFactory logger, 
            UrlEncoder encoder, 
            ISystemClock clock) 
            : base(options, logger, encoder, clock)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<bool> HandleRequestAsync()
        {
            if (_httpContextAccessor.HttpContext.Request.Path == MockIdSvrUiPipeline.FederatedSignOutPath)
            {
                await _httpContextAccessor.HttpContext.SignOutAsync();
                return true;
            }

            return false;
        }

        public Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties properties)
        {
            return Task.CompletedTask;
        }

        public Task SignOutAsync(AuthenticationProperties properties)
        {
            return Task.CompletedTask;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            throw new NotImplementedException();
        }
    }
}