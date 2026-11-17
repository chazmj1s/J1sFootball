using Maui.RevenueCat.InAppBilling.Enums;
using Maui.RevenueCat.InAppBilling.Services;
using Microsoft.Extensions.Logging;

namespace SaturdayPulse.Services
{
    public enum SeasonPassPurchaseStatus
    {
        Purchased,
        Cancelled,
        Pending,
        Unavailable,
        NothingToRestore,
        Failed,

        /// <summary>The store refused to start the purchase on this device/account
        /// (not signed in to the store, no payment method, or purchases restricted).</summary>
        NotAllowed
    }

    /// <summary>What the UI should offer the user after a failed purchase.</summary>
    public enum SeasonPassRecoveryAction
    {
        /// <summary>Just show the message.</summary>
        None,

        /// <summary>Transient store/network problem - offer "Try Again".</summary>
        Retry,

        /// <summary>This store account already owns the pass - offer Restore Purchases.</summary>
        RestorePurchases,

        /// <summary>Store account/payment setup problem - send the user to the
        /// store's own account settings (Android) or tell them where to look (iOS).</summary>
        OpenStoreAccountSettings,

        /// <summary>Product/configuration problem on our side - offer support email.</summary>
        ContactSupport
    }

    /// <summary>Outcome of a Season Pass purchase or restore attempt. Message is
    /// safe to show to the user as-is. Action tells the UI which follow-up to offer.</summary>
    public sealed record SeasonPassPurchaseResult(
        SeasonPassPurchaseStatus Status,
        string Message,
        SeasonPassRecoveryAction Action = SeasonPassRecoveryAction.None);

    /// <summary>
    /// Thin wrapper over IRevenueCatBilling for the annual Season Pass
    /// subscription. Lazily initializes the SDK on first use (the wrapper
    /// requires initialization after app start, not in a constructor, and a
    /// purchase/restore tap is always after start). All runtime failures come
    /// back as a result; these methods do not throw.
    ///
    /// API keys: fill AppleApiKey / GoogleApiKey with the platform public SDK
    /// keys (RevenueCat > Project settings > API keys) once the real store apps
    /// are linked. Until then DEBUG builds fall back to the Test Store key;
    /// RELEASE builds with an empty platform key report purchases as
    /// unavailable rather than shipping the test key.
    /// </summary>
    public sealed class SeasonPassPurchaseService
    {
        private const string TestStoreApiKey = "test_yIOCSaMyXlYEJyFvJddUngPFqXi";
        private const string AppleApiKey = "appl_eyorCHPCWfmAtfVANIhnOFhXOGw";
        private const string GoogleApiKey = "";

        private const string OfferingId = "default";
        private const string PackageId = "$rc_annual";
        private const string EntitlementId = "j1s_sports_season_pass";

        private readonly IRevenueCatBilling _billing;
        private readonly ILogger<SeasonPassPurchaseService> _logger;
        private int _operationInProgress;

        public SeasonPassPurchaseService(
            IRevenueCatBilling billing,
            ILogger<SeasonPassPurchaseService> logger)
        {
            _billing = billing;
            _logger = logger;
        }

        private static string? GetApiKey()
        {
#if ANDROID
            return ResolveKey(GoogleApiKey);
#elif IOS
            return ResolveKey(AppleApiKey);
#else
            return null;
#endif
        }

        private static string? ResolveKey(string platformKey)
        {
            if (!string.IsNullOrEmpty(platformKey)) return platformKey;
#if DEBUG
            return TestStoreApiKey;
#else
            return null;
#endif
        }

        /// <param name="appUserId">
        /// The API's UserProfile.UserId for the logged-in account (JWT sub).
        /// Becomes the RevenueCat App User ID so the server-side webhook can
        /// attribute the purchase to the right UserEntitlement row.
        /// </param>
        public async Task<SeasonPassPurchaseResult> PurchaseSeasonPassAsync(string appUserId)
        {
            if (string.IsNullOrWhiteSpace(appUserId))
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "Please log in to purchase the Season Pass.");
            }

