using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;

namespace ClinicReport.Api.Auth;

/// <summary>
/// Hosted mode: what Stripe's webhook events do to a tenant. Stripe may send an event twice and in any order, so an
/// event already processed changes nothing, and neither does one older than the last that did. The event's row and the
/// tenant's change are saved together: a failure saves neither, and Stripe's retry finds the event still to do.
/// </summary>
public sealed class StripeEvents(CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock, ILogger<StripeEvents> logger)
{
    private const string Checkout = "checkout.session.completed";
    private const string Updated = "customer.subscription.updated";
    private const string Deleted = "customer.subscription.deleted";
    private const string PaymentFailed = "invoice.payment_failed";

    public async Task ApplyAsync(Event stripeEvent, CancellationToken ct)
    {
        // This project takes no real payments, whatever a key or endpoint is set to.
        if (stripeEvent.Livemode)
        {
            logger.LogWarning("Ignored live Stripe event {Event}", stripeEvent.Id);
            return;
        }
        if (await catalog.ProcessedStripeEvents.AnyAsync(e => e.Id == stripeEvent.Id, ct))
            return;

        var tenant = await FindTenantAsync(stripeEvent, ct);
        if (tenant is null)
            logger.LogWarning("No tenant for Stripe event {Event} ({Type})", stripeEvent.Id, stripeEvent.Type);
        else if (tenant.LastStripeEventAt is null || stripeEvent.Created >= tenant.LastStripeEventAt)
        {
            Apply(stripeEvent, tenant);
            tenant.LastStripeEventAt = stripeEvent.Created;
        }
        catalog.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            Id = stripeEvent.Id, Type = stripeEvent.Type, ProcessedAt = clock.GetUtcNow().UtcDateTime,
        });
        try
        {
            await catalog.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same event, delivered twice at once: the other delivery has done the work.
            catalog.ChangeTracker.Clear();
            if (await catalog.ProcessedStripeEvents.AnyAsync(e => e.Id == stripeEvent.Id, ct))
                return;
            throw;
        }
        if (tenant is not null)
            directory.Forget(tenant.Slug);
    }

    private async Task<Tenant?> FindTenantAsync(Event stripeEvent, CancellationToken ct)
    {
        switch (stripeEvent.Type, stripeEvent.Data.Object)
        {
            case (Checkout, Session session):
                return await catalog.Tenants.SingleOrDefaultAsync(t => t.Slug == session.ClientReferenceId, ct);
            case (Updated or Deleted, Subscription subscription):
                // By the slug first: this can arrive before the checkout that told us the customer.
                if (subscription.Metadata.TryGetValue("tenant", out var slug))
                    return await catalog.Tenants.SingleOrDefaultAsync(t => t.Slug == slug, ct);
                return await ByCustomerAsync(subscription.CustomerId, ct);
            case (PaymentFailed, Invoice invoice):
                return await ByCustomerAsync(invoice.CustomerId, ct);
            default:
                return null;
        }
    }

    private Task<Tenant?> ByCustomerAsync(string customerId, CancellationToken ct) =>
        catalog.Tenants.SingleOrDefaultAsync(t => t.StripeCustomerId == customerId, ct);

    private void Apply(Event stripeEvent, Tenant tenant)
    {
        switch (stripeEvent.Data.Object)
        {
            case Session session:
                Subscribe(tenant, session.CustomerId, session.SubscriptionId);
                break;
            // A subscription the clinic has since replaced says nothing about the clinic.
            case Subscription subscription when tenant.StripeSubscriptionId is not null && tenant.StripeSubscriptionId != subscription.Id:
                break;
            case Subscription subscription when stripeEvent.Type == Deleted:
                tenant.StripeSubscriptionId = null;
                tenant.Plan = Plans.Free;
                tenant.Status = TenantStatus.Cancelled;
                tenant.CancelledAt = clock.GetUtcNow().UtcDateTime;
                tenant.PastDueSince = null;
                break;
            case Subscription { Status: "active" or "trialing" } subscription:
                Subscribe(tenant, subscription.CustomerId, subscription.Id);
                break;
            case Subscription { Status: "past_due" or "unpaid" }:
            case Invoice when tenant.StripeSubscriptionId is not null:
                MarkPastDue(tenant);
                break;
        }
    }

    private static void Subscribe(Tenant tenant, string customerId, string subscriptionId)
    {
        tenant.Plan = Plans.Pro;
        tenant.Status = TenantStatus.Active;
        tenant.StripeCustomerId = customerId;
        tenant.StripeSubscriptionId = subscriptionId;
        tenant.CancelledAt = null;
        tenant.PastDueSince = null;
    }

    /// <summary>The first failure starts the grace period; those after it don't restart it.</summary>
    private void MarkPastDue(Tenant tenant)
    {
        if (tenant.Status is not (TenantStatus.Active or TenantStatus.PastDue))
            return;
        tenant.Status = TenantStatus.PastDue;
        tenant.PastDueSince ??= clock.GetUtcNow().UtcDateTime;
    }
}
