using System.Text.Json.Nodes;
using NexusPort.Shared.Exceptions;

namespace NexusPort.Modules.Booking.Application.Services;

public sealed partial class ExportBookingService
{
    /// <summary>
    /// Booking chiều xuất được hệ thống tự động duyệt ngay khi chủ hàng gửi, không chờ hãng tàu duyệt thủ công.
    /// Gọi trong cùng transaction với việc tạo hoặc gửi lại booking; kiểm tra chuyến còn mở như bước duyệt thủ công.
    /// </summary>
    private async Task AutoApprove(Guid id, CancellationToken ct)
    {
        var b = await Booking(id, true, ct);
        var v = await Voyage(G(b, "voyageId"), true, ct);
        Require(S(v, "status") == "Scheduled" && D(v, "vgmCutoff") > DateTime.UtcNow, "Voyage", "Chuyến đã đóng hoặc quá hạn khai container.");
        var submitted = b["submission"] != null;
        await Exec("UPDATE export_bookings SET status=@p0,port_status=@p1,version=version+1,updated_at=now() WHERE id=@p2", ct,
            submitted ? "ContainersDeclared" : "Approved", submitted ? "Accepted" : "WaitingContainers", id);
        if (submitted) await ForwardSubmission(b, ct);
        await History(id, G(b, "voyageId"), "BookingApproved", "Hệ thống tự động duyệt booking và gửi thông tin dự kiến sang cảng.", ct);
        await Deliver(id, G(b, "voyageId"), I(b, "version") + 1, "BookingApproved", new { bookingId = id, quantity = I(b, "quantity"), containerType = S(b, "containerTypeCode") }, ct);
        if (submitted) await Deliver(id, G(b, "voyageId"), I(b, "version") + 1, "ContainerListUpdated", await Containers(id, ct), ct);
    }
}
