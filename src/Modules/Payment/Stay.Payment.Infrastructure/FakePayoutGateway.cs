using System.Collections.Concurrent;
using Stay.Payment.Contracts;

namespace Stay.Payment.Infrastructure;

/// <summary>
/// Local fake for Razorpay Route payouts (no SDK in Stay, §9). Pays successfully and is idempotent by
/// key — replaying the same idempotency key returns the original reference (BR-5). A real Route adapter
/// replaces it in higher environments. Tests can force a failure for a given key.
/// </summary>
public sealed class FakePayoutGateway : IPayoutGateway
{
    private readonly ConcurrentDictionary<string, string> _byKey = new();
    private readonly HashSet<string> _failKeys = [];

    /// <summary>Forces the next payout with this idempotency key to fail (test/dev hook).</summary>
    public void FailKey(string idempotencyKey) => _failKeys.Add(idempotencyKey);

    public Task<PayoutResult> PayoutAsync(PayoutInstruction instruction, CancellationToken ct = default)
    {
        if (_failKeys.Contains(instruction.IdempotencyKey))
            return Task.FromResult(PayoutResult.Failed("Route payout declined (fake)."));

        var pspRef = _byKey.GetOrAdd(instruction.IdempotencyKey, k => $"fake_payout_{k}");
        return Task.FromResult(PayoutResult.Ok(pspRef));
    }
}
