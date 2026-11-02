using System;
using Sample.Core;

namespace Sample.App;

public static class Program
{
    public static void Main()
    {
        var service = new OrderService<Order>(new OrderRepository());
        var order = new Order(1, 250m, OrderStatus.Paid);

        if (service.Validate(order))
        {
            service.Save(order);
        }

        Console.WriteLine(PricingRules.Classify(order));
    }
}
