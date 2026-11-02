using System.Collections.Generic;

namespace Sample.Core;

public class OrderRepository : IOrderRepository
{
    private readonly Dictionary<int, Order> _orders = new Dictionary<int, Order>();

    public void Store(Order order)
    {
        _orders[order.Id] = order;
    }

    public Order? Find(int id)
    {
        return _orders.TryGetValue(id, out var order) ? order : null;
    }
}
