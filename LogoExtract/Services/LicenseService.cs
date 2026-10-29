using LogoExtract.Core.Licensing;
using Plugin.InAppBilling;

namespace LogoExtract.Services;

/// <summary><see cref="ILicenseService"/> backed by Google Play Billing (managed, non-consumable product), via
/// the <c>Plugin.InAppBilling</c> library (MIT) -- same package/pattern as the sibling PdfReader/Minesweeper
/// apps' own LicenseService, adapted to this app (owner's request, 2026-10-29, "thêm chức năng mua bản quyền
/// mua pro giống như các app khác"). LogoExtract's own Pro unlock is just "removes ads" (there is no feature
/// gate elsewhere in this app, unlike DocScanner's trial-counter shape), so there is nothing else to unlock.</summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>Product id of the "remove ads" unlock.
    /// TODO: replace with the real product id once created in Play Console &gt; Monetize &gt; Products &gt;
    /// In-app products -- purchases against this placeholder id will never actually succeed until that product
    /// exists (and this app has been uploaded to a Play Console testing track with a licensed tester account),
    /// same as every sibling app's own identical TODO.</summary>
    public const string ProProductId = "logoextract_pro_remove_ads";

    private const string CachedIsProKey = "license_is_pro_cached";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseService()
    {
        // Cached, instant, no network -- read synchronously so App's own constructor can set
        // IAdsClient.AreAdsEnabled correctly from the very first frame, before RefreshAsync's own Play Billing
        // round-trip has had any chance to complete.
        State = new LicenseState(Preferences.Default.Get(CachedIsProKey, false));
    }

    public LicenseState State { get; private set; }
    public string? ProPriceText { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await LockedRefreshAsync();
        Changed?.Invoke();
    }

    public async Task<PurchaseOutcome> PurchaseProAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play. Kiểm tra mạng rồi thử lại.";
                Changed?.Invoke();
                return PurchaseOutcome.Error;
            }

            InAppBillingPurchase? purchase;
            try
            {
                purchase = await billing.PurchaseAsync(ProProductId, ItemType.InAppPurchase);
            }
            finally
            {
                await billing.DisconnectAsync();
            }

            if (purchase == null) return PurchaseOutcome.Cancelled;
            await LockedRefreshAsync();
            Changed?.Invoke();
            return PurchaseOutcome.Purchased;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.UserCancelled)
        {
            return PurchaseOutcome.Cancelled;
        }
        catch (InAppBillingPurchaseException ex) when (ex.PurchaseError == PurchaseError.AlreadyOwned)
        {
            await LockedRefreshAsync();
            Changed?.Invoke();
            return PurchaseOutcome.AlreadyOwned;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Changed?.Invoke();
            return PurchaseOutcome.Error;
        }
    }

    public async Task<PurchaseOutcome> RestoreAsync()
    {
        bool wasPro = State.IsPro;
        await RefreshAsync();
        if (LastError != null) return PurchaseOutcome.Error;
        return State.IsPro ? (wasPro ? PurchaseOutcome.AlreadyOwned : PurchaseOutcome.Purchased) : PurchaseOutcome.Cancelled;
    }

    private async Task LockedRefreshAsync()
    {
        await _gate.WaitAsync();
        try { await RefreshLockedAsync(); }
        finally { _gate.Release(); }
    }

    private async Task RefreshLockedAsync()
    {
        IInAppBilling billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync())
            {
                LastError = "Không kết nối được với Google Play.";
                return;
            }
            try
            {
                IEnumerable<InAppBillingPurchase>? purchases = await billing.GetPurchasesAsync(ItemType.InAppPurchase);
                InAppBillingPurchase? owned = purchases?.FirstOrDefault(p => p.ProductId == ProProductId && p.State == PurchaseState.Purchased);
                if (owned is { IsAcknowledged: false })
                {
                    try { await billing.FinalizePurchaseOfProductAsync([ProProductId]); }
                    catch (Exception) { /* still counts as owned; retried next refresh */ }
                }
                Preferences.Default.Set(CachedIsProKey, owned != null);
                State = new LicenseState(owned != null);

                IEnumerable<InAppBillingProduct>? products = await billing.GetProductInfoAsync(ItemType.InAppPurchase, [ProProductId]);
                ProPriceText = products?.FirstOrDefault(p => p.ProductId == ProProductId)?.LocalizedPrice;
                LastError = null;
            }
            finally
            {
                await billing.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}
