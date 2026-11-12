using NexusPort.Modules.Gate.Application.DTOs;

namespace NexusPort.Modules.Gate.Application.Interfaces;

public sealed class ContainerOnnxInferenceResult
{
    public bool ModelLoaded { get; set; }
    public double ProcessingTimeMs { get; set; }
    public string OcrProvider { get; set; } = "None";
    public string? DetectedContainerNumber { get; set; }
    public string? RawIdText { get; set; }
    public double IdConfidence { get; set; }
    public bool Iso6346Valid { get; set; }
    public string? Iso6346Error { get; set; }
    public string? DetectedContainerType { get; set; }
    public string? RawTypeText { get; set; }
    public double TypeConfidence { get; set; }
    public string? AnnotatedImageBase64 { get; set; }
    public IReadOnlyList<ContainerAiDetectionDto> Detections { get; set; } = Array.Empty<ContainerAiDetectionDto>();
    public string? ErrorMessage { get; set; }
}

public interface IContainerDetectionService
{
    ContainerModelStatusDto GetModelStatus();
    Task<ContainerOnnxInferenceResult> DetectAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}

public interface IGateContainerInspectionService
{
    ContainerModelStatusDto GetModelStatus();
    Task<ContainerInspectionResultDto> InspectImageAsync(byte[] imageBytes, ContainerInspectionQuery query, CancellationToken cancellationToken = default);
    Task<ContainerVerifyDecisionResultDto> RecordContainerDecisionAsync(ContainerVerifyDecisionRequestDto request, CancellationToken cancellationToken = default);
}

