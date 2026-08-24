using SecurePrintManager.Database;

namespace SecurePrintManager.Tests;

public class CoreTests
{
    [Fact]
    public void PrintJob_defaults_to_hold()
    {
        var job = new PrintJob
        {
            UserId = 1,
            PrinterName = "P1",
            DocumentName = "x.pdf",
            Pages = 1
        };

        Assert.Equal("HOLD", job.Status);
        Assert.Equal(1, job.Pages);
    }

    [Fact]
    public void AuditLog_defaults_have_empty_hash_and_action()
    {
        var log = new AuditLog();

        Assert.Empty(log.Action);
        Assert.Empty(log.CurrentHash);
        Assert.Null(log.PreviousHash);
    }
}
