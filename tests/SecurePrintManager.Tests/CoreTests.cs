using SecurePrintManager.Core;
namespace SecurePrintManager.Tests;
public class CoreTests
{
    [Fact] public void Invalid_transition_is_rejected()
    {
        var j=new PrintJob{UserId=Guid.NewGuid(),PrinterName="P1",DocumentName="x.pdf",Pages=1};
        Assert.Throws<InvalidOperationException>(()=>j.TransitionTo(JobState.Completed));
    }
    [Fact] public async Task AES_GCM_round_trip()
    {
        var p=new AesGcmDocumentProtector(new byte[32]);
        await using var input=new MemoryStream("secret"u8.ToArray());
        var e=await p.ProtectAsync(input,default); await using var output=await p.UnprotectAsync(e,default);
        using var r=new StreamReader(output); Assert.Equal("secret",await r.ReadToEndAsync());
    }
}
