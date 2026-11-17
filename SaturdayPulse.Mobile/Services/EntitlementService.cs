using SaturdayPulse.Helpers;
using SaturdayPulse.Models;

namespace SaturdayPulse.Services
{
    /// <summary>
    /// Singleton service that owns Season Pass / admin entitlement state for
    /// the whole app — same shape as FollowService: an in-memory cache other
    /// ViewModels read synchronously (IsLoggedIn/HasSeasonPass/IsAdmin),
    /// updated whenever a fresh UserProfileDto comes back from the server,
    /// and an EntitlementChanged event so PowerRankingsViewModel/
    /// ScheduleViewModel/MyTeamsViewModel/PostseasonViewModel/
    /// SandboxViewModel can react to entitlement changes without depending
    /// on SettingsViewModel directly (2026-07-25 design decision).
    ///
    /// This does NOT independently poll the API. SettingsViewModel already
    /// owns the login / create-account / dev-toggle / passive-startup-load
    /// flows and has the UserProfileDto in hand at the end of each — it
    /// just also calls ApplyProfile here so every other ViewModel finds out.
    /// Logout calls Clear().
    ///
    /// EnsureLoggedInForPurchaseAsync holds the Season Pass "log in first"
    /// check that used to live entirely inside SettingsViewModel.SeasonPassCommand.
    /// Pulling it out here means the Settings button and the new gated
    /// Details paywall message (MyTeamsViewModel) share the exact same
    /// login-check logic. PurchaseSeasonPassAsync is the matching single
    /// entry point for the store purchase itself (RevenueCat).
    ///
    /// PurchaseWithRecoveryAsync / RestoreWithPromptAsync (2026-11-12) are
    /// the full user-facing flows - login gate, store call, failure
    /// recovery prompts (Try Again / Restore / store settings / support),
    /// and the post-purchase profile refresh - so every paywall entry point
    /// (Settings, My Teams, and any future gate) behaves identically.
    /// Every profile fetched along the way is applied here and raised via
    /// ProfileRefreshed so SettingsViewModel can update its own bound
    /// fields even when the purchase started on another tab.
    /// </summary>
    public class EntitlementService
    {
        private readonly AuthService _authService;
        private readonly UserApiService _userApi;
        private readonly SeasonPassPurchaseService _purchaseService;

        private bool _isLoggedIn;
        private bool _hasSeasonPass;
        private bool _isAdmin;
        private string? _userId;

        public bool IsLoggedIn => _isLoggedIn;
        public bool HasSeasonPass => _hasSeasonPass;
        public bool IsAdmin => _isAdmin;

        /// <summary>The API-side UserId (JWT sub) of the logged-in account; null when logged out.</summary>
        public string? UserId => _userId;

        /// <summary>Fires whenever IsLoggedIn, HasSeasonPass, or IsAdmin changes.</summary>
        public event Action? EntitlementChanged;

        /// <summary>
        /// Fires with each profile this service fetches itself during the
        /// purchase/restore flows (login inside the flow, post-purchase
        /// refresh). Not raised by ApplyProfile, so a subscriber that calls
        /// ApplyProfile from its handler (SettingsViewModel) can't loop.
        /// Always raised on the main thread.
        /// </summary>
        public event Action<UserProfileDto>? ProfileRefreshed;

        private const string StoreSettingsUrl = "https://play.google.com/store/paymentmethods";

        public EntitlementService(
            AuthService authService,
            UserApiService userApi,
            SeasonPassPurchaseService purchaseService)
        {
            _authService = authService;
            _userApi = userApi;
            _purchaseService = purchaseService;
        }

        /// <summary>
        /// Call whenever a fresh profile comes back from the server — login,
        /// create account, passive startup fetch, or the admin dev-entitlement
        /// toggle. Safe to call repeatedly; only raises EntitlementChanged if
        /// something actually changed.
        /// </summary>
        public void ApplyProfile(UserProfileDto profile)
        {
            var changed = !_isLoggedIn
                || _hasSeasonPass != profile.IsEntitled
                || _isAdmin != profile.IsAdmin;

            _isLoggedIn = true;
            _hasSeasonPass = profile.IsEntitled;
            _isAdmin = profile.IsAdmin;
            _userId = string.IsNullOrWhiteSpace(profile.UserId) ? null : profile.UserId;

            if (changed) EntitlementChanged?.Invoke();
        }

