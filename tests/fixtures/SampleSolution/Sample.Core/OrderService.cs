using System;

namespace Sample.Core;

public class OrderService<T> : OrderServiceBase where T : Order
{
    private readonly IOrderRepository _repository;

    public OrderService(IOrderRepository repository)
    {
        _repository = repository;
    }

    public int SavedCount { get; private set; }

    public override bool Validate(Order order) => base.Validate(order) && order.Id > 0;

    public void Save(T order)
    {
        if (!Validate(order))
        {
            throw new ArgumentException("Invalid order", nameof(order));
        }

        _repository.Store(order);
        SavedCount++;
    }
}
