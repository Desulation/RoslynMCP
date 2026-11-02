using System;

namespace Sample.Core;

public static class PricingRules
{
    // Cyclomatic complexity 9: base 1, if +1, if +1, && +1, case +1, case +1, when +1, ?: +1, ?? +1.
    public static string Classify(Order order)
    {
        if (order is null)
        {
            throw new ArgumentNullException(nameof(order));
        }

        if (order.Total > 1000 && order.Status == OrderStatus.Paid)
        {
            return "vip";
        }

        switch (order.Status)
        {
            case OrderStatus.New:
                return "new";
            case OrderStatus.Cancelled when order.Total == 0:
                return "void";
            default:
                break;
        }

        var note = order.Note ?? "none";
        return order.Total > 100 ? "regular:" + note : "small:" + note;
    }
}
