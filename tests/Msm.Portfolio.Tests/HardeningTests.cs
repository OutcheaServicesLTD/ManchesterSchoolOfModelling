using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Controllers;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The hardening from specification section 43.
/// </summary>
/// <remarks>
/// A rate limit policy that is defined but never attached to an endpoint looks exactly
/// like one that works, right up until the endpoint is attacked. These assert the
/// attachment, not just the definition.
/// </remarks>
public class HardeningTests
{
    public static TheoryData<Type, string, Type[], string> LimitedEndpoints => new()
    {
        // Sign-in guards every staff account, and Identity's lockout is per account: it
        // does nothing about one source working through a list of addresses.
        { typeof(AccountController), nameof(AccountController.Login),
          [typeof(Msm.Portfolio.Web.ViewModels.LoginViewModel)], RateLimitPolicies.SignIn },

        // Anonymous, and writes a client record.
        { typeof(OnboardingController), nameof(OnboardingController.Index),
          [typeof(Msm.Portfolio.Web.ViewModels.OnboardingViewModel), typeof(CancellationToken)],
          RateLimitPolicies.AnonymousForm },

        // The consent token is the only authorisation a guardian has, so guessing at it
        // is limited on the way in as well as on submission.
        { typeof(GuardianController), nameof(GuardianController.Approve),
          [typeof(string), typeof(CancellationToken)], RateLimitPolicies.AnonymousForm },

        { typeof(GuardianController), nameof(GuardianController.Approve),
          [typeof(string), typeof(Msm.Portfolio.Web.ViewModels.GuardianApprovalViewModel),
           typeof(CancellationToken)],
          RateLimitPolicies.AnonymousForm },

        // Completely anonymous and writes to the database, which makes it the most
        // attractive endpoint on the site to abuse.
        { typeof(PublicPortfolioController), nameof(PublicPortfolioController.Enquire),
          [typeof(string), typeof(Msm.Portfolio.Web.ViewModels.EnquiryViewModel),
           typeof(CancellationToken)],
          RateLimitPolicies.PublicEnquiry },

        { typeof(WebhookController), nameof(WebhookController.GoCardless),
          [typeof(CancellationToken)], RateLimitPolicies.Webhook }
    };

    [Theory]
    [MemberData(nameof(LimitedEndpoints))]
    public void The_endpoint_carries_its_rate_limit_policy(
        Type controller, string action, Type[] parameters, string expectedPolicy)
    {
        var method = controller.GetMethod(action, parameters);

        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(expectedPolicy, attribute!.PolicyName);
    }

    [Fact]
    public void Every_policy_name_is_distinct()
    {
        string[] policies =
        [
            RateLimitPolicies.SignIn,
            RateLimitPolicies.AnonymousForm,
            RateLimitPolicies.PublicEnquiry,
            RateLimitPolicies.Webhook
        ];

        Assert.Equal(policies.Length, policies.Distinct().Count());
    }

    [Fact]
    public void The_content_security_policy_denies_inline_script()
    {
        // Styles carry an inline exception because the gallery sets each image's aspect
        // ratio to stop the page jumping. Scripts must never acquire the same exception:
        // that is the directive doing nearly all of the work here.
        var policy = SecurityHeaders.BuildContentSecurityPolicy(isDevelopment: false);

        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-eval'", policy);
    }

    [Fact]
    public void The_content_security_policy_refuses_framing_and_plugins()
    {
        var policy = SecurityHeaders.BuildContentSecurityPolicy(isDevelopment: false);

        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("base-uri 'self'", policy);
        Assert.Contains("form-action 'self'", policy);
    }

    [Fact]
    public void The_content_security_policy_allows_no_external_origin()
    {
        var policy = SecurityHeaders.BuildContentSecurityPolicy(isDevelopment: false);

        // Everything is served from this application, including media, so any http(s)
        // origin appearing here would be an accident rather than a requirement.
        Assert.DoesNotContain("http://", policy);
        Assert.DoesNotContain("https://", policy);
    }

    [Fact]
    public void Insecure_requests_are_upgraded_outside_development()
    {
        Assert.Contains(
            "upgrade-insecure-requests", SecurityHeaders.BuildContentSecurityPolicy(isDevelopment: false));

        // Left off locally, where the developer exception page and hot reload use plain
        // HTTP resources.
        Assert.DoesNotContain(
            "upgrade-insecure-requests", SecurityHeaders.BuildContentSecurityPolicy(isDevelopment: true));
    }
}
