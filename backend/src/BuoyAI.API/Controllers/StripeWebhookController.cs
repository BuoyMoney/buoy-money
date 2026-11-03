using Microsoft.AspNetCore.Mvc;
using Stripe;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Webhook controller for handling Stripe events
/// </summary>
// NOTE: intentionally inherits ControllerBase, not BaseController (the repo convention).
// BaseController carries [EnableRateLimiting("api")] and user-context helpers (GetCurrentUserId).
// This endpoint is UNAUTHENTICATED (Stripe authenticates via webhook signature, not a user JWT),
// so the user helpers are irrelevant, and subjecting it to the shared "api" rate limiter could
// drop Stripe's aggressive event fan-out / retries. Keep it on ControllerBase deliberately.
[ApiController]
[Route("api/billing/webhook")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class StripeWebhookController : ControllerBase
{
    private readonly IStripeService _stripeService;
    private readonly IBillingService _billingService;
    private readonly INotificationService _notificationService;
    private readonly ISubscriptionSyncService _syncService;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IConfiguration _configuration;
    private readonly BuoyAiMetrics _metrics;
    private readonly IReservationService _reservationService;
    private readonly string? _environmentTag;

    public StripeWebhookController(
        IStripeService stripeService,
        IBillingService billingService,
        INotificationService notificationService,
        ISubscriptionSyncService syncService,
        ILogger<StripeWebhookController> logger,
        IConfiguration configuration,
        BuoyAiMetrics metrics,
        IReservationService reservationService,
        IHostEnvironment? hostEnvironment = null)
    {
        _environmentTag = hostEnvironment == null ? null : StripeEnvironmentPolicy.Tag(hostEnvironment.EnvironmentName);
        _stripeService = stripeService;
        _billingService = billingService;
        _notificationService = notificationService;
        _syncService = syncService;
        _logger = logger;
        _configuration = configuration;
        _metrics = metrics;
        _reservationService = reservationService;
    }

    /// <summary>
    /// Staging and production share one Stripe account and both receive its events. An object
    /// stamped for the OTHER environment names a user id that means someone else here, so it must
    /// never grant anything. Untagged objects (hosted plans from before the tag) are still handled.
    /// </summary>
    private bool IsForeignEnvironment(IDictionary<string, string>? metadata)
    {
        if (_environmentTag == null) return false;
        var owner = StripeEnvironmentPolicy.Owner(metadata);
        if (owner == null || string.Equals(owner, _environmentTag, StringComparison.OrdinalIgnoreCase)) return false;
        _logger.LogInformation("Ignoring Stripe object owned by environment {Owner}; this host is {Tag}", owner, _environmentTag);
        return true;
    }

    [HttpPost]
    public async Task<IActionResult> HandleWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        try
        {
            var stripeSignature = Request.Headers["Stripe-Signature"].ToString();
            var webhookSecret = _configuration["Stripe:WebhookSecret"];

            var stripeEvent = EventUtility.ConstructEvent(
                json,
                stripeSignature,
                webhookSecret
            );

            _logger.LogInformation("Received Stripe webhook: {EventType}", stripeEvent.Type);
            _metrics.RecordWebhookReceived(stripeEvent.Type);

            // Idempotency: Stripe delivers at-least-once and retries on any non-2xx, so
            // the same event id can arrive more than once. Skip anything already processed
            // to avoid double-inserting payment rows and re-sending emails.
            if (await _billingService.HasProcessedStripeEventAsync(stripeEvent.Id))
            {
                _logger.LogInformation(
                    "Stripe event {EventId} ({EventType}) already processed; acknowledging without re-running.",
                    stripeEvent.Id, stripeEvent.Type);
                return Ok();
            }

            // Handle different event types
            try
            {
                switch (stripeEvent.Type)
                {
                case "checkout.session.completed":
                    await HandleCheckoutSessionCompleted(stripeEvent);
                    break;

                case "charge.refunded":
                    await HandleChargeRefunded(stripeEvent);
                    break;

                case "customer.subscription.created":
                    await HandleSubscriptionCreated(stripeEvent);
                    break;

                case "customer.subscription.updated":
                    await HandleSubscriptionUpdated(stripeEvent);
                    break;

                case "customer.subscription.deleted":
                    await HandleSubscriptionDeleted(stripeEvent);
                    break;

                case "invoice.payment_succeeded":
                    await HandleInvoicePaymentSucceeded(stripeEvent);
                    break;

                case "invoice.payment_failed":
                    await HandleInvoicePaymentFailed(stripeEvent);
                    break;

                case "invoice.finalized":
                    await HandleInvoiceFinalized(stripeEvent);
                    break;

                // Cloud (modular) subscriptions: a paid invoice and any schedule change are
                // both reasons to re-read the subscription, which re-projects its products.
                case "invoice.paid":
                    await HandleInvoicePaid(stripeEvent);
                    break;

                case "subscription_schedule.created":
                case "subscription_schedule.updated":
                case "subscription_schedule.released":
                case "subscription_schedule.completed":
                case "subscription_schedule.canceled":
                case "subscription_schedule.aborted":
                    await HandleSubscriptionScheduleChanged(stripeEvent);
                    break;

                case "charge.dispute.created":
                    // Not acted on yet; surfaced in the logs so a dispute on a multi-product
                    // invoice is seen. Revenue reporting and dispute handling are tracked work.
                    _logger.LogWarning("Stripe dispute created: {EventId}", stripeEvent.Id);
                    break;

                case "customer.subscription.trial_will_end":
                    await HandleTrialWillEnd(stripeEvent);
                    break;

                // Cardless trials end here. Stripe pauses the subscription when the trial
                // runs out with no payment method attached, which is our "trial lapsed"
                // signal. Before trials were Stripe-backed there was no such signal at all
                // and access simply went stale.
                case "customer.subscription.paused":
                    await HandleSubscriptionPaused(stripeEvent);
                    break;

                // The customer came back and added a card, so the paused subscription is
                // live again.
                case "customer.subscription.resumed":
                    await HandleSubscriptionResumed(stripeEvent);
                    break;

                case "setup_intent.succeeded":
                    await HandleSetupIntentSucceeded(stripeEvent);
                    break;

                default:
                    _logger.LogInformation("Unhandled webhook event type: {EventType}", stripeEvent.Type);
                    break;
                }
            }
            catch (Exception ex)
            {
                // Handler threw (transient DB error, bug, etc.). Record a failure metric so a
                // systematically-failing handler is visible in Grafana, then rethrow so the
                // event is NOT marked processed and Stripe retries the delivery.
                _metrics.RecordWebhookFailed("handler", stripeEvent.Type);
                _logger.LogError(ex, "Stripe webhook handler failed for {EventType}", stripeEvent.Type);
                throw;
            }

            // Record only after successful processing. If a handler threw, it propagates
            // (→ non-2xx) without marking, so Stripe's retry re-processes the event.
            await _billingService.MarkStripeEventProcessedAsync(stripeEvent.Id, stripeEvent.Type);

            return Ok();
        }
        catch (StripeException ex)
        {
            // Signature verification failed — most commonly a rotated/mismatched
            // Stripe:WebhookSecret, which would otherwise silently halt ALL billing.
            _metrics.RecordWebhookFailed("signature", "unknown");
            _logger.LogError(ex, "Stripe webhook signature verification failed");
            return BadRequest("Invalid signature");
        }
    }

    private async Task HandleInvoicePaymentSucceeded(Event stripeEvent)
    {
        try
        {
            var invoice = stripeEvent.Data.Object as Stripe.Invoice;
            if (invoice == null)
            {
                _logger.LogWarning("Invoice payment succeeded event missing invoice data");
                return;
            }

            // In Stripe.NET v48+, subscription info is in Parent.SubscriptionDetails
            var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
            var amount = invoice.AmountPaid / 100m; // Stripe amounts are in cents

            if (string.IsNullOrEmpty(subscriptionId))
            {
                _logger.LogInformation("Invoice payment succeeded for non-subscription invoice {InvoiceId}", invoice.Id);
                return;
            }

            _logger.LogInformation("Invoice payment succeeded for subscription {SubscriptionId}, amount ${Amount}",
                subscriptionId, amount);

            // The payment-succeeded metric is recorded INSIDE HandleSuccessfulPaymentAsync, after
            // its StripeInvoiceId idempotency short-circuit, so a Stripe retry of an
            // already-processed invoice doesn't re-fire the metric without inserting a row.
            await _billingService.HandleSuccessfulPaymentAsync(subscriptionId, amount, invoice.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling successful payment webhook");
            throw;
        }
    }

    private async Task HandleInvoicePaymentFailed(Event stripeEvent)
    {
        try
        {
            var invoice = stripeEvent.Data.Object as Stripe.Invoice;
            if (invoice == null)
            {
                _logger.LogWarning("Invoice payment failed event missing invoice data");
                return;
            }

            // In Stripe.NET v48+, subscription info is in Parent.SubscriptionDetails
            var subscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;
            var failureReason = invoice.LastFinalizationError?.Message ?? "Payment failed";

            if (string.IsNullOrEmpty(subscriptionId))
            {
                _logger.LogWarning("Invoice payment failed for non-subscription invoice {InvoiceId}", invoice.Id);
                return;
            }

            _logger.LogWarning("Invoice payment failed for subscription {SubscriptionId}: {FailureReason}",
                subscriptionId, failureReason);

            // The payment-failed metric is recorded INSIDE HandleFailedPaymentAsync, after its
            // StripeInvoiceId idempotency short-circuit, so a Stripe retry of an already-processed
            // invoice doesn't re-fire the metric without inserting a row.
            await _billingService.HandleFailedPaymentAsync(subscriptionId, failureReason, invoice.Id);

            // Send notification to user about failed payment. This is a best-effort
            // side-effect AFTER the payment row + PastDue status have been committed, so a
            // notification failure must NOT bubble up and force Stripe to retry the whole
            // event (which would otherwise double-insert the failed-payment row).
            try
            {
                var userId = await _billingService.GetUserIdByStripeSubscriptionIdAsync(subscriptionId);
                if (userId.HasValue)
                {
                    await _notificationService.CreateNotificationAsync(
                        userId.Value,
                        new CreateNotificationRequest
                        {
                            Type = "payment_failed",
                            Title = "Payment Failed",
                            Message = "Your payment could not be processed. Please update your payment method to avoid service interruption.",
                            ActionUrl = "/billing",
                            ActionText = "Update Payment Method"
                        }
                    );
                }
            }
            catch (Exception notifyEx)
            {
                _logger.LogError(notifyEx,
                    "Failed to create payment-failed notification for subscription {SubscriptionId}; payment row already committed",
                    subscriptionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling failed payment webhook");
            throw;
        }
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (subscription?.Id == null)
            {
                _logger.LogWarning("Subscription updated event missing subscription ID");
                return;
            }

            _logger.LogInformation("Subscription {SubscriptionId} updated in Stripe to status: {Status}", 
                subscription.Id, subscription.Status);
            
            // Handle status change (trialing -> active, etc.)
            await _billingService.HandleSubscriptionStatusChangeAsync(subscription.Id, subscription.Status);

            // ...and project the full snapshot, which is what carries trial_end. Without this
            // a trial extended or shortened in the Stripe dashboard never reached us, and
            // access kept expiring on our stale date while Stripe believed otherwise.
            await ProjectAsync(subscription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling subscription updated webhook");
            throw;
        }
    }

    // A refund on a reservation payment marks that reservation refunded. Any other charge
    // is not ours to handle here, so it is a no-op.
    private async Task HandleChargeRefunded(Event stripeEvent)
    {
        var charge = stripeEvent.Data.Object as Stripe.Charge;
        // Partial refunds also send charge.refunded; only a full refund takes a reservation off the count.
        if (charge == null || !charge.Refunded || string.IsNullOrEmpty(charge.PaymentIntentId))
        {
            return;
        }

        var marked = await _reservationService.MarkRefundedAsync(charge.PaymentIntentId);
        if (marked)
        {
            _logger.LogInformation("Reservation refunded (payment intent {PaymentIntentId})", charge.PaymentIntentId);
            return;
        }

        // Stripe doesn't promise delivery order. A reservation refunded before its checkout event was
        // recorded: fail so this event is retried once the reservation exists, instead of being lost.
        if (await _reservationService.IsReservationPaymentAsync(charge.PaymentIntentId))
        {
            throw new InvalidOperationException(
                $"Refund for reservation payment {charge.PaymentIntentId} arrived before its checkout; retrying");
        }
    }

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent)
    {
        try
        {
            var session = stripeEvent.Data.Object as Stripe.Checkout.Session;

            // Founding-member $1 reservations are one-off payments that must never reach
            // the subscription path below.
            if (session?.Metadata != null
                && session.Metadata.TryGetValue("kind", out var kind)
                && kind == "reservation")
            {
                await _reservationService.RecordCompletedAsync(new CompletedReservationCheckout(
                    session.Id,
                    session.CustomerDetails?.Email ?? session.CustomerEmail,
                    session.CustomerId,
                    session.PaymentIntentId,
                    session.AmountTotal,
                    session.Currency,
                    session.Metadata));
                return;
            }

            if (session == null || session.Mode != "subscription")
            {
                _logger.LogInformation("Ignoring non-subscription checkout session {SessionId}", session?.Id);
                return;
            }

            _logger.LogInformation(
                "Checkout session {SessionId} completed for subscription {SubscriptionId} (payment_status: {PaymentStatus})",
                session.Id, session.SubscriptionId, session.PaymentStatus);

            // checkout.session.completed can fire before the charge actually settles
            // (async/delayed-payment, SCA-pending), where PaymentStatus is not yet "paid".
            // Passing null here would bypass ActivateSubscriptionFromCheckoutAsync's
            // active/trialing gate and grant premium before any successful charge. Translate
            // the session's payment status into a status the gate understands so activation
            // only happens once payment is collected; the customer.subscription.created/.updated
            // handlers reconcile the rest once Stripe reports active/trialing.
            var mappedStatus = session.PaymentStatus switch
            {
                "paid" => "active",
                "no_payment_required" => "trialing", // $0 / trial checkout
                null => "incomplete",
                _ => session.PaymentStatus
            };

            if (IsForeignEnvironment(session.Metadata)) return;

            await _billingService.ActivateSubscriptionFromCheckoutAsync(
                session.ClientReferenceId, session.SubscriptionId, session.CustomerId, session.Metadata, mappedStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling checkout.session.completed webhook");
            throw;
        }
    }

    private async Task HandleSubscriptionCreated(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            _logger.LogInformation(
                "Subscription {SubscriptionId} created in Stripe with status: {Status}", 
                subscription?.Id, subscription?.Status);
            
            if (subscription != null && !IsForeignEnvironment(subscription.Metadata))
            {
                // Real recurring price straight from Stripe (cents -> dollars), so the
                // recorded amount reflects the actual configured price rather than a
                // hardcoded value.
                // All items count: a Cloud subscription bills several products at once.
                decimal? actualAmount = null;
                // A metered item (Cloud AI pay-as-you-go) has no fixed price to add up.
                var priced = subscription.Items?.Data
                    ?.Where(i => i.Price?.UnitAmount != null && !BuoyAI.Infrastructure.Services.StripeSnapshotMapper.IsMetered(i)).ToList();
                if (priced is { Count: > 0 })
                {
                    actualAmount = priced.Sum(i => i.Price.UnitAmount!.Value * Math.Max(i.Quantity, 1)) / 100m;
                }

                // Hosted Checkout creates the subscription with no pre-existing DB row.
                // Activate from the subscription metadata we attached at session creation
                // (idempotent, no-op for /subscribe and card-upfront trials which already
                // have a row). Only activates on active/trialing status. This makes hosted
                // Checkout work using only the customer.subscription.created event.
                await _billingService.ActivateSubscriptionFromCheckoutAsync(
                    null, subscription.Id, subscription.CustomerId, subscription.Metadata,
                    subscription.Status, actualAmount);

                // Sync status for rows that already existed (card-upfront trials, etc.)
                await _billingService.HandleSubscriptionStatusChangeAsync(subscription.Id, subscription.Status);

                // A Cloud subscription's products only exist in the projection, so project now.
                // Hosted Standard and Premium subscriptions are untouched by this.
                if (BuoyAI.Infrastructure.Services.StripeSnapshotMapper.From(subscription).IsCatalog)
                {
                    await ProjectAsync(subscription);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling subscription created webhook");
            throw;
        }
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (subscription?.Id == null)
            {
                _logger.LogWarning("Subscription deleted event missing subscription ID");
                return;
            }

            _logger.LogInformation("Subscription {SubscriptionId} deleted/cancelled in Stripe", subscription.Id);
            
            await _billingService.CancelSubscriptionByStripeIdAsync(subscription.Id);

            // A Cloud subscription's products live in UserEntitlements, which the line above does not
            // know about. Project the ended subscription so they are removed with it.
            if (StripeSnapshotMapper.From(subscription).IsCatalog)
            {
                await ProjectAsync(subscription);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling subscription deleted webhook");
            throw;
        }
    }

    private async Task HandleInvoiceFinalized(Event stripeEvent)
    {
        try
        {
            _logger.LogInformation("Invoice finalized in Stripe");
            // Invoice is ready for payment
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling invoice finalized webhook");
            throw;
        }
    }

    private async Task HandleTrialWillEnd(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (subscription?.Id == null)
            {
                _logger.LogWarning("Trial will end event missing subscription ID");
                return;
            }

            // Handle trial ending in billing service (sends email)
            if (subscription.TrialEnd.HasValue)
            {
                await _billingService.HandleTrialWillEndAsync(subscription.Id, subscription.TrialEnd.Value);
            }

            // Find user by Stripe subscription ID for in-app notification
            var userId = await _billingService.GetUserIdByStripeSubscriptionIdAsync(subscription.Id);
            
            if (userId.HasValue)
            {
                // Send trial ending notification
                await _notificationService.CreateNotificationAsync(
                    userId.Value,
                    new CreateNotificationRequest
                    {
                        Type = "trial_ending",
                        Title = "Your trial is ending soon",
                        // Card-agnostic: trials are now cardless by default, so most
                        // recipients have nothing on file to charge. Telling them a card
                        // will be charged would be simply untrue.
                        Message = "Your free trial ends in 3 days. Add a payment method to keep your premium features.",
                        ActionUrl = "/billing",
                        ActionText = "Manage Subscription"
                    }
                );
                
                _logger.LogInformation("Sent trial ending notification to user {UserId} for subscription {SubscriptionId}", 
                    userId.Value, subscription.Id);
            }
            else
            {
                _logger.LogWarning("Could not find user for Stripe subscription {SubscriptionId}", subscription.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling trial will end webhook");
            throw;
        }
    }

    /// <summary>
    /// A cardless trial reached its end with no payment method, so Stripe moved the
    /// subscription to <c>paused</c>. That is the authoritative "trial is over" moment.
    ///
    /// Idempotent: replaying this event just re-applies the same end state. Stripe retries
    /// webhooks and does not guarantee ordering, so this must be safe to receive twice, and
    /// safe to receive after the customer has already converted (guarded below).
    /// </summary>
    private async Task HandleSubscriptionPaused(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (subscription?.Id == null)
            {
                _logger.LogWarning("Subscription paused event missing subscription ID");
                return;
            }

            // Through the single projection path, so a webhook and a forced refresh can
            // never disagree about what "paused" means locally.
            await ProjectAsync(subscription);

            var userId = await _billingService.GetUserIdByStripeSubscriptionIdAsync(subscription.Id);
            if (userId.HasValue)
            {
                await _notificationService.CreateNotificationAsync(
                    userId.Value,
                    new CreateNotificationRequest
                    {
                        Type = "trial_ended",
                        Title = "Your free trial has ended",
                        Message = "Add a payment method to pick up right where you left off. Your data is safe.",
                        ActionUrl = "/billing",
                        ActionText = "Choose a plan"
                    }
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling subscription paused webhook");
        }
    }

    /// <summary>
    /// The customer added a card and Stripe resumed a previously paused subscription.
    /// </summary>
    private async Task HandleSubscriptionResumed(Event stripeEvent)
    {
        try
        {
            var subscription = stripeEvent.Data.Object as Stripe.Subscription;
            if (subscription?.Id == null)
            {
                _logger.LogWarning("Subscription resumed event missing subscription ID");
                return;
            }

            await ProjectAsync(subscription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling subscription resumed webhook");
        }
    }

    /// <summary>
    /// A paid invoice on a subscription. Stripe advises re-reading the subscription after
    /// <c>invoice.paid</c> and confirming it is active before extending access, so this pulls the
    /// live subscription instead of trusting the event. The legacy <c>invoice.payment_succeeded</c>
    /// path (payment rows, metrics) is separate and untouched.
    /// </summary>
    private async Task HandleInvoicePaid(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as Stripe.Invoice;
        var subscriptionId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
        if (string.IsNullOrEmpty(subscriptionId)) return;

        // Only a Cloud plan is re-read here: the subscription metadata its checkout stamped carries
        // "entitlements". A hosted Standard or Premium renewal is already handled by
        // invoice.payment_succeeded, so it costs no extra Stripe call and has no second writer.
        var meta = invoice!.Parent?.SubscriptionDetails?.Metadata;
        if (meta == null || !meta.ContainsKey("entitlements")) return;
        if (IsForeignEnvironment(meta)) return;

        await RefreshSubscriptionAsync(subscriptionId);
    }

    /// <summary>
    /// A subscription schedule (a scheduled product removal or end of plan) changed. The schedule
    /// itself carries nothing we mirror, so re-read the subscription it belongs to.
    /// </summary>
    private async Task HandleSubscriptionScheduleChanged(Event stripeEvent)
    {
        var schedule = stripeEvent.Data.Object as Stripe.SubscriptionSchedule;
        var subscriptionId = schedule?.SubscriptionId ?? schedule?.ReleasedSubscription;
        if (string.IsNullOrEmpty(subscriptionId)) return;

        await RefreshSubscriptionAsync(subscriptionId);
    }

    private async Task RefreshSubscriptionAsync(string subscriptionId)
    {
        var userId = await _billingService.GetUserIdByStripeSubscriptionIdAsync(subscriptionId);
        if (userId == null)
        {
            _logger.LogInformation("No local user for Stripe subscription {SubscriptionId}; nothing to refresh", subscriptionId);
            return;
        }

        var snapshot = await _stripeService.GetSubscriptionAsync(subscriptionId);
        if (snapshot == null) return;

        await _syncService.ProjectAsync(userId.Value, snapshot);
    }

    /// <summary>
    /// Projects a webhook's subscription payload onto local state via the one mapping in
    /// <see cref="ISubscriptionSyncService"/>. No extra Stripe call: the event already carries
    /// the authoritative object.
    /// </summary>
    private async Task ProjectAsync(Stripe.Subscription subscription)
    {
        if (IsForeignEnvironment(subscription.Metadata)) return;

        var userId = await _billingService.GetUserIdByStripeSubscriptionIdAsync(subscription.Id);
        if (userId == null)
        {
            _logger.LogWarning(
                "No local user for Stripe subscription {SubscriptionId}; nothing to project", subscription.Id);
            return;
        }

        var snapshot = StripeSnapshotMapper.From(subscription);

        // Stripe does not promise delivery order, so an event can carry an older state than the one
        // already applied. For a Cloud (multi-product) subscription, where the products themselves
        // ride on this snapshot, read the live subscription instead and project that. If Stripe
        // cannot be reached the event's own payload is still better than dropping the change.
        if (snapshot.IsCatalog)
        {
            try
            {
                snapshot = await _stripeService.GetSubscriptionAsync(subscription.Id) ?? snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not re-read Stripe subscription {SubscriptionId}; projecting the event payload", subscription.Id);
            }
        }

        await _syncService.ProjectAsync(userId.Value, snapshot);
    }

    private async Task HandleSetupIntentSucceeded(Event stripeEvent)
    {
        try
        {
            var setupIntent = stripeEvent.Data.Object as Stripe.SetupIntent;
            _logger.LogInformation(
                "Setup intent {SetupIntentId} succeeded for customer {CustomerId} - payment method saved", 
                setupIntent?.Id, setupIntent?.Customer);
            
            // Payment method has been saved successfully
            // The actual subscription creation happens in the /trial/start-with-card endpoint
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling setup intent succeeded webhook");
            throw;
        }
    }
}