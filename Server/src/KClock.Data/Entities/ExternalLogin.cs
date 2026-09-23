namespace KClock.Data.Entities;

public class ExternalLogin
{
    public required string Provider { get; set; }
    public required string Subject { get; set; }
    public Guid AccountId { get; set; }

    public Account? Account { get; set; }
}
