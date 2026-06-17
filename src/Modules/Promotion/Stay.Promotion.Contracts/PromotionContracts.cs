namespace Stay.Promotion.Contracts;

/// <summary>Body for <c>POST /api/v1/admin/promotions</c> — create a platform or host promotion.</summary>
public sealed record CreatePromotionRequest(
    string OwnerType, long? OwnerId, string Name, string Type, decimal Value,
    decimal? MinAmount, decimal? Budget, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

/// <summary>A created promotion.</summary>
public sealed record PromotionResponse(long Id, string Name, string Type, string Status);

/// <summary>Body for <c>POST /api/v1/admin/promotions/{id}/coupons</c> — issue a redeemable code.</summary>
public sealed record IssueCouponRequest(string Code, int? MaxRedemptions, int? PerUserLimit);

/// <summary>A coupon code.</summary>
public sealed record CouponResponse(long Id, string Code, string Status);

/// <summary>Body for <c>POST /api/v1/coupons/apply</c> — preview a coupon's effect on an amount.</summary>
public sealed record ApplyCouponRequest(string Code, decimal Amount, string Currency);

/// <summary>The discount a coupon yields on an amount, and the resulting net (the funnel shows net before pay).</summary>
public sealed record DiscountQuote(long CouponId, decimal DiscountAmount, decimal NetAmount, string Currency);
