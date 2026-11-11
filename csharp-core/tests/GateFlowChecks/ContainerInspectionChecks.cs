using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexusPort.Modules.Gate.Domain.Rules;
using NexusPort.Modules.Gate.Domain.Rules.Concrete;
using NexusPort.Modules.Gate.Domain.Validation;
using NexusPort.Modules.Gate.Infrastructure.Services;
using OpenCvSharp;

internal static class ContainerInspectionChecks
{
    public static async Task<int> RunAllAsync()
    {
        var passed = 0;
        void Verify(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++;
        }

        // 1. ISO 6346 validation & OCR normalization
        var msku = Iso6346Validator.Validate("MSKU3829109");
        Verify(msku.IsValid && msku.ExpectedCheckDigit == 9, "ISO 6346 valid MSKU3829109");

        var emcuValid = Iso6346Validator.Validate("EMCU8812902");
        Verify(emcuValid.IsValid && emcuValid.ExpectedCheckDigit == 2, "ISO 6346 valid EMCU8812902");

        var emcuBad = Iso6346Validator.Validate("EMCU8812904");
        Verify(!emcuBad.IsValid && emcuBad.ExpectedCheckDigit == 2 && emcuBad.ActualCheckDigit == 4,
            "ISO 6346 rejects invalid check digit EMCU8812904");

        var ocrExtracted = Iso6346Validator.ExtractContainerNumber("E M C U 881290 2");
        Verify(ocrExtracted.ContainerNumber == "EMCU8812902" && ocrExtracted.IsIso6346Valid,
            "ISO 6346 OCR extraction normalizes EMCU8812902");

        Verify(Iso6346Validator.AreIsoTypesCompatible("45G1", "40HC"), "ISO type 45G1 compatible with 40HC");
        Verify(!Iso6346Validator.AreIsoTypesCompatible("22G1", "40HC"), "ISO type 22G1 incompatible with 40HC");

        // 2. ContainerMatchAndStatusRule strict ISO + booking match + type compatibility
        var rule = new ContainerMatchAndStatusRule();

        var rulePass = await rule.EvaluateAsync(new GateValidationContext
        {
            GateType = "GateIn",
            ContainerNumber = "EMCU8812902",
            DetectedContainerType = "45G1",
            StrictContainerIsoCheck = true,
            ExpectedBookingContainerNumbers = ["EMCU8812902"],
            ExpectedBookingIsoSizes = ["40HC"]
        });
        Verify(rulePass.IsSuccess, "ContainerMatchAndStatusRule passes valid matching container & type");

        var ruleBadIso = await rule.EvaluateAsync(new GateValidationContext
        {
            GateType = "GateIn",
            ContainerNumber = "EMCU8812904",
            StrictContainerIsoCheck = true,
            ExpectedBookingContainerNumbers = ["EMCU8812902"]
        });
        Verify(!ruleBadIso.IsSuccess && ruleBadIso.FailureReason == "CONTAINER_INVALID_ISO6346",
            "ContainerMatchAndStatusRule fails CONTAINER_INVALID_ISO6346 on invalid check digit");

        var ruleMismatch = await rule.EvaluateAsync(new GateValidationContext
        {
            GateType = "GateIn",
            ContainerNumber = "EMCU8812902",
            StrictContainerIsoCheck = true,
            ExpectedBookingContainerNumbers = ["MSKU3829109"]
        });
        Verify(!ruleMismatch.IsSuccess && ruleMismatch.FailureReason == "CONTAINER_BOOKING_MISMATCH",
            "ContainerMatchAndStatusRule fails CONTAINER_BOOKING_MISMATCH when not in booking");

        var ruleTypeMismatch = await rule.EvaluateAsync(new GateValidationContext
        {
            GateType = "GateIn",
            ContainerNumber = "EMCU8812902",
            DetectedContainerType = "22G1",
            StrictContainerIsoCheck = true,
            ExpectedBookingContainerNumbers = ["EMCU8812902"],
            ExpectedBookingIsoSizes = ["40HC"]
        });
        Verify(!ruleTypeMismatch.IsSuccess && ruleTypeMismatch.FailureReason == "CONTAINER_TYPE_MISMATCH",
            "ContainerMatchAndStatusRule fails CONTAINER_TYPE_MISMATCH on incompatible ISO size/type");

        // 3. ContainerOnnxDetectionService end-to-end model load + inference on blank JPEG
        var modelPath = FindContainerOnnxModelPath();
        Verify(File.Exists(modelPath), $"container.onnx model file exists at {modelPath}");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContainerDetection:ModelPath"] = modelPath
            })
            .Build();

        using var detectionService = new ContainerOnnxDetectionService(
            config,
            new StubHttpClientFactory(),
            NullLogger<ContainerOnnxDetectionService>.Instance);

        var status = detectionService.GetModelStatus();
        Verify(status.IsModelLoaded &&
               status.InputShape.SequenceEqual([1, 3, 640, 640]) &&
               status.OutputShape.SequenceEqual([1, 6, 8400]) &&
               status.Labels.GetValueOrDefault(0) == "ID" &&
               status.Labels.GetValueOrDefault(1) == "Type",
            "ContainerOnnxDetectionService loads container.onnx metadata (1x3x640x640 -> 1x6x8400, ID/Type)");

        using var blankMat = new Mat(480, 640, MatType.CV_8UC3, new Scalar(25, 45, 65));
        Cv2.ImEncode(".jpg", blankMat, out var jpegBytes);

        var inference = await detectionService.DetectAsync(jpegBytes);
        Verify(inference.ModelLoaded &&
               inference.Detections != null &&
               !string.IsNullOrWhiteSpace(inference.AnnotatedImageBase64),
            "ContainerOnnxDetectionService executes inference on JPEG image without error");

        return passed;
    }

    private static string FindContainerOnnxModelPath()
    {
        var probeRoots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var root in probeRoots)
        {
            var dir = new DirectoryInfo(root);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "NexusPort.Api", "Models", "container.onnx");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Models", "container.onnx");
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

