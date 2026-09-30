namespace JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;

internal sealed class EmailDeliverySlotEntity
{
    public string Key { get; set; } = string.Empty;
    public Guid DeliveryId { get; set; }
    public string? Source { get; set; }
    public Guid Version { get; set; }
}
