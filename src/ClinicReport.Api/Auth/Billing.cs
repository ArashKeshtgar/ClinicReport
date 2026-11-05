using ClinicReport.Api.Reports;
using Microsoft.Extensions.Options;
using Stripe;

namespace ClinicReport.Api.Auth;

/// <summary>Hosted mode: Stripe, in test mode only. The secrets come from /run/secrets/Stripe__SecretKey and Stripe__WebhookSecret.</summary>
public class StripeOptions
{
    public const string TestKeyPrefix = "sk_test_";

    public string? SecretKey { get; set; }
    public string? WebhookSecret { get; set; }

    /// <summary>The Pro price in Stripe, per user per month.</summary>
    public string? ProPriceId { get; set; }

    public bool Configured => !string.IsNullOrEmpty(SecretKey) && !string.IsNullOrEmpty(ProPriceId);

    /// <summary>This project takes no real payments: anything but a test key stops the start-up.</summary>
    public void EnsureTestKey()
    {
        if (!string.IsNullOrEmpty(SecretKey) && !SecretKey.StartsWith(TestKeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Stripe:SecretKey must be a test key ({TestKeyPrefix}...).");
    }
}

/// <summary>Who is subscribing. Only the slug and the owner's email go to Stripe; nothing from a clinic's database.</summary>
public record CheckoutRequest(string Slug, string? CustomerId, string? Email, int Seats, string SuccessUrl, string CancelUrl);

public interface IBillingGateway
{
    /// <summary>The Stripe page where the clinic subscribes to Pro.</summary>
    Task<string> CreateCheckoutUrlAsync(CheckoutRequest request, CancellationToken ct = default);

    /// <summary>The Stripe page where the clinic changes its card or cancels.</summary>
    Task<string> CreatePortalUrlAsync(string customerId, string returnUrl, CancellationToken ct = default);

    /// <summary>Sets how many users the subscription bills for; Stripe prorates the change.</summary>
    Task UpdateSeatsAsync(string subscriptionId, int seats, CancellationToken ct = default);
}

public sealed class StripeBillingGateway(IOptions<StripeOptions> options, ILogger<StripeBillingGateway> logger, HttpMessageHandler? handler = null) : IBillingGateway
{
    /// <summary>Like the mail timeout: a request must not hang on a server that never answers.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // Made at the first call: a server without Stripe has no key to make it with, and StripeClient refuses an empty one.
    private readonly Lazy<StripeClient> _client = new(() => new StripeClient(options.Value.SecretKey,
        httpClient: new SystemNetHttpClient(handler is null ? new HttpClient { Timeout = Timeout } : new HttpClient(handler) { Timeout = Timeout }, maxNetworkRetries: 1)));

    public Task<string> CreateCheckoutUrlAsync(CheckoutRequest request, CancellationToken ct = default) => CallAsync(async () =>
    {
        var session = await new Stripe.Checkout.SessionService(_client.Value).CreateAsync(new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            ClientReferenceId = request.Slug,
            Customer = request.CustomerId,
            // Stripe takes one or the other.
            CustomerEmail = request.CustomerId is null ? request.Email : null,
            LineItems = [new() { Price = options.Value.ProPriceId, Quantity = request.Seats }],
            // On the subscription too: its later events don't carry the checkout's reference.
            SubscriptionData = new() { Metadata = new() { ["tenant"] = request.Slug } },
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
        }, cancellationToken: ct);
        return session.Url;
    });

    public Task<string> CreatePortalUrlAsync(string customerId, string returnUrl, CancellationToken ct = default) => CallAsync(async () =>
    {
        var session = await new Stripe.BillingPortal.SessionService(_client.Value).CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions { Customer = customerId, ReturnUrl = returnUrl }, cancellationToken: ct);
        return session.Url;
    });

    public Task UpdateSeatsAsync(string subscriptionId, int seats, CancellationToken ct = default) => CallAsync(async () =>
    {
        var service = new SubscriptionService(_client.Value);
        var subscription = await service.GetAsync(subscriptionId, cancellationToken: ct);
        var item = subscription.Items.Data.Single();
        if (item.Quantity != seats)
            await service.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions { Items = [new() { Id = item.Id, Quantity = seats }] }, cancellationToken: ct);
        return subscriptionId;
    });

    private async Task<string> CallAsync(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException or TaskCanceledException)
        {
            // The type and code only: Stripe's message can quote what was sent, such as the owner's email.
            logger.LogWarning("Stripe call failed: {Error} {Code}", ex.GetType().Name, (ex as StripeException)?.StripeError?.Code);
            throw new ReportRuleException("Stripe could not be reached. Try again in a moment.", StatusCodes.Status502BadGateway);
        }
    }
}
