namespace Sample.Core;

public interface IOrderRepository
{
    void Store(Order order);

    Order? Find(int id);
}
