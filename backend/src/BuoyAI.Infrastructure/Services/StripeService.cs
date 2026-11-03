using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Utilities;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Stripe payment processing and subscription management service.
/// Note: Webhook handling is done by StripeWebhookController, not this service.
/// This service handles Stripe API calls (creating subscriptions, payments, etc.)
/// </summary>
public class StripeService : IStripeService
{
    private readonly ILogger<StripeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly StripeClient _stripeClient;

    // Which environment this process is. Staging and production share one live Stripe
    // account, so every object we create is stamped with this and we refuse to adopt or
    // delete objects belonging to another environment. See StripeEnvironmentPolicy.
    private readonly string _environmentTag;

    // Stripe price IDs (configured in appsettings or Stripe dashboard)
    private readonly Dictionary<(SubscriptionTier, SubscriptionBillingCycle), string> _priceIds;

    public StripeService(
        IConfiguration configuration,
        ILogger<StripeService> logger,
        IHostEnvironment hostEnvironment)
    {
        _logger = logger;
        _configuration = configuration;
        _environmentTag = StripeEnvironmentPolicy.Tag(hostEnvironment.EnvironmentName);

        // Initialize Stripe client with API key
        var apiKey = _configuration["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Stripe API key not configured");
        }

        _stripeClient = new StripeClient(apiKey);

