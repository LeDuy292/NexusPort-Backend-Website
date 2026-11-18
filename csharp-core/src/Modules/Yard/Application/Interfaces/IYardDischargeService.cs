using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexusPort.Modules.Yard.Application.DTOs;

namespace NexusPort.Modules.Yard.Application.Interfaces;

public interface IYardDischargeService
{
    /// <summary>
    /// Chạy thuật toán phân bổ ô bãi thông minh (Pre-allocation) khi duyệt Manifest nhập.
    /// Khóa trạng thái ô sang 'reserved' và lưu vị trí dự kiến cho từng container.
    /// </summary>
    Task<VesselDischargePlanDto> PreAllocateManifestAsync(Guid manifestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các kế hoạch dỡ tàu (chiều nhập) đang hiệu lực cho Điều độ cảng (Dispatcher).
    /// </summary>
    Task<IReadOnlyList<VesselDischargePlanDto>> GetActiveDischargePlansAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy chi tiết kế hoạch dỡ tàu theo ManifestId.
    /// </summary>
    Task<VesselDischargePlanDto?> GetDischargePlanByIdAsync(Guid manifestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tally quét container tại chân cẩu: đối chiếu manifest, nhận diện ô bãi đã đặt trước,
    /// ghi nhận tình trạng vỏ (EIR) và mốc giờ dỡ hàng.
    /// </summary>
    Task<TallyScanResultDto> TallyScanAsync(TallyScanRequestDto dto, string inspectorName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cho phép Dispatcher điều chỉnh thủ công ô bãi dự kiến cho một container.
    /// </summary>
    Task<VesselDischargePlanDto> OverrideSlotAsync(OverrideSlotRequestDto dto, CancellationToken cancellationToken = default);
}
