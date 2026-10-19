using Biatec.Chat.Api.Evidence.Recording;
using Biatec.Chat.Core.Evidence;

namespace Biatec.Chat.Tests.Evidence.Recording;

public class ConsentReceiptPayloadTests
{
    [Fact]
    public void ConsentReceipt_payload_keeps_receipt_verbatim_and_kind_is_stable()
    {
        var receipt = "{\"kid\":\"dev-1\",\"sig\":\"AAAA\"}";
        var p = EvidencePayloads.ConsentReceipt(Guid.NewGuid(), Guid.NewGuid(), "req-1", "hash", receipt);
        Assert.Equal(receipt, (string?)p["receipt"]);
        Assert.Equal("req-1", (string?)p["authReqId"]);
        Assert.Equal("consent_receipt", EvidenceKinds.ConsentReceipt);
    }
}