        // Load price IDs from configuration.
        //
        // The key is "Stripe:Products:{Tier}:{Cycle}" — this previously read
        // "Stripe:PriceIds:{Tier}{Cycle}", which exists in NO appsettings file, so every
        // entry resolved to "" in every environment. BillingHelpers.GetStripePriceId has
        // always used the correct key, which is why paid checkout still worked; only the
        // paths reading this dictionary were broken.
        _priceIds = new Dictionary<(SubscriptionTier, SubscriptionBillingCycle), string>
        {
            [(SubscriptionTier.Standard, SubscriptionBillingCycle.Monthly)] = _configuration["Stripe:Products:Standard:Monthly"] ?? "",
            [(SubscriptionTier.Standard, SubscriptionBillingCycle.Yearly)] = _configuration["Stripe:Products:Standard:Yearly"] ?? "",
            [(SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly)] = _configuration["Stripe:Products:Premium:Monthly"] ?? "",
            [(SubscriptionTier.Premium, SubscriptionBillingCycle.Yearly)] = _configuration["Stripe:Products:Premium:Yearly"] ?? ""
        };
    }

    public async Task<string> CreateCustomerAsync(User user)
    {
        try
        {
            var options = new CustomerCreateOptions
            {
                Email = user.Email,
                Name = $"{user.FirstName} {user.LastName}",
                Metadata = new Dictionary<string, string>
                {
                    ["user_id"] = user.Id.ToString(),
                    ["username"] = user.Username ?? string.Empty,
                    [StripeEnvironmentPolicy.MetadataKey] = _environmentTag
                }
            };

            var customer = await _stripeClient.V1.Customers.CreateAsync(options);
            
            _logger.LogInformation("Created Stripe customer {CustomerId} for user {UserId}", customer.Id, user.Id);
            return customer.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Stripe customer for user {UserId}", user.Id);
            throw;
        }
    }

    public async Task UpdateCustomerAsync(string customerId, User user)
    {
        try
        {
            var options = new CustomerUpdateOptions
            {
                Email = user.Email,
                Name = $"{user.FirstName} {user.LastName}",
                Metadata = new Dictionary<string, string>
                {
                    ["user_id"] = user.Id.ToString(),
                    ["username"] = user.Username ?? string.Empty,
                    // Re-stamp on update so a customer created before this policy shipped
                    // gets claimed by whichever environment is actively managing it.
                    [StripeEnvironmentPolicy.MetadataKey] = _environmentTag
                }
            };

            await _stripeClient.V1.Customers.UpdateAsync(customerId, options);
            _logger.LogInformation("Updated Stripe customer {CustomerId} for user {UserId}", customerId, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Stripe customer {CustomerId} for user {UserId}", customerId, user.Id);
            throw;
        }
    }

    public async Task<bool> DeleteCustomerAsync(string customerId)
    {
        try
        {
            // Deleting a customer cancels its subscriptions and detaches its payment
            // methods, and staging shares the live account with production, so confirm we
            // own this customer before destroying it. Refusing is not an error: local
            // deletion still completes and the foreign customer is simply left intact.
            var customer = await _stripeClient.V1.Customers.GetAsync(customerId);
            if (!StripeEnvironmentPolicy.IsOwnedBy(customer.Metadata, _environmentTag))
            {
                _logger.LogWarning(
                    "Refusing to delete Stripe customer {CustomerId}: it belongs to environment {Owner}, not {Current}. " +
                    "Local data deletion continues; the Stripe customer is left intact and can be removed by hand if it really is orphaned.",
                    customerId,
                    StripeEnvironmentPolicy.Owner(customer.Metadata) ?? "untagged (pre-policy)",
                    _environmentTag);
                return false;
            }

            await _stripeClient.V1.Customers.DeleteAsync(customerId);
            _logger.LogInformation("Deleted Stripe customer {CustomerId}", customerId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Stripe customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task AttachPaymentMethodAsync(string customerId, string paymentMethodId)
    {
        try
        {
            // Attach the payment method to the customer
            var attachOptions = new PaymentMethodAttachOptions
            {
                Customer = customerId
            };
            await _stripeClient.V1.PaymentMethods.AttachAsync(paymentMethodId, attachOptions);
            
            // Set it as the default payment method for the customer
            var customerUpdateOptions = new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions
                {
                    DefaultPaymentMethod = paymentMethodId
                }
            };
            await _stripeClient.V1.Customers.UpdateAsync(customerId, customerUpdateOptions);
            
            _logger.LogInformation("Attached and set default payment method {PaymentMethodId} for customer {CustomerId}", 
                paymentMethodId, customerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach payment method {PaymentMethodId} to customer {CustomerId}", 
                paymentMethodId, customerId);
            throw;
        }
    }

    /// <summary>
    /// Creates a SetupIntent for collecting a payment method without charging.
    /// Used for card-upfront trials where we validate the card before starting trial.
    /// </summary>
    public async Task<SetupIntentResult> CreateSetupIntentAsync(string customerId)
    {
        try
        {
            var options = new SetupIntentCreateOptions
            {
                Customer = customerId,
                PaymentMethodTypes = new List<string> { "card" },
                Usage = "off_session", // Allow charging later without user present
                Metadata = new Dictionary<string, string>
                {
                    ["purpose"] = "trial_signup"
                }
            };

            var setupIntent = await _stripeClient.V1.SetupIntents.CreateAsync(options);
            
            _logger.LogInformation("Created SetupIntent {SetupIntentId} for customer {CustomerId}", 
                setupIntent.Id, customerId);
            
            return new SetupIntentResult(setupIntent.Id, setupIntent.ClientSecret);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create SetupIntent for customer {CustomerId}", customerId);
            throw;
        }
    }

    /// <summary>
    /// Creates a subscription with a trial period using a pre-validated payment method.
    /// The payment method will be charged automatically when the trial ends.
    /// </summary>
    public async Task<string> CreateTrialSubscriptionAsync(string customerId, string priceId, string paymentMethodId, int trialDays = 14)
    {
        try
        {
            // First attach and set the payment method as default
            await AttachPaymentMethodAsync(customerId, paymentMethodId);

            var options = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Price = priceId }
                },
                DefaultPaymentMethod = paymentMethodId,
                TrialPeriodDays = trialDays,
                PaymentSettings = new SubscriptionPaymentSettingsOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    SaveDefaultPaymentMethod = "on_subscription"
                },
                // Automatically charge using default payment method when trial ends
                CollectionMethod = "charge_automatically",
                Metadata = new Dictionary<string, string>
                {
                    ["is_trial"] = "true",
                    ["card_upfront"] = "true",
                    [StripeEnvironmentPolicy.MetadataKey] = _environmentTag
                }
            };

            var subscription = await _stripeClient.V1.Subscriptions.CreateAsync(options);
            
            _logger.LogInformation(
                "Created trial subscription {SubscriptionId} for customer {CustomerId} with {TrialDays}-day trial, status: {Status}", 
                subscription.Id, customerId, trialDays, subscription.Status);
            
            return subscription.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create trial subscription for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
    {
        try
        {
            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = returnUrl
            };
            var session = await _stripeClient.V1.BillingPortal.Sessions.CreateAsync(options);
            _logger.LogInformation("Created Stripe billing portal session for customer {CustomerId}", customerId);
            return session.Url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create billing portal session for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        string clientReferenceId,
        IDictionary<string, string> metadata,
        string? promoCode = null,
        string? customerCountry = null)
    {
        try
        {
            var options = new Stripe.Checkout.SessionCreateOptions
            {
                Mode = "subscription",
                Customer = customerId,
                ClientReferenceId = clientReferenceId,
                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                {
                    new() { Price = priceId, Quantity = 1 }
                },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                // Mirror metadata onto both the session and the resulting subscription so
                // either webhook (checkout.session.completed / customer.subscription.created)
                // can resolve the user, tier and billing cycle.
                Metadata = new Dictionary<string, string>(metadata),
                SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>(metadata)
                }
            };

            // If a promo code was pre-applied in our UI, attach it as a discount (Stripe
            // forbids combining a fixed discount with the "enter a code" box). Otherwise
            // let the user enter one on the hosted page.
            string? promoCodeId = null;
            if (!string.IsNullOrWhiteSpace(promoCode))
            {
                var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(
                    new PromotionCodeListOptions { Code = promoCode, Active = true, Limit = 1 });
                promoCodeId = promoCodes.Data.FirstOrDefault()?.Id;
                if (promoCodeId == null)
                {
                    _logger.LogWarning("Checkout promo code {PromoCode} not found or inactive; ignoring", promoCode);
                }
            }

            if (promoCodeId != null)
            {
                options.Discounts = new List<Stripe.Checkout.SessionDiscountOptions>
                {
                    new() { PromotionCode = promoCodeId }
                };
            }
            else
            {
                options.AllowPromotionCodes = true;
            }

            // Merchant-of-record routing scaffold (#941): once Stripe Managed Payments
            // is activated on the account, non-US customers route through SMP (Stripe
            // remits their VAT/GST as MoR) while US checkouts stay exactly as today.
            // Sent via extra params so this compiles regardless of Stripe.net's SMP
            // typing; the flag is inert until Billing:ManagedPayments:Enabled is true.
            if (_configuration.GetValue("Billing:ManagedPayments:Enabled", false)
                && !string.IsNullOrEmpty(customerCountry)
                && !string.Equals(customerCountry, "US", StringComparison.OrdinalIgnoreCase))
            {
                options.AddExtraParam("managed_payments[enabled]", "true");
                _logger.LogInformation(
                    "Routing checkout for customer {CustomerId} (country {Country}) through Managed Payments",
                    customerId, customerCountry);
            }

            var session = await _stripeClient.V1.Checkout.Sessions.CreateAsync(options);
            _logger.LogInformation(
                "Created Stripe Checkout session {SessionId} for customer {CustomerId}",
                session.Id, customerId);
            return session.Url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Checkout session for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<string> CreateSubscriptionAsync(string customerId, string priceId, string? promoCode = null, bool startTrial = false)
    {
        try
        {
            var options = new SubscriptionCreateOptions
            {
                Customer = customerId,
                Items = new List<SubscriptionItemOptions>
                {
                    new() { Price = priceId }
                },
                // Allow subscription to be created even if first payment fails
                // This gives us the subscription ID and allows retry later
                PaymentBehavior = "allow_incomplete",
                PaymentSettings = new SubscriptionPaymentSettingsOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    SaveDefaultPaymentMethod = "on_subscription"
                },
                // Automatically charge using default payment method
                CollectionMethod = "charge_automatically",
                Metadata = new Dictionary<string, string>
                {
                    [StripeEnvironmentPolicy.MetadataKey] = _environmentTag
                }
            };

            // Add trial period if specified (cardless trial, length from config)
            if (startTrial)
            {
                var trialDays = _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);
                options.TrialPeriodDays = trialDays;
            }

            // Apply promo code if provided
            if (!string.IsNullOrEmpty(promoCode))
            {
                // Look up the promo code ID from the code string
                var promoCodeOptions = new PromotionCodeListOptions
                {
                    Code = promoCode,
                    Active = true,
                    Limit = 1
                };

                var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(promoCodeOptions);
                if (promoCodes.Data.Any())
                {
                    var promoCodeId = promoCodes.Data.First().Id;
                    options.Discounts = new List<SubscriptionDiscountOptions>
                    {
                        new SubscriptionDiscountOptions { PromotionCode = promoCodeId }
                    };
                    _logger.LogInformation("Applied promo code {PromoCode} (ID: {PromoCodeId}) to subscription", promoCode, promoCodeId);
                }
                else
                {
                    _logger.LogWarning("Promo code {PromoCode} not found or inactive", promoCode);
                }
            }

            var subscription = await _stripeClient.V1.Subscriptions.CreateAsync(options);
            
            _logger.LogInformation("Created Stripe subscription {SubscriptionId} for customer {CustomerId}", 
                subscription.Id, customerId);
            
            return subscription.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for customer {CustomerId}", customerId);
            throw;
        }
    }

    /// <summary>
    /// Creates a CARDLESS trial subscription: a real Stripe subscription in <c>trialing</c>
    /// status with no payment method attached.
    ///
    /// This is what makes Stripe the system of record for the trial lifecycle instead of us
    /// reimplementing it. Stripe decides when the trial ends, emits
    /// <c>customer.subscription.trial_will_end</c> three days out (the reminder we could not
    /// send before, because our no-card trials had no subscription behind them), and emits
    /// <c>customer.subscription.paused</c> at expiry.
    ///
    /// <para><b>End behaviour is <c>pause</c>, deliberately.</b> The three options are:
    /// <c>create_invoice</c> raises an invoice nobody can pay, which drags the customer into
    /// Stripe's dunning flow and emails them about a failed payment they never agreed to;
    /// <c>cancel</c> destroys the subscription so converting later means building a new one
    /// from scratch; <c>pause</c> freezes it, bills nothing, and lets conversion be "add a
    /// card and resume". It also keeps lapsed trials out of the churn numbers, where they
    /// would otherwise read as cancellations by people who were never customers.</para>
    ///
    /// <para>No <c>PaymentBehavior</c> is set. That parameter governs what happens when a
    /// first invoice needs paying, and a trialing subscription has no invoice to pay yet.
    /// The previous version of this method passed <c>default_incomplete</c>, which would have
    /// forced the subscription to <c>incomplete</c> and blocked the trial outright.</para>
    /// </summary>
    /// <param name="trialDays">
    /// Honoured as given, so a captured marketing lead's longer offer reaches Stripe rather
    /// than being silently replaced by the configured default.
    /// </param>
    public async Task<CardlessTrialResult> CreateCardlessTrialSubscriptionAsync(
        string customerId, SubscriptionTier tier, int trialDays)
    {
        // Read through BillingHelpers so there is a single definition of "which price is
        // this tier", and so a missing price throws a named error instead of Stripe
        // rejecting an empty string.
        var priceId = BillingHelpers.GetStripePriceId(_configuration, tier, SubscriptionBillingCycle.Monthly);

        var options = new SubscriptionCreateOptions
        {
            Customer = customerId,
            Items = new List<SubscriptionItemOptions> { new() { Price = priceId } },
            TrialPeriodDays = trialDays,
            TrialSettings = new SubscriptionTrialSettingsOptions
            {
                EndBehavior = new SubscriptionTrialSettingsEndBehaviorOptions
                {
                    MissingPaymentMethod = "pause"
                }
            },
            // Nothing to collect during the trial; the customer adds a card only if they convert.
            PaymentSettings = new SubscriptionPaymentSettingsOptions
            {
                SaveDefaultPaymentMethod = "on_subscription"
            },
            CollectionMethod = "charge_automatically",
            Metadata = new Dictionary<string, string>
            {
                ["is_trial"] = "true",
                ["cardless"] = "true",
                ["tier"] = tier.ToString(),
                ["trial_days"] = trialDays.ToString(),
                [StripeEnvironmentPolicy.MetadataKey] = _environmentTag
            }
        };

        var subscription = await _stripeClient.V1.Subscriptions.CreateAsync(options);

        _logger.LogInformation(
            "Created cardless trial subscription {SubscriptionId} for customer {CustomerId}: tier={Tier}, {TrialDays}d, status={Status}, trialEnd={TrialEnd}",
            subscription.Id, customerId, tier, trialDays, subscription.Status, subscription.TrialEnd);

        return new CardlessTrialResult(
            subscription.Id,
            // Stripe's own trial end is authoritative. Using it rather than a locally
            // computed AddDays keeps our cached TrialEndDate identical to the date Stripe
            // will actually act on, so the reminder and the lockout agree to the second.
            subscription.TrialEnd ?? DateTime.UtcNow.AddDays(trialDays),
            subscription.Status);
    }

    /// <summary>
    /// Resumes a subscription that Stripe paused when a cardless trial ended without a card.
    /// Used when the customer comes back and adds one.
    /// </summary>
    public async Task ResumePausedSubscriptionAsync(string subscriptionId)
    {
        await _stripeClient.V1.Subscriptions.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            PauseCollection = null
        });
        _logger.LogInformation("Resumed paused subscription {SubscriptionId}", subscriptionId);
    }

    /// <summary>
    /// The customer's current live subscription, or null.
    ///
    /// "Live" excludes canceled and incomplete_expired: those are finished and must not block
    /// a new trial or be projected as current state. A paused subscription IS live, because a
    /// lapsed cardless trial is resumable and still the customer's subscription.
    ///
    /// This is the read that makes Stripe the authority. Everything that decides what a user
    /// is entitled to ultimately resolves through here rather than through our own row.
    /// </summary>
    public async Task<StripeSubscriptionSnapshot?> GetLiveSubscriptionAsync(string customerId)
    {
        var subs = await _stripeClient.V1.Subscriptions.ListAsync(new SubscriptionListOptions
        {
            Customer = customerId,
            Status = "all",
            Limit = 100
        });

        // Prefer the most recently created live one. A customer should only ever have a single
        // live subscription; if a bug or a manual dashboard edit produced more, acting on the
        // newest is the least surprising choice and the drift check will flag the rest.
        var live = subs.Data
            .Where(s => s.Status is not ("canceled" or "incomplete_expired"))
            .OrderByDescending(s => s.Created)
            .FirstOrDefault();

        if (live == null) return null;

        return StripeSnapshotMapper.From(live);
    }

    /// <summary>Snapshot of one subscription by id, or null if Stripe does not know it.</summary>
    public async Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId)
    {
        try
        {
            var s = await _stripeClient.V1.Subscriptions.GetAsync(subscriptionId);
            return StripeSnapshotMapper.From(s);
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "invalid_request_error")
        {
            // Deleted or never existed. Not an error worth throwing over: the caller is
            // reconciling and "Stripe has no such subscription" is a valid answer.
            _logger.LogWarning("Stripe does not recognise subscription {SubscriptionId}", subscriptionId);
            return null;
        }
    }

    public async Task<SubscriptionDiscountDto?> ApplyPromotionCodeToSubscriptionAsync(
        string subscriptionId, string promoCode)
    {
        // Resolve the customer-facing code to its promotion-code id. Inactive codes are
        // excluded here rather than after, so an expired code behaves exactly like a
        // nonexistent one.
        var matches = await _stripeClient.V1.PromotionCodes.ListAsync(new PromotionCodeListOptions
        {
            Code = promoCode.Trim(),
            Active = true,
            Limit = 1
        });

        var promotionCodeId = matches.Data.FirstOrDefault()?.Id;
        if (promotionCodeId == null)
        {
            _logger.LogInformation("Promotion code rejected for subscription {SubscriptionId}: not found or inactive", subscriptionId);
            return null;
        }

        // Replaces any existing discount rather than stacking. Stripe allows one discount per
        // subscription, and silently stacking would let a user chain codes indefinitely.
        var updated = await _stripeClient.V1.Subscriptions.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Discounts = new List<SubscriptionDiscountOptions>
            {
                new() { PromotionCode = promotionCodeId }
            },
            // Expanded field — see ApplyCouponToSubscriptionAsync. Without it the redeem
            // endpoint would 404 a code it had just successfully applied.
            Expand = new List<string> { "discounts" }
        });

        _logger.LogInformation(
            "Applied promotion code to subscription {SubscriptionId} (promo {PromotionCodeId})",
            subscriptionId, promotionCodeId);

        return ToDiscountDto(updated);
    }

    public async Task<SubscriptionDiscountDto?> ApplyCouponToSubscriptionAsync(
        string subscriptionId, string couponId)
    {
        try
        {
            // Coupon id rather than promotion code: a system-granted reward has no
            // customer-facing string, so there is nothing to share, leak, or guess.
            var updated = await _stripeClient.V1.Subscriptions.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
            {
                Discounts = new List<SubscriptionDiscountOptions>
                {
                    new() { Coupon = couponId }
                },
                // Subscription.Discounts is an EXPANDED field: without this, Stripe returns
                // discount IDs only and the objects come back empty. The coupon still gets
                // applied — it just reads back as "no discount", so the caller concludes the
                // grant failed and leaves the referral pending forever while re-applying the
                // same coupon on every sweep.
                Expand = new List<string> { "discounts" }
            });

            _logger.LogInformation(
                "Applied reward coupon {CouponId} to subscription {SubscriptionId}", couponId, subscriptionId);

            return ToDiscountDto(updated);
        }
        catch (Exception ex)
        {
            // A misconfigured or deleted coupon must not take the sweep down; the caller
            // leaves the referral pending and retries.
            _logger.LogError(ex, "Failed to apply coupon {CouponId} to subscription {SubscriptionId}", couponId, subscriptionId);
            return null;
        }
    }

    public async Task<SubscriptionDiscountDto?> GetSubscriptionDiscountAsync(string subscriptionId)
    {
        try
        {
            var subscription = await _stripeClient.V1.Subscriptions.GetAsync(
                subscriptionId,
                // Expanded field — without this the badge never renders even when a discount
                // is genuinely applied.
                new SubscriptionGetOptions { Expand = new List<string> { "discounts" } });
            return ToDiscountDto(subscription);
        }
        catch (Exception ex)
        {
            // Display-only data. A Stripe hiccup must degrade to "no badge", never to a failed
            // page load for the whole subscription card.
            _logger.LogWarning(ex, "Could not read discount for subscription {SubscriptionId}", subscriptionId);
            return null;
        }
    }

    /// <summary>
    /// Maps Stripe's discount shape onto ours, including the display string. Returns null when
    /// the subscription carries no discount.
    /// </summary>
    private static SubscriptionDiscountDto? ToDiscountDto(Stripe.Subscription subscription)
    {
        var discount = subscription.Discounts?.FirstOrDefault();
        var coupon = discount?.Coupon;
        if (coupon == null) return null;

        var dto = new SubscriptionDiscountDto
        {
            // Prefer the code the user actually typed; fall back to the coupon name for
            // discounts applied directly in the Stripe dashboard, which have no code.
            Code = discount!.PromotionCode?.Code ?? coupon.Name ?? string.Empty,
            PercentOff = coupon.PercentOff.HasValue ? (decimal)coupon.PercentOff.Value : null,
            AmountOff = coupon.AmountOff,
            Currency = coupon.Currency,
            Duration = coupon.Duration ?? string.Empty,
            DurationInMonths = (int?)coupon.DurationInMonths,
            EndsAt = discount.End,
            // Next invoice. While trialing there is none until the trial ends, which is the
            // whole reason a discount can look applied and still do nothing.
            AppliesAt = subscription.TrialEnd
                        ?? subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd
        };

        // A repeating coupon counts down from when it was applied, not from first billing, so
        // a long trial can outlive it entirely.
        dto.ExpiresBeforeItApplies =
            dto.EndsAt.HasValue && dto.AppliesAt.HasValue && dto.EndsAt < dto.AppliesAt;

        dto.Description = DescribeDiscount(dto);
        return dto;
    }

    /// <summary>
    /// Formats a Stripe minor-unit amount using the COUPON's currency, not the server's.
    ///
    /// The obvious ":C" format specifier reads CultureInfo.CurrentCulture, which on a
    /// container with no locale set is the invariant culture — whose currency symbol is the
    /// generic placeholder "¤". A $15 referral credit would have rendered to users as
    /// "¤15.00 off". The currency belongs to the coupon, so it is read from there.
    /// </summary>
    private static string FormatMoney(long minorUnits, string? currency)
    {
        var value = minorUnits / 100m;
        return currency?.ToLowerInvariant() switch
        {
            "usd" => $"${value:0.00}",
            "gbp" => $"£{value:0.00}",
            "eur" => $"€{value:0.00}",
            null or "" => $"{value:0.00}",
            // Unknown currency: show the ISO code rather than guess at a symbol.
            var code => $"{value:0.00} {code.ToUpperInvariant()}",
        };
    }

    /// <summary>
    /// Wording for the subscription card. Built here so web and mobile cannot drift.
    /// </summary>
    private static string DescribeDiscount(SubscriptionDiscountDto d)
    {
        var amount = d.PercentOff.HasValue
            ? $"{d.PercentOff.Value:0.##}% off"
            : d.AmountOff.HasValue
                ? $"{FormatMoney(d.AmountOff.Value, d.Currency)} off"
                : "Discount";

        var period = d.Duration switch
        {
            "forever" => " forever",
            "once" => " on your next bill",
            "repeating" when d.DurationInMonths is > 1 => $" for {d.DurationInMonths} months",
            "repeating" => " for 1 month",
            _ => string.Empty
        };

        // Say WHEN. "100% off" on day 1 of a 14-day trial is indistinguishable from nothing
        // happening, because the user is charged zero either way for the next two weeks.
        var when = d.AppliesAt.HasValue
            ? $", starting {d.AppliesAt.Value:d MMM yyyy}"
            : string.Empty;

        return d.ExpiresBeforeItApplies
            ? amount + period + " — expires before your next bill"
            : amount + period + when;
    }

    /// <summary>
    /// Finds an existing Stripe customer for this email, so repeated signup attempts and
    /// reconciliation retries reuse one customer instead of littering the account with
    /// duplicates that all look like separate people in Stripe's metrics.
    ///
    /// Scoped to customers this environment owns. Reuse is only correct WITHIN an
    /// environment: staging and production share the live account, so an unscoped match on
    /// email would hand staging the production customer for the same person, after which a
    /// staging account deletion would cancel that person's real subscription. Finding
    /// nothing is the safe outcome, since the caller then mints a fresh customer.
    /// </summary>
    public async Task<string?> FindCustomerIdByEmailAsync(string email)
    {
        // Limit is deliberately >1: the same address can legitimately have one customer per
        // environment, and the one we want is not necessarily the most recent.
        var matches = await _stripeClient.V1.Customers.ListAsync(new CustomerListOptions
        {
            Email = email.Trim().ToLowerInvariant(),
            Limit = 100
        });

        var owned = matches.Data
            .FirstOrDefault(c => StripeEnvironmentPolicy.IsOwnedBy(c.Metadata, _environmentTag));

        if (owned == null && matches.Data.Count > 0)
        {
            _logger.LogInformation(
                "Found {Count} Stripe customer(s) for this email but none owned by {Current}; a new customer will be created rather than adopting another environment's.",
                matches.Data.Count,
                _environmentTag);
        }

        return owned?.Id;
    }

    public async Task UpdateSubscriptionAsync(string subscriptionId, string newPriceId, string? promoCode = null)
    {
        try
        {
            // Get current subscription
            var subscription = await _stripeClient.V1.Subscriptions.GetAsync(subscriptionId);
            var subscriptionItem = subscription.Items.Data[0];

            // Update subscription item with new price
            var options = new SubscriptionUpdateOptions
            {
                Items = new List<SubscriptionItemOptions>
                {
                    new()
                    {
                        Id = subscriptionItem.Id,
                        Price = newPriceId
                    }
                },
                BillingCycleAnchor = SubscriptionBillingCycleAnchor.Now
            };

            // Apply promo code if provided
            if (!string.IsNullOrEmpty(promoCode))
            {
                // Look up the promo code ID from the code string
                var promoCodeOptions = new PromotionCodeListOptions
                {
                    Code = promoCode,
                    Active = true,
                    Limit = 1
                };

                var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(promoCodeOptions);
                if (promoCodes.Data.Any())
                {
                    var promoCodeId = promoCodes.Data.First().Id;
                    options.Discounts = new List<SubscriptionDiscountOptions>
                    {
                        new SubscriptionDiscountOptions { PromotionCode = promoCodeId }
                    };
                    _logger.LogInformation("Applied promo code {PromoCode} (ID: {PromoCodeId}) to subscription update", promoCode, promoCodeId);
                }
                else
                {
                    _logger.LogWarning("Promo code {PromoCode} not found or inactive for subscription update", promoCode);
                }
            }

            await _stripeClient.V1.Subscriptions.UpdateAsync(subscriptionId, options);
            
            _logger.LogInformation("Updated Stripe subscription {SubscriptionId} to price {PriceId}{PromoInfo}", 
                subscriptionId, newPriceId,
                string.IsNullOrEmpty(promoCode) ? "" : $" with promo code {promoCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, bool immediately = false)
    {
        try
        {
            if (immediately)
            {
                // Cancel immediately
                await _stripeClient.V1.Subscriptions.CancelAsync(subscriptionId);
            }
            else
            {
                // Cancel at period end
                var options = new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = true
                };
                await _stripeClient.V1.Subscriptions.UpdateAsync(subscriptionId, options);
            }

            _logger.LogInformation("Cancelled Stripe subscription {SubscriptionId} (immediate: {Immediate})", 
                subscriptionId, immediately);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task<PaymentStatus> GetPaymentStatusAsync(string paymentIntentId)
    {
        try
        {
            var paymentIntent = await _stripeClient.V1.PaymentIntents.GetAsync(paymentIntentId);
            return MapPaymentIntentStatus(paymentIntent.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get payment status for {PaymentIntentId}", paymentIntentId);
            throw;
        }
    }

    public async Task RefundPaymentAsync(string paymentIntentId, decimal? amount = null)
    {
        try
        {
            var options = new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId
            };

            if (amount.HasValue)
            {
                options.Amount = (long)(amount.Value * 100); // Convert to cents
            }

            await _stripeClient.V1.Refunds.CreateAsync(options);
            _logger.LogInformation("Created refund for payment intent {PaymentIntentId}", paymentIntentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refund payment {PaymentIntentId}", paymentIntentId);
            throw;
        }
    }

    public async Task<bool> PayPendingInvoicesAsync(string customerId)
    {
        try
        {
            // Find all open/unpaid invoices for the customer
            var invoiceOptions = new InvoiceListOptions
            {
                Customer = customerId,
                Status = "open",
                Limit = 10
            };

            var invoices = await _stripeClient.V1.Invoices.ListAsync(invoiceOptions);
            var paidAny = false;

            foreach (var invoice in invoices.Data)
            {
                try
                {
                    // Attempt to pay the invoice using the customer's default payment method
                    var payOptions = new InvoicePayOptions();
                    await _stripeClient.V1.Invoices.PayAsync(invoice.Id, payOptions);
                    _logger.LogInformation("Successfully paid pending invoice {InvoiceId} for customer {CustomerId}", 
                        invoice.Id, customerId);
                    paidAny = true;
                }
                catch (StripeException ex)
                {
                    _logger.LogWarning("Failed to pay invoice {InvoiceId} for customer {CustomerId}: {Error}", 
                        invoice.Id, customerId, ex.Message);
                    // Continue with other invoices even if one fails
                }
            }

            return paidAny;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pay pending invoices for customer {CustomerId}", customerId);
            throw;
        }
    }

    public async Task<bool> ValidatePromoCodeAsync(string promoCode)
    {
        try
        {
            var options = new PromotionCodeListOptions
            {
                Code = promoCode,
                Active = true,
                Limit = 1
            };

            var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(options);
            return promoCodes.Data.Any();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate promo code {PromoCode}", promoCode);
            return false;
        }
    }

    public async Task<PromoCodeValidationResult> ValidatePromoCodeWithPriceAsync(string promoCode, string priceId)
    {
        try
        {
            var options = new PromotionCodeListOptions
            {
                Code = promoCode,
                Active = true,
                Limit = 1
            };

            var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(options);
            if (!promoCodes.Data.Any())
            {
                return new PromoCodeValidationResult { IsValid = false };
            }

            var promoCodeObj = promoCodes.Data.First();
            var coupon = promoCodeObj.Coupon;

            // Get the price to calculate discount
            var price = await _stripeClient.V1.Prices.GetAsync(priceId);
            var originalPrice = price.UnitAmount ?? 0;

            long discountAmount = 0;
            decimal discountPercent = 0;

            if (coupon.PercentOff.HasValue)
            {
                discountPercent = coupon.PercentOff.Value;
                discountAmount = (long)(originalPrice * (discountPercent / 100));
            }
            else if (coupon.AmountOff.HasValue)
            {
                discountAmount = coupon.AmountOff.Value;
                discountPercent = (decimal)discountAmount / originalPrice * 100;
            }

            var finalPrice = originalPrice - discountAmount;

            return new PromoCodeValidationResult
            {
                IsValid = true,
                PromoCode = promoCode,
                DiscountPercent = discountPercent,
                DiscountAmount = discountAmount,
                OriginalPrice = originalPrice,
                FinalPrice = finalPrice,
                Description = coupon.Name ?? $"{discountPercent}% off"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate promo code {PromoCode} with price {PriceId}", promoCode, priceId);
            return new PromoCodeValidationResult { IsValid = false };
        }
    }

    public async Task<decimal> GetPromoCodeDiscountAsync(string promoCode)
    {
        try
        {
            var options = new PromotionCodeListOptions
            {
                Code = promoCode,
                Active = true,
                Limit = 1
            };

            var promoCodes = await _stripeClient.V1.PromotionCodes.ListAsync(options);
            if (!promoCodes.Data.Any())
            {
                return 0;
            }

            var promoCodeObj = promoCodes.Data.First();
            var coupon = promoCodeObj.Coupon;

            if (coupon.PercentOff.HasValue)
            {
                return coupon.PercentOff.Value / 100m;
            }

            if (coupon.AmountOff.HasValue)
            {
                return coupon.AmountOff.Value / 100m; // Convert from cents
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get discount for promo code {PromoCode}", promoCode);
            return 0;
        }
    }

    public async Task<List<(string PriceId, long UnitAmount, string Currency, string Interval)>> GetPricesAsync(IEnumerable<string> priceIds)
    {
        var results = new List<(string PriceId, long UnitAmount, string Currency, string Interval)>();

        try
        {
            foreach (var priceId in priceIds.Where(id => !string.IsNullOrEmpty(id)))
            {
                try
                {
                    var price = await _stripeClient.V1.Prices.GetAsync(priceId);
                    if (price != null && price.UnitAmount.HasValue)
                    {
                        var interval = price.Recurring?.Interval ?? "month";
                        results.Add((priceId, price.UnitAmount.Value, price.Currency, interval));
                    }
                }
                catch (StripeException ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch price {PriceId} from Stripe", priceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch prices from Stripe");
        }

        return results;
    }

    // Private helper methods
    private UserSubscriptionStatus MapStripeStatus(string stripeStatus)
    {
        return stripeStatus switch
        {
            "active" => UserSubscriptionStatus.Active,
            "past_due" => UserSubscriptionStatus.PastDue,
            "canceled" => UserSubscriptionStatus.Canceled,
            "unpaid" => UserSubscriptionStatus.Expired,
            "trialing" => UserSubscriptionStatus.Trialing,
            _ => UserSubscriptionStatus.Expired
        };
    }

    private PaymentStatus MapPaymentIntentStatus(string paymentIntentStatus)
    {
        return paymentIntentStatus switch
        {
            "succeeded" => PaymentStatus.Succeeded,
            "processing" => PaymentStatus.Pending,
            "requires_payment_method" => PaymentStatus.Failed,
            "requires_confirmation" => PaymentStatus.Pending,
            "requires_action" => PaymentStatus.Pending,
            "canceled" => PaymentStatus.Failed,
            _ => PaymentStatus.Failed
        };
    }
}