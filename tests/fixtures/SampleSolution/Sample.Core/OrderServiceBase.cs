namespace Sample.Core;

public abstract class OrderServiceBase
{
    public virtual bool Validate(Order order) => order.Total >= 0;
}
