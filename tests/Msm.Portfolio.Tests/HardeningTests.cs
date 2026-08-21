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
    public void A_preview_deployment_asks_search_engines_to_stay_away()
    {
        // A demonstration site carrying invented models, on a subdomain of MSM's real
        // brand, must not turn up in a search for MSM — and un-indexing is slow.
        var headers = SecurityHeaders.RobotsHeaderFor(discourageSearchEngines: true);

        Assert.Equal("noindex, nofollow, noarchive, noimageindex", headers);
    }

    [Fact]
    public void The_live_site_says_nothing_to_search_engines()
    {
        // Portfolios are meant to be findable. Emitting noindex by accident would
        // quietly remove every model from search results.
        Assert.Null(SecurityHeaders.RobotsHeaderFor(discourageSearchEngines: false));
    }

    /// <summary>
    /// The enquiry form is the only route an agency has to a model, and it was silently
    /// rejecting every submission: the fields post as "Enquiry.Name" because they are
    /// rendered from the page's view model, and the action bound them without that
    /// prefix. Nothing failed loudly — the page simply came back asking for details that
    /// were already filled in.
    /// </summary>
    [Fact]
    public void The_enquiry_form_is_bound_under_the_prefix_it_posts_under()
    {
        var parameter = typeof(PublicPortfolioController)
            .GetMethod(nameof(PublicPortfolioController.Enquire))!
            .GetParameters()
            .Single(p => p.ParameterType == typeof(Msm.Portfolio.Web.ViewModels.EnquiryViewModel));

        var binding = parameter.GetCustomAttribute<Microsoft.AspNetCore.Mvc.BindAttribute>();

        Assert.NotNull(binding);

        // Tied to the property the form is rendered from, so renaming it breaks this
        // test rather than the enquiry form.
        Assert.Equal(
            nameof(Msm.Portfolio.Web.ViewModels.PublicPortfolioViewModel.Enquiry),
            binding!.Prefix);
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
