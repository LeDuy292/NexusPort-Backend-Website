using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NexusPort.Modules.Gate.Application.DTOs;
using NexusPort.Modules.Gate.Application.Interfaces;
using NexusPort.Modules.Gate.Domain.Validation;
using OpenCvSharp;

namespace NexusPort.Modules.Gate.Infrastructure.Services;

/// <summary>
/// Dịch vụ AI nhận diện vùng Mã Container (ID) và Loại Container (Type) từ model YOLOv8/YOLO11 ONNX (container.onnx),
/// kết hợp OpenCvSharp4 cắt vùng quan tâm (ROI) và OCR + kiểm tra chuẩn ISO 6346 phục vụ kiểm duyệt cổng (Gate).
/// </summary>
public sealed class ContainerOnnxDetectionService : IContainerDetectionService, IDisposable
{
    private const int ModelInputSize = 640;
    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [0] = "ID",
        [1] = "Type"
    };

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ContainerOnnxDetectionService> _logger;
    private readonly float _confidenceThreshold;
    private readonly float _nmsIouThreshold;
    private readonly string? _azureEndpoint;
    private readonly string? _azureKey;
    private readonly string _ocrSpaceApiKey;

    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private string? _resolvedModelPath;
    private string? _loadError;
    private bool _disposed;

    public ContainerOnnxDetectionService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ContainerOnnxDetectionService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _confidenceThreshold = float.TryParse(configuration["ContainerDetection:ConfidenceThreshold"], out var conf)
            ? Math.Clamp(conf, 0.05f, 0.95f)
            : 0.25f;
        _nmsIouThreshold = float.TryParse(configuration["ContainerDetection:NmsIouThreshold"], out var iou)
            ? Math.Clamp(iou, 0.1f, 0.9f)
            : 0.45f;

        _azureEndpoint = (configuration["AZURE_VISION_ENDPOINT"] ?? Environment.GetEnvironmentVariable("AZURE_VISION_ENDPOINT"))?.Trim().TrimEnd('/');
        _azureKey = (configuration["AZURE_VISION_KEY"] ?? Environment.GetEnvironmentVariable("AZURE_VISION_KEY"))?.Trim();
        _ocrSpaceApiKey = (configuration["ContainerDetection:OcrSpaceApiKey"] ?? "K86333912188957").Trim();
    }

    public ContainerModelStatusDto GetModelStatus()
    {
        var session = EnsureSessionLoaded();
        var exists = !string.IsNullOrEmpty(_resolvedModelPath) && File.Exists(_resolvedModelPath);
        long? sizeBytes = exists ? new FileInfo(_resolvedModelPath!).Length : null;

        var inputName = "images";
        var inputShape = new[] { 1, 3, ModelInputSize, ModelInputSize };
        var outputName = "output0";
        var outputShape = new[] { 1, 6, 8400 };

        if (session != null)
        {
            var firstInput = session.InputMetadata.FirstOrDefault();
            if (!string.IsNullOrEmpty(firstInput.Key))
            {
                inputName = firstInput.Key;
                inputShape = firstInput.Value.Dimensions;
            }

            var firstOutput = session.OutputMetadata.FirstOrDefault();
            if (!string.IsNullOrEmpty(firstOutput.Key))
            {
                outputName = firstOutput.Key;
                outputShape = firstOutput.Value.Dimensions;
            }
        }

        return new ContainerModelStatusDto
        {
            IsModelLoaded = session != null,
            ModelPath = _resolvedModelPath,
            ModelFileSizeBytes = sizeBytes,
            InputName = inputName,
            InputShape = inputShape,
            OutputName = outputName,
            OutputShape = outputShape,
            Labels = new Dictionary<int, string>(ClassNames),
            AzureOcrConfigured = !string.IsNullOrWhiteSpace(_azureEndpoint) && !string.IsNullOrWhiteSpace(_azureKey),
            Message = session != null
                ? "Model container.onnx đã nạp sẵn sàng cho kiểm duyệt container tại cổng."
                : _loadError ?? "Không thể nạp model container.onnx."
        };
    }

    public async Task<ContainerOnnxInferenceResult> DetectAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (imageBytes == null || imageBytes.Length == 0)
        {
            return new ContainerOnnxInferenceResult
            {
                ModelLoaded = EnsureSessionLoaded() != null,
                ErrorMessage = "Dữ liệu ảnh trống."
            };
        }

        var session = EnsureSessionLoaded();
        if (session == null)
        {
            return new ContainerOnnxInferenceResult
            {
                ModelLoaded = false,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = _loadError ?? "Không tìm thấy hoặc không nạp được model container.onnx."
            };
        }

        using var srcBgr = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (srcBgr.Empty())
        {
            return new ContainerOnnxInferenceResult
            {
                ModelLoaded = true,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = "Không thể giải mã tệp ảnh hợp lệ (JPEG/PNG/WEBP)."
            };
        }

        var origW = srcBgr.Width;
        var origH = srcBgr.Height;

        // 1. Tiền xử lý Letterbox 640x640 + chuẩn hóa NCHW RGB [0..1]
        var (tensor, scale, padX, padY) = PreprocessLetterbox(srcBgr);

        var inputName = session.InputMetadata.Keys.FirstOrDefault() ?? "images";
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        // 2. Chạy suy luận ONNX Runtime (InferenceSession.Run là thread-safe)
        List<RawYoloDetection> rawDetections;
        using (var results = session.Run(inputs))
        {
            var outputTensor = results.First().AsTensor<float>();
            rawDetections = ParseOutputAndNms(outputTensor, origW, origH, scale, padX, padY);
        }

        // 3. Cắt ROI & chạy OCR cho từng vùng ID và Type
        var detectionDtos = new List<ContainerAiDetectionDto>();
        string? bestIdRaw = null;
        string? bestContainerNo = null;
        double bestIdConf = 0;
        var bestIdIsoValid = false;
        string? bestIdIsoMsg = null;

        string? bestTypeRaw = null;
        string? bestContainerType = null;
        double bestTypeConf = 0;
        var usedOcrProvider = "None";

        // Giới hạn tối đa 4 bounding box tốt nhất để tránh gọi OCR thừa
        foreach (var det in rawDetections.Take(4))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var label = ClassNames.GetValueOrDefault(det.ClassId, $"Class_{det.ClassId}");
            var croppedBytes = CropAndEnhanceRegion(srcBgr, det.X, det.Y, det.Width, det.Height);
            string? cropDataUrl = croppedBytes != null ? $"data:image/jpeg;base64,{Convert.ToBase64String(croppedBytes)}" : null;

            string? extractedText = null;
            if (croppedBytes != null)
            {
                var ocrRes = await RunOcrWithFallbackAsync(croppedBytes, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ocrRes.Provider) && ocrRes.Provider != "None")
                {
                    usedOcrProvider = ocrRes.Provider;
                }

                if (!string.IsNullOrWhiteSpace(ocrRes.Text))
                {
                    if (det.ClassId == 0) // ID
                    {
                        var parsed = Iso6346Validator.ExtractContainerNumber(ocrRes.Text);
                        extractedText = parsed.ContainerNumber ?? ocrRes.Text;

                        // Ưu tiên kết quả đạt ISO 6346, sau đó mới xét độ tin cậy cao nhất
                        var shouldSelect = bestContainerNo == null
                            || (!bestIdIsoValid && parsed.IsIso6346Valid)
                            || (bestIdIsoValid == parsed.IsIso6346Valid && det.Confidence > bestIdConf);

                        if (shouldSelect)
                        {
                            bestContainerNo = parsed.ContainerNumber;
                            bestIdRaw = ocrRes.Text;
                            bestIdConf = det.Confidence;
                            bestIdIsoValid = parsed.IsIso6346Valid;
                            bestIdIsoMsg = parsed.ValidationMessage;
                        }
                    }
                    else if (det.ClassId == 1) // Type
                    {
                        var parsedType = Iso6346Validator.ExtractIsoType(ocrRes.Text);
                        extractedText = parsedType ?? ocrRes.Text;

                        if (bestContainerType == null || det.Confidence > bestTypeConf)
                        {
                            bestContainerType = parsedType ?? ocrRes.Text.Trim();
                            bestTypeRaw = ocrRes.Text;
                            bestTypeConf = det.Confidence;
                        }
                    }
                }
            }

            detectionDtos.Add(new ContainerAiDetectionDto
            {
                ClassId = det.ClassId,
                Label = label,
                Confidence = Math.Round(det.Confidence, 4),
                Box = new ContainerBoundingBoxDto
                {
                    X = det.X,
                    Y = det.Y,
                    Width = det.Width,
                    Height = det.Height
                },
                ExtractedText = extractedText,
                CroppedImageBase64 = cropDataUrl
            });
        }

        // Nếu chưa phát hiện text từ ID nhưng vẫn bắt được vùng ID -> ghi nhận độ tin cậy bbox tốt nhất
        if (bestIdConf == 0)
        {
            var topId = detectionDtos.FirstOrDefault(d => d.ClassId == 0);
            if (topId != null)
            {
                bestIdConf = topId.Confidence;
            }
        }
        if (bestTypeConf == 0)
        {
            var topType = detectionDtos.FirstOrDefault(d => d.ClassId == 1);
            if (topType != null)
            {
                bestTypeConf = topType.Confidence;
            }
        }

        var annotatedBase64 = RenderAnnotatedImage(srcBgr, detectionDtos);
        sw.Stop();

        return new ContainerOnnxInferenceResult
        {
            ModelLoaded = true,
            ProcessingTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            OcrProvider = usedOcrProvider,
            DetectedContainerNumber = bestContainerNo,
            RawIdText = bestIdRaw,
            IdConfidence = Math.Round(bestIdConf, 4),
            Iso6346Valid = bestIdIsoValid,
            Iso6346Error = bestIdIsoMsg,
            DetectedContainerType = bestContainerType,
            RawTypeText = bestTypeRaw,
            TypeConfidence = Math.Round(bestTypeConf, 4),
            AnnotatedImageBase64 = annotatedBase64,
            Detections = detectionDtos
        };
    }

    private InferenceSession? EnsureSessionLoaded()
    {
        if (_session != null) return _session;

        lock (_sessionLock)
        {
            if (_session != null) return _session;
            if (_disposed) return null;

            var modelPath = ResolveModelPath();
            _resolvedModelPath = modelPath;

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                _loadError = "Không tìm thấy tệp model container.onnx tại Models/container.onnx.";
                _logger.LogWarning("{Error} BaseDir={BaseDir}, Cwd={Cwd}", _loadError, AppContext.BaseDirectory, Directory.GetCurrentDirectory());
                return null;
            }

            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                _session = new InferenceSession(modelPath, options);
                _loadError = null;
                _logger.LogInformation("Loaded container.onnx ONNX Runtime session from {ModelPath}", modelPath);
                return _session;
            }
            catch (Exception ex)
            {
                _loadError = $"Lỗi khởi tạo ONNX Runtime cho container.onnx: {ex.Message}";
                _logger.LogError(ex, "Failed to load container.onnx from {ModelPath}", modelPath);
                return null;
            }
        }
    }

    private string? ResolveModelPath()
    {
        var configured = _configuration["ContainerDetection:ModelPath"];
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), configured));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Models", "container.onnx"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "container.onnx"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Models", "container.onnx"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "src", "NexusPort.Api", "Models", "container.onnx"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "csharp-core", "src", "NexusPort.Api", "Models", "container.onnx"));

        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault();
    }

    private static (DenseTensor<float> Tensor, float Scale, int PadX, int PadY) PreprocessLetterbox(Mat srcBgr)
    {
        var scale = Math.Min((float)ModelInputSize / srcBgr.Width, (float)ModelInputSize / srcBgr.Height);
        var newW = Math.Max(1, (int)Math.Round(srcBgr.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(srcBgr.Height * scale));
        var padX = (ModelInputSize - newW) / 2;
        var padY = (ModelInputSize - newH) / 2;

        using var resized = new Mat();
        Cv2.Resize(srcBgr, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);

        using var letterbox = new Mat();
        Cv2.CopyMakeBorder(
            resized,
            letterbox,
            padY,
            ModelInputSize - newH - padY,
            padX,
            ModelInputSize - newW - padX,
            BorderTypes.Constant,
            new Scalar(114, 114, 114));

        var tensor = new DenseTensor<float>([1, 3, ModelInputSize, ModelInputSize]);
        var indexer = letterbox.GetGenericIndexer<Vec3b>();

        for (var y = 0; y < ModelInputSize; y++)
        {
            for (var x = 0; x < ModelInputSize; x++)
            {
                var bgr = indexer[y, x];
                tensor[0, 0, y, x] = bgr.Item2 / 255.0f; // R
                tensor[0, 1, y, x] = bgr.Item1 / 255.0f; // G
                tensor[0, 2, y, x] = bgr.Item0 / 255.0f; // B
            }
        }

        return (tensor, scale, padX, padY);
    }

    private List<RawYoloDetection> ParseOutputAndNms(
        Tensor<float> output,
        int origW,
        int origH,
        float scale,
        int padX,
        int padY)
    {
        var dims = output.Dimensions;
        if (dims.Length < 3 || dims[1] < 5)
        {
            return new List<RawYoloDetection>();
        }

        var channels = dims[1];
        var numClasses = channels - 4;
        var anchors = dims[2];
        var candidates = new List<RawYoloDetection>();

        for (var i = 0; i < anchors; i++)
        {
            var bestClass = -1;
            var bestScore = 0f;

            for (var c = 0; c < numClasses; c++)
            {
                var score = output[0, 4 + c, i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || bestScore < _confidenceThreshold) continue;

            var cx = output[0, 0, i];
            var cy = output[0, 1, i];
            var w = output[0, 2, i];
            var h = output[0, 3, i];

            var x1 = (cx - w / 2f - padX) / scale;
            var y1 = (cy - h / 2f - padY) / scale;
            var x2 = (cx + w / 2f - padX) / scale;
            var y2 = (cy + h / 2f - padY) / scale;

            var ix1 = Math.Clamp((int)Math.Round(x1), 0, origW - 1);
            var iy1 = Math.Clamp((int)Math.Round(y1), 0, origH - 1);
            var ix2 = Math.Clamp((int)Math.Round(x2), ix1 + 1, origW);
            var iy2 = Math.Clamp((int)Math.Round(y2), iy1 + 1, origH);

            candidates.Add(new RawYoloDetection(bestClass, bestScore, ix1, iy1, ix2 - ix1, iy2 - iy1));
        }

        return ApplyPerClassNms(candidates, _nmsIouThreshold);
    }

    private static List<RawYoloDetection> ApplyPerClassNms(List<RawYoloDetection> candidates, float iouThreshold)
    {
        var finalBoxes = new List<RawYoloDetection>();

        foreach (var group in candidates.GroupBy(c => c.ClassId))
        {
            var sorted = group.OrderByDescending(c => c.Confidence).ToList();
            var suppressed = new bool[sorted.Count];

            for (var i = 0; i < sorted.Count; i++)
            {
                if (suppressed[i]) continue;
                var current = sorted[i];
                finalBoxes.Add(current);

                for (var j = i + 1; j < sorted.Count; j++)
                {
                    if (!suppressed[j] && ComputeIoU(current, sorted[j]) > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }
        }

        return finalBoxes.OrderByDescending(b => b.Confidence).ToList();
    }

    private static float ComputeIoU(RawYoloDetection a, RawYoloDetection b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        var interW = Math.Max(0, x2 - x1);
        var interH = Math.Max(0, y2 - y1);
        var interArea = (float)(interW * interH);
        if (interArea <= 0f) return 0f;

        var unionArea = (float)(a.Width * a.Height + b.Width * b.Height) - interArea;
        return unionArea > 0f ? interArea / unionArea : 0f;
    }

    private static byte[]? CropAndEnhanceRegion(Mat srcBgr, int x, int y, int width, int height)
    {
        var padX = Math.Max(6, (int)Math.Round(width * 0.08));
        var padY = Math.Max(6, (int)Math.Round(height * 0.08));

        var rx = Math.Max(0, x - padX);
        var ry = Math.Max(0, y - padY);
        var rx2 = Math.Min(srcBgr.Width, x + width + padX);
        var ry2 = Math.Min(srcBgr.Height, y + height + padY);
        var rw = rx2 - rx;
        var rh = ry2 - ry;

        if (rw <= 2 || rh <= 2) return null;

        using var cropped = new Mat(srcBgr, new Rect(rx, ry, rw, rh));
        using var processed = new Mat();

        // Phóng đại vùng ký tự nhỏ để Azure Vision / OCR.Space đọc nét rõ ràng
        if (rw < 180 || rh < 80)
        {
            var factor = Math.Min(3.0, Math.Max(180.0 / rw, 80.0 / rh));
            Cv2.Resize(cropped, processed, new Size(), factor, factor, InterpolationFlags.Cubic);
        }
        else
        {
            cropped.CopyTo(processed);
        }

        Cv2.ImEncode(".jpg", processed, out var jpegBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 95));
        return jpegBytes;
    }

    private static string? RenderAnnotatedImage(Mat srcBgr, IReadOnlyList<ContainerAiDetectionDto> detections)
    {
        try
        {
            using var canvas = srcBgr.Clone();
            var thickness = Math.Max(2, canvas.Width / 400);
            var fontScale = Math.Max(0.55, canvas.Width / 1400.0);

            foreach (var det in detections)
            {
                // ID = Xanh ngọc (#10B981 -> BGR 129, 185, 16), Type = Cam (#F97316 -> BGR 22, 115, 249)
                var color = det.ClassId == 0 ? new Scalar(129, 185, 16) : new Scalar(22, 115, 249);
                var rect = new Rect(det.Box.X, det.Box.Y, det.Box.Width, det.Box.Height);
                Cv2.Rectangle(canvas, rect, color, thickness);

                var caption = string.IsNullOrWhiteSpace(det.ExtractedText)
                    ? $"{det.Label} {det.Confidence * 100:F0}%"
                    : $"{det.Label}: {det.ExtractedText} ({det.Confidence * 100:F0}%)";

                var textPos = new Point(det.Box.X, Math.Max(20, det.Box.Y - 8));
                Cv2.PutText(canvas, caption, textPos, HersheyFonts.HersheySimplex, fontScale, Scalar.Black, thickness + 2);
                Cv2.PutText(canvas, caption, textPos, HersheyFonts.HersheySimplex, fontScale, color, thickness);
            }

            Cv2.ImEncode(".jpg", canvas, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 88));
            return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string Text, string Provider)> RunOcrWithFallbackAsync(byte[] croppedJpeg, CancellationToken cancellationToken)
    {
        // 1. Ưu tiên Azure AI Vision Read API (đã cấu hình sẵn trong .env)
        if (!string.IsNullOrWhiteSpace(_azureEndpoint) && !string.IsNullOrWhiteSpace(_azureKey))
        {
            try
            {
                var azureText = await ReadWithAzureVisionAsync(croppedJpeg, cancellationToken);
                if (!string.IsNullOrWhiteSpace(azureText))
                {
                    return (azureText, "AzureVision");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure Vision OCR failed on container crop, falling back to OCR.Space.");
            }
        }

        // 2. Fallback sang OCR.Space API (như Driver OcrService)
        try
        {
            var ocrSpaceText = await ReadWithOcrSpaceAsync(croppedJpeg, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ocrSpaceText))
            {
                return (ocrSpaceText, "OcrSpace");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OCR.Space fallback failed on container crop.");
        }

        return (string.Empty, "None");
    }

    private async Task<string> ReadWithAzureVisionAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ContainerOcr");
        var url = $"{_azureEndpoint}/computervision/imageanalysis:analyze?api-version=2024-02-01&features=read&language=en";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _azureKey);
        request.Content = new ByteArrayContent(imageBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Azure Vision returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, body.Length > 200 ? body[..200] : body);
            return string.Empty;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("readResult", out var readResult) ||
            !readResult.TryGetProperty("blocks", out var blocks))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("lines", out var blockLines)) continue;
            foreach (var line in blockLines.EnumerateArray())
            {
                if (line.TryGetProperty("text", out var textEl))
                {
                    var lineStr = textEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(lineStr)) lines.Add(lineStr);
                }
            }
        }

        return string.Join(" ", lines);
    }

    private async Task<string> ReadWithOcrSpaceAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_ocrSpaceApiKey)) return string.Empty;

        var client = _httpClientFactory.CreateClient("ContainerOcr");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_ocrSpaceApiKey), "apikey");
        content.Add(new StringContent("eng"), "language");
        content.Add(new StringContent("2"), "OCREngine");

        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(fileContent, "file", "container_crop.jpg");

        using var response = await client.PostAsync("https://api.ocr.space/parse/image", content, cancellationToken);
        if (!response.IsSuccessStatusCode) return string.Empty;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("ParsedResults", out var parsedResults) &&
            parsedResults.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parsedResults.EnumerateArray())
            {
                if (item.TryGetProperty("ParsedText", out var textEl))
                {
                    var text = textEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }

        return string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sessionLock)
        {
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }

    private readonly record struct RawYoloDetection(
        int ClassId,
        float Confidence,
        int X,
        int Y,
        int Width,
        int Height);
}

