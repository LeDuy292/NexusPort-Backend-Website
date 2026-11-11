using System.Security.Cryptography;
using System.Text;
using NexusPort.Modules.ServiceCatalog.Domain;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    // Customer attestation is bound to the actor, document and exact selected
    // occurrences. It does not write customs release or gate permissions.
    private string CustomsConfirmationScope(Guid draftId, ServiceDraftInput input) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(draftId.ToString("N") + "|" + Actor.ToString("N") + "|" + SourceHash(input) + "|" +
            string.Join(",", input.Lines.Select(line => line.SourceContainerId).Order().Select(id => id.ToString("N"))))));
    private bool HasCustomsConfirmation(Guid draftId, ServiceDraftInput input) => input.Lines.Length > 0
        && input.Delivery.CustomsConfirmationScope == CustomsConfirmationScope(draftId, input);

    public async Task<object> ConfirmRegistrationCustoms(Guid id, ServiceDraftInput request, CancellationToken ct)
    {
        var company = await Company(ct);
        await GuardEmailDraft(id, company, ct);
        var input = ServiceDraftRules.Normalize(request);
        Require(input.Lines.Length > 0, "Chọn ít nhất một container trước khi xác nhận thông quan.");
        var snapshots = await Snapshots(company, input, ct);
        return new { confirmationScope = CustomsConfirmationScope(id, input), provider = "CUSTOMER_CONFIRMATION",
            confirmedBy = Actor, confirmedAt = DateTimeOffset.UtcNow, vasscmConnected = false,
            items = snapshots.Containers.Select(container => new { sourceContainerId = G(container!, "sourceContainerId"),
                containerNumber = S(container, "containerNumber"), status = "CUSTOMER_CONFIRMED" }),
            notice = "Khách hàng đã xác nhận thông quan cho các container được chọn. Cảng đối chiếu điều kiện giao nhận thực tế." };
    }
}