        /// <summary>Call on logout.</summary>
        public void Clear()
        {
            var changed = _isLoggedIn || _hasSeasonPass || _isAdmin;

            _isLoggedIn = false;
            _hasSeasonPass = false;
            _isAdmin = false;
            _userId = null;

            if (changed) EntitlementChanged?.Invoke();
        }

        /// <summary>
        /// Season Pass purchase entry point. If nobody's logged in, offers
        /// to log in first (same prompt SettingsViewModel's original
        /// SeasonPassCommand showed); returns whether the caller can proceed
        /// to the purchase flow (PurchaseSeasonPassAsync), plus the
        /// freshly-fetched profile if a login just happened — callers that
        /// keep their own local copy of profile fields (SettingsViewModel)
        /// should apply FreshProfile themselves; callers that only read
        /// through this service (MyTeamsViewModel) don't need to do anything
        /// else, since ApplyProfile was already called internally.
        /// </summary>
        public async Task<SeasonPassLoginResult> EnsureLoggedInForPurchaseAsync()
        {
            if (_isLoggedIn) return new SeasonPassLoginResult(true, null);

            var proceed = await Shell.Current.DisplayAlert(
                "Season Pass",
                "You'll need an account to purchase a Season Pass.",
                "Log In", "Cancel");
            if (!proceed) return new SeasonPassLoginResult(false, null);

            var authOk = await _authService.LoginAsync(isSignup: false);
            if (!authOk) return new SeasonPassLoginResult(false, null);

            // Same first-login handling as SettingsViewModel.TryLoginAsync: Auth0's
            // hosted page can't tell a first-time Apple/Google login from a
            // returning one, so a login with no profile creates one (the server's
            // email-uniqueness check still blocks duplicates).
            var profile = await _userApi.GetMeAsync();
            if (profile == null)
            {
                var outcome = await _userApi.CreateAccountAsync(_authService.LastLoginEmail);

                if (outcome.IsSuccess && outcome.Profile != null)
                {
                    profile = outcome.Profile;
                }
                else
                {
                    // GetMeAsync returns null for any failure, not only a 404, so a
                    // conflict can mean the account exists and the first lookup
                    // failed. Re-check once before treating it as a real conflict.
                    profile = await _userApi.GetMeAsync();

                    if (profile == null)
                    {
                        if (outcome.IsConflict)
                        {
                            await _authService.LogoutAsync();
                            var reason = (outcome.ConflictMessage ?? "That account can't be used.").Trim('"');
                            await Shell.Current.DisplayAlert(
                                "Season Pass",
                                $"{reason} Log in with the sign-in method you used originally.",
                                "OK");
                        }
                        else
                        {
                            // End the Auth0 session so the next attempt asks for credentials
                            // instead of silently reusing this identity and failing again.
                            await _authService.LogoutAsync();
                            await Shell.Current.DisplayAlert(
                                "Season Pass",
                                "Couldn't reach the server — check your connection and try again.",
                                "OK");
                        }

                        return new SeasonPassLoginResult(false, null);
                    }
                }
            }

            ApplyProfile(profile);
            return new SeasonPassLoginResult(true, profile);
        }

        /// <summary>
        /// Runs the store purchase for the annual Season Pass. Call only
        /// after EnsureLoggedInForPurchaseAsync returned CanProceed = true.
        /// Does not throw; failures are reported in the result.
        /// </summary>
        public Task<SeasonPassPurchaseResult> PurchaseSeasonPassAsync()
            => _purchaseService.PurchaseSeasonPassAsync(_userId ?? string.Empty);

        /// <summary>
        /// Restores previous store purchases for the logged-in account.
        /// Does not throw; failures are reported in the result.
        /// </summary>
        public Task<SeasonPassPurchaseResult> RestoreSeasonPassAsync()
            => _purchaseService.RestoreSeasonPassAsync(_userId ?? string.Empty);