            if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "A purchase is already in progress.");
            }

            try
            {
                var failure = await EnsureIdentifiedAsync(appUserId);
                if (failure != null) return failure;

                var offeringsResult = await _billing.GetOfferings();
                var offerings = offeringsResult.Value;
                if (offeringsResult.IsError || offerings is null || offerings.Count == 0)
                {
                    _logger.LogError(
                        offeringsResult.ErrorException,
                        "RevenueCat GetOfferings failed or returned nothing. Error={Error}",
                        offeringsResult.Error);
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Unavailable,
                        "The Season Pass isn't available right now. Please try again later.",
                        SeasonPassRecoveryAction.Retry);
                }

                var offering = offerings.FirstOrDefault(o => o.Identifier == OfferingId)
                               ?? offerings.FirstOrDefault(o => o.IsCurrent);
                var package = offering?.AvailablePackages
                    .FirstOrDefault(p => p.Identifier == PackageId);

                if (package is null)
                {
                    _logger.LogError(
                        "RevenueCat package {PackageId} not found in offering {OfferingId}.",
                        PackageId, OfferingId);
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Unavailable,
                        "The Season Pass isn't available right now. Please try again later.",
                        SeasonPassRecoveryAction.ContactSupport);
                }

                var purchase = await _billing.PurchaseProduct(package);

                if (purchase.IsSuccess)
                {
                    var entitled = purchase.Value?.Entitlements
                        .Any(e => e.IsActive && e.Identifier == EntitlementId) ?? false;

                    if (entitled)
                    {
                        _logger.LogInformation(
                            "Season Pass purchase succeeded; entitlement {EntitlementId} active.",
                            EntitlementId);
                        return new SeasonPassPurchaseResult(
                            SeasonPassPurchaseStatus.Purchased,
                            "Purchase complete. Thank you!");
                    }

                    _logger.LogError(
                        "Purchase reported success but entitlement {EntitlementId} is not active.",
                        EntitlementId);
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Failed,
                        "Your purchase went through but the Season Pass didn't activate. Tap Restore Purchases, or contact support if it still doesn't appear.",
                        SeasonPassRecoveryAction.RestorePurchases);
                }

                if (purchase.Error == PurchaseErrorStatus.PurchaseCancelledError)
                {
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Cancelled,
                        "Purchase cancelled.");
                }

                if (purchase.Error == PurchaseErrorStatus.PaymentPendingError)
                {
                    _logger.LogInformation("Season Pass purchase pending (PaymentPendingError).");
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Pending,
                        "Your payment is pending approval. The Season Pass will activate once it completes.");
                }

                _logger.LogError(
                    purchase.ErrorException,
                    "Season Pass purchase failed. Error={Error}",
                    purchase.Error);

                return MapPurchaseFailure(purchase.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during Season Pass purchase.");
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "The purchase couldn't be completed. Please try again.",
                    SeasonPassRecoveryAction.Retry);
            }
            finally
            {
                Interlocked.Exchange(ref _operationInProgress, 0);
            }
        }

        /// <summary>
        /// Restores previous store purchases for the logged-in account
        /// (required by App Store review for auto-renewing subscriptions).
        /// Success means the Season Pass entitlement is active in RevenueCat;
        /// the server-side row is the caller's concern (profile refresh).
        /// </summary>
        public async Task<SeasonPassPurchaseResult> RestoreSeasonPassAsync(string appUserId)
        {
            if (string.IsNullOrWhiteSpace(appUserId))
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "Please log in to restore purchases.");
            }

            if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "Another purchase operation is already in progress.");
            }

            try
            {
                var failure = await EnsureIdentifiedAsync(appUserId);
                if (failure != null) return failure;

                _logger.LogInformation("Restoring purchases for {UserId}.", appUserId);

                // A restore that never completes would otherwise look like the
                // button doing nothing, so cap it.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var restore = await _billing.RestoreTransactions(timeout.Token);
                if (restore.IsError)
                {
                    _logger.LogError(
                        restore.ErrorException,
                        "RevenueCat RestoreTransactions failed. Error={Error}",
                        restore.Error);

                    var restoreMessage = restore.Error == PurchaseErrorStatus.PurchaseCancelledError
                        ? "Restoring took too long. Check your connection and try again."
                        : "We couldn't restore your purchases. Please try again.";

                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Failed,
                        restoreMessage);
                }

                var entitled = restore.Value?.Entitlements
                    .Any(e => e.IsActive && e.Identifier == EntitlementId) ?? false;

                return entitled
                    ? new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Purchased,
                        "Your Season Pass has been restored.")
                    : new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.NothingToRestore,
                        "No active Season Pass purchase was found for this store account.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during Season Pass restore.");
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "We couldn't restore your purchases. Please try again.");
            }
            finally
            {
                Interlocked.Exchange(ref _operationInProgress, 0);
            }
        }

        /// <summary>
        /// Initializes the SDK for this user, or switches the SDK to this user
        /// if it was already initialized for someone else. Returns null on
        /// success, or the failure result to hand back to the caller.
        /// </summary>
        private async Task<SeasonPassPurchaseResult?> EnsureIdentifiedAsync(string appUserId)
        {
            var apiKey = GetApiKey();
            if (apiKey is null)
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Unavailable,
                    "In-app purchases aren't available on this platform.");
            }

            if (!_billing.IsInitialized())
            {
                // Custom App User ID at init: RevenueCat never creates an
                // anonymous ($RCAnonymousID) customer for this user.
                _billing.Initialize(apiKey, appUserId);
                return null;
            }

            if (!string.Equals(_billing.GetAppUserId(), appUserId, StringComparison.Ordinal))
            {
                // Already initialized this session for a different (or
                // anonymous) user, e.g. after a logout/login as someone else.
                var loginResult = await _billing.Login(appUserId);
                if (loginResult.IsError)
                {
                    _logger.LogError(
                        loginResult.ErrorException,
                        "RevenueCat Login failed. Error={Error}",
                        loginResult.Error);
                    return new SeasonPassPurchaseResult(
                        SeasonPassPurchaseStatus.Failed,
                        "We couldn't verify your account with the store. Please try again.",
                        SeasonPassRecoveryAction.Retry);
                }
            }

            return null;
        }

        /// <summary>
        /// Maps a non-cancel, non-pending purchase failure to the message and
        /// follow-up action the UI should offer. Members confirmed in
        /// Kebechet 9 (PurchaseCancelledError, PaymentPendingError,
        /// ProductAlreadyPurchasedError, StoreProblemError, NetworkError) are
        /// compared directly; the remaining RevenueCat codes are matched by
        /// enum name so this compiles even if the wrapper omits or renames one.
        /// </summary>
        private static SeasonPassPurchaseResult MapPurchaseFailure(PurchaseErrorStatus? error)
        {
            // Kebechet 9 exposes Error as nullable; null (no code reported)
            // falls through to the generic Try Again result below.
            var name = error?.ToString() ?? string.Empty;

            // This store account already holds the subscription (possibly
            // bought under a different J1S login) - Restore applies it here.
            if (error == PurchaseErrorStatus.ProductAlreadyPurchasedError
                || name is "ReceiptAlreadyInUseError" or "ReceiptInUseByOtherSubscriberError")
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "This store account already owns the Season Pass. Tap Restore to apply it to this account.",
                    SeasonPassRecoveryAction.RestorePurchases);
            }

            // Store refused to start the purchase: not signed in to the store,
            // no usable payment method, or in-app purchases restricted.
            if (name is "PurchaseNotAllowedError" or "PaymentMethodNotAllowedError" or "InsufficientPermissionsError")
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.NotAllowed,
                    NotAllowedMessage(),
                    SeasonPassRecoveryAction.OpenStoreAccountSettings);
            }

            // Transient store or network problem.
            if (error == PurchaseErrorStatus.StoreProblemError
                || error == PurchaseErrorStatus.NetworkError
                || name is "OfflineConnectionError")
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Failed,
                    "The store couldn't complete the purchase. Check your connection and try again. If you were charged, tap Restore Purchases.",
                    SeasonPassRecoveryAction.Retry);
            }

            // Product or store configuration problem on our side - retrying
            // won't help.
            if (name is "ProductNotAvailableForPurchaseError"
                or "ConfigurationError"
                or "InvalidCredentialsError"
                or "PurchaseInvalidError")
            {
                return new SeasonPassPurchaseResult(
                    SeasonPassPurchaseStatus.Unavailable,
                    "The Season Pass isn't available for purchase right now. Please contact support.",
                    SeasonPassRecoveryAction.ContactSupport);
            }

            return new SeasonPassPurchaseResult(
                SeasonPassPurchaseStatus.Failed,
                "The purchase couldn't be completed. You were not charged. Please try again.",
                SeasonPassRecoveryAction.Retry);
        }

        private static string NotAllowedMessage()
        {
#if ANDROID
            return "Google Play couldn't start the purchase. Make sure you're signed in to the Play Store "
                 + "and have a valid payment method on your Google account, then try again.";
#elif IOS
            return "The App Store couldn't start the purchase. Make sure you're signed in under "
                 + "Settings > [your name] > Media & Purchases with a valid payment method, and that "
                 + "In-App Purchases aren't restricted in Settings > Screen Time > Content & Privacy Restrictions.";
#else
            return "Purchases aren't allowed on this device.";
#endif
        }
    }
}
