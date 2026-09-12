namespace OrderFlow.Infrastructure.Domain;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, Guid id)
        : base($"{entity} '{id}' was not found.")
    {
        Entity = entity;
        Id = id;
    }

    public string Entity { get; }

    public Guid Id { get; }
}