        // ── Full user-facing flows (shared by every paywall) ──────────────

        /// <summary>
        /// Complete Season Pass purchase flow for any paywall: login gate,
        /// store purchase, failure recovery prompts, post-purchase profile
        /// refresh. Try Again loops back into the purchase; Restore runs
        /// RestoreWithPromptAsync; store-account problems point at the
        /// store's own settings; configuration problems offer support email
        /// when <paramref name="supportEmail"/> is known (OK-only otherwise).
        /// Returns true when the Season Pass ended up active (purchased, or
        /// restored from the recovery prompt). Does not throw.
        /// </summary>
        public async Task<bool> PurchaseWithRecoveryAsync(string? supportEmail = null)
        {
            try
            {
                var login = await EnsureLoggedInForPurchaseAsync();
                if (!login.CanProceed) return false;
                if (login.FreshProfile != null) RaiseProfileRefreshed(login.FreshProfile);

                while (true)
                {
                    var purchase = await PurchaseSeasonPassAsync();

                    if (purchase.Status == SeasonPassPurchaseStatus.Cancelled) return false;

                    if (purchase.Status == SeasonPassPurchaseStatus.Purchased)
                    {
                        await Shell.Current.DisplayAlert("Season Pass", purchase.Message, "OK");
                        await RefreshProfileAfterPurchaseAsync();
                        return true;
                    }

                    // Pending (Ask to Buy / slow card) is not a failure - don't
                    // log it as one.
                    if (purchase.Status != SeasonPassPurchaseStatus.Pending)
                    {
                        AppLogger.Log(
                            $"[SeasonPass] Purchase result: {purchase.Status} / {purchase.Action}",
                            SaturdayPulse.Helpers.LogLevel.Error);
                    }

                    var next = await ShowRecoveryPromptAsync(purchase, supportEmail);
                    switch (next)
                    {
                        case RecoveryChoice.Retry:
                            continue;
                        case RecoveryChoice.Restore:
                            return await RestoreWithPromptAsync();
                        default:
                            return false;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[SeasonPass] Purchase flow failed: {ex}", SaturdayPulse.Helpers.LogLevel.Error);
                await Shell.Current.DisplayAlert(
                    "Season Pass",
                    "Something went wrong starting the purchase. Please try again.",
                    "OK");
                return false;
            }
        }

        /// <summary>
        /// Complete Restore Purchases flow: login gate, store restore, result
        /// alert, post-restore profile refresh. Returns true when the Season
        /// Pass was restored. Does not throw.
        /// </summary>
        public async Task<bool> RestoreWithPromptAsync()
        {
            try
            {
                var login = await EnsureLoggedInForPurchaseAsync();
                if (!login.CanProceed) return false;
                if (login.FreshProfile != null) RaiseProfileRefreshed(login.FreshProfile);

                System.Diagnostics.Debug.WriteLine("[Restore] Starting restore.");
                var restore = await RestoreSeasonPassAsync();
                System.Diagnostics.Debug.WriteLine($"[Restore] Finished: {restore.Status} - {restore.Message}");

                if (restore.Status == SeasonPassPurchaseStatus.Failed)
                {
                    AppLogger.Log(
                        $"[SeasonPass] Restore result: {restore.Status} / {restore.Message}",
                        SaturdayPulse.Helpers.LogLevel.Error);
                }

                await Shell.Current.DisplayAlert("Restore Purchases", restore.Message, "OK");

                if (restore.Status != SeasonPassPurchaseStatus.Purchased) return false;

                await RefreshProfileAfterPurchaseAsync();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[SeasonPass] Restore flow failed: {ex}", SaturdayPulse.Helpers.LogLevel.Error);
                await Shell.Current.DisplayAlert(
                    "Restore Purchases",
                    "Something went wrong restoring purchases. Please try again.",
                    "OK");
                return false;
            }
        }

        private enum RecoveryChoice
        {
            Done,
            Retry,
            Restore
        }

        /// <summary>
        /// Shows the failure message with the action-specific button and
        /// performs any side action (open store settings / mail app) itself.
        /// Returns whether the caller should retry or restore.
        /// </summary>
        private async Task<RecoveryChoice> ShowRecoveryPromptAsync(
            SeasonPassPurchaseResult result, string? supportEmail)
        {
            const string title = "Season Pass";

            switch (result.Action)
            {
                case SeasonPassRecoveryAction.Retry:
                    return await Shell.Current.DisplayAlert(title, result.Message, "Try Again", "Cancel")
                        ? RecoveryChoice.Retry
                        : RecoveryChoice.Done;

                case SeasonPassRecoveryAction.RestorePurchases:
                    return await Shell.Current.DisplayAlert(title, result.Message, "Restore", "Cancel")
                        ? RecoveryChoice.Restore
                        : RecoveryChoice.Done;

                case SeasonPassRecoveryAction.OpenStoreAccountSettings:
#if ANDROID
                    if (await Shell.Current.DisplayAlert(title, result.Message, "Open Google Play", "Close"))
                    {
                        await TryOpenUriAsync(new Uri(StoreSettingsUrl));
                    }
#else
                    // iOS: no supported deep link into Apple Account billing;
                    // the message says where to look.
                    await Shell.Current.DisplayAlert(title, result.Message, "OK");
#endif
                    return RecoveryChoice.Done;

                case SeasonPassRecoveryAction.ContactSupport:
                    if (!string.IsNullOrWhiteSpace(supportEmail))
                    {
                        if (await Shell.Current.DisplayAlert(title, result.Message, "Email Support", "Close"))
                        {
                            var subject = Uri.EscapeDataString("J1S Season Pass purchase problem");
                            await TryOpenUriAsync(new Uri($"mailto:{supportEmail}?subject={subject}"));
                        }
                    }
                    else
                    {
                        await Shell.Current.DisplayAlert(title, result.Message, "OK");
                    }
                    return RecoveryChoice.Done;

                default:
                    await Shell.Current.DisplayAlert(title, result.Message, "OK");
                    return RecoveryChoice.Done;
            }
        }

        private static async Task TryOpenUriAsync(Uri uri)
        {
            try
            {
                await Launcher.Default.OpenAsync(uri);
            }
            catch (Exception ex)
            {
                AppLogger.Log(
                    $"[SeasonPass] Couldn't open {uri.Scheme} link: {ex.Message}",
                    SaturdayPulse.Helpers.LogLevel.Error);
            }
        }

        /// <summary>
        /// After a purchase or restore, the store confirms immediately but the
        /// server entitlement row is written by the RevenueCat webhook a few
        /// seconds later. Re-fetches the profile until a store-backed
        /// entitlement shows as active (or attempts run out), applying each
        /// result so every gated screen updates as soon as the row lands.
        /// Moved here from SettingsViewModel (2026-11-12) so purchases from
        /// any tab get the same refresh.
        /// </summary>
        private async Task RefreshProfileAfterPurchaseAsync()
        {
            const int maxAttempts = 6;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2));

                var profile = await _userApi.GetMeAsync();
                if (profile == null) continue;

                RaiseProfileRefreshed(profile);

                var storeBackedActive = profile.Entitlements.Any(e =>
                    e.IsActive && e.Source is "apple" or "google" or "revenuecat-test");
                if (storeBackedActive) return;
            }
        }

        /// <summary>Applies the profile to this service, then notifies
        /// subscribers on the main thread.</summary>
        private void RaiseProfileRefreshed(UserProfileDto profile)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ApplyProfile(profile);
                ProfileRefreshed?.Invoke(profile);
            });
        }
    }

    /// <summary>Result of EnsureLoggedInForPurchaseAsync — CanProceed tells the
    /// caller whether to continue to the purchase flow; FreshProfile is
    /// non-null only when a new login happened during this call.</summary>
    public readonly record struct SeasonPassLoginResult(bool CanProceed, UserProfileDto? FreshProfile);
}
