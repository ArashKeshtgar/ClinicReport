using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.Extensions.Options;
using Stripe;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Hosted mode: a clinic's plan, what it uses of it, and the way to Stripe, where it subscribes and manages its card.
/// The plan itself changes when Stripe says so (webhook), never on the way back from these pages.
/// </summary>
public static class BillingEndpoints
{
    public record Meter(int Used, int? Limit);
    public record BillingStatus(
        string Plan, string Status, bool OnTrial, DateTime? TrialEndsAt,
        Meter Users, Meter Services, Meter SignedReports, bool Configured, bool CanUpgrade, bool CanManage, bool SuspendedByOperator);
    public record RedirectTo(string Url);
    /// <param name="ByOperator">Suspended by the service, not for not paying: paying won't lift it.</param>
    public record BillingNotice(string Status, DateTime? Until, bool ByOperator);

    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsHosted())
            return;
        var admin = app.MapGroup("/api/billing").WithTags("Billing").RequireAuthorization(Permissions.UsersManage);

        // A bad signature is a 400 and a failure a 500, which Stripe retries; 200 means done, or nothing to do.
        app.MapPost(Tenancy.StripeWebhookPath, async (HttpContext http, StripeEvents events, IOptions<StripeOptions> stripe, CancellationToken ct) =>
        {
            if (!http.OnRootDomain())
                return Results.NotFound();
            if (string.IsNullOrEmpty(stripe.Value.WebhookSecret))
                throw new ReportRuleException("Billing is not set up on this server.", StatusCodes.Status503ServiceUnavailable);
            if (http.Request.Headers["Stripe-Signature"].ToString() is not { Length: > 0 } signature)
                return Results.BadRequest();
            // The exact bytes Stripe signed, so read before anything parses them.
            using var reader = new StreamReader(http.Request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            Event stripeEvent;
            try
            {
                // The webhook endpoint's API version is set at Stripe and need not be this library's.
                stripeEvent = EventUtility.ConstructEvent(payload, signature, stripe.Value.WebhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (StripeException)
            {
                return Results.BadRequest();
            }
            await events.ApplyAsync(stripeEvent, ct);
            return Results.Ok();
        }).AllowAnonymous().WithTags("Billing");

        // For everyone at the clinic, once signed in: not in /api/settings, which the sign-in page reads.
        app.MapGet("/api/billing/notice", (ITenantContext current) =>
            current.Tenant is { Status: TenantStatus.PastDue or TenantStatus.Suspended or TenantStatus.Cancelled } tenant
                ? Results.Ok(new BillingNotice(tenant.Status, tenant.DeadlineAt, tenant.Status == TenantStatus.Suspended && tenant.PastDueSince is null))
                : Results.NoContent()).RequireAuthorization().WithTags("Billing");

        admin.MapGet("/", async (ITenantContext current, PlanLimits limits, TimeProvider clock, IOptions<StripeOptions> stripe, CancellationToken ct) =>
        {
            var tenant = current.Tenant!;
            var plan = tenant.EffectivePlan(clock.GetUtcNow().UtcDateTime);
            var usage = await limits.UsageAsync(ct);
            var free = plan == Plans.Free;
            return new BillingStatus(
                plan, tenant.Status, tenant.Plan == Plans.Trial && !free, tenant.TrialEndsAt,
                new(usage.ActiveUsers, free ? PlanLimits.FreeUsers : null),
                new(usage.Services, free ? PlanLimits.FreeServices : null),
                new(usage.SignedThisMonth, free ? PlanLimits.FreeSignedReportsPerMonth : null),
                stripe.Value.Configured, tenant.StripeSubscriptionId is null, tenant.StripeCustomerId is not null,
                tenant.Status == TenantStatus.Suspended && tenant.PastDueSince is null);
        });

        admin.MapPost("/checkout", async (HttpContext http, ITenantContext current, PlanLimits limits, IBillingGateway gateway, IOptions<StripeOptions> stripe, CancellationToken ct) =>
        {
            RequireStripe(stripe.Value);
            var tenant = current.Tenant!;
            // A second subscription would charge the clinic twice; the portal changes the one it has.
            if (tenant.StripeSubscriptionId is not null)
                throw new ReportRuleException("This clinic already has a subscription. Manage it from the billing page.");
            var usage = await limits.UsageAsync(ct);
            var url = await gateway.CreateCheckoutUrlAsync(new CheckoutRequest(
                tenant.Slug, tenant.StripeCustomerId, tenant.OwnerEmail, usage.ActiveUsers,
                $"{BillingPage(http)}?checkout=success", $"{BillingPage(http)}?checkout=cancelled"), ct);
            return new RedirectTo(url);
        });

        admin.MapPost("/portal", async (HttpContext http, ITenantContext current, IBillingGateway gateway, IOptions<StripeOptions> stripe, CancellationToken ct) =>
        {
            RequireStripe(stripe.Value);
            if (current.Tenant!.StripeCustomerId is not { } customer)
                throw new ReportRuleException("This clinic has no subscription to manage yet.");
            return new RedirectTo(await gateway.CreatePortalUrlAsync(customer, BillingPage(http), ct));
        });
    }

    /// <summary>Back to the clinic's own subdomain: the host passed tenant resolution, so it is this tenant's.</summary>
    private static string BillingPage(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/billing";

    private static void RequireStripe(StripeOptions stripe)
    {
        if (!stripe.Configured)
            throw new ReportRuleException("Billing is not set up on this server.", StatusCodes.Status503ServiceUnavailable);
    }
}
