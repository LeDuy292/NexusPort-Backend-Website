using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using OpenCvSharp;

namespace NexusPort.Modules.Driver.Application.Services;

public interface IOcrService
{
    Task<OcrRecognitionResponse?> ExtractIdCardAsync(IFormFile imageFile, CancellationToken cancellationToken = default);
    Task<OcrRecognitionResponse?> ExtractDriverLicenseAsync(IFormFile imageFile, CancellationToken cancellationToken = default);
    Task<OcrVehicleRegistrationResponse?> ExtractVehicleRegistrationAsync(IFormFile imageFile, CancellationToken cancellationToken = default);
}

public class OcrService : IOcrService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey = "K86333912188957"; // OCR.Space public key

    public OcrService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<OcrRecognitionResponse?> ExtractIdCardAsync(IFormFile imageFile, CancellationToken cancellationToken = default)
    {
        var text = await ExtractTextFromOcrSpaceAsync(imageFile, cancellationToken);
        Console.WriteLine("=== RAW OCR TEXT CCCD ===");
        Console.WriteLine(text);
        Console.WriteLine("=========================");
        var faceBytes = CropFace(imageFile);

        // Simple Regex for CCCD
        // Simple Regex for CCCD
        var textNoSpaces = text.Replace(" ", "");
        var idMatch = Regex.Match(textNoSpaces, @"(?<!\d)\d{12}(?!\d)");
        var idNumber = idMatch.Success ? idMatch.Value : null;

        // Họ và tên: uppercase letters after some keywords or just the longest uppercase string
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string? name = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Họ và tên") || lines[i].Contains("Full name"))
            {
                if (i + 1 < lines.Length)
                {
                    name = lines[i + 1].Trim();
                }
            }
        }
        
        // If regex fails, fallback to simple heuristic: find the longest all-uppercase string
        if (string.IsNullOrEmpty(name))
        {
            var uppercaseLines = lines.Where(l => l.Length > 5 && l == l.ToUpper() && !l.Any(char.IsDigit)).ToList();
            if (uppercaseLines.Any())
            {
                name = uppercaseLines.OrderByDescending(l => l.Length).First().Trim();
            }
        }

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(idNumber))
        {
            return null;
        }

        return new OcrRecognitionResponse
        {
            Id = idNumber,
            Name = name,
            FaceImageBytes = faceBytes
        };
    }

    public async Task<OcrRecognitionResponse?> ExtractDriverLicenseAsync(IFormFile imageFile, CancellationToken cancellationToken = default)
    {
        var text = await ExtractTextFromOcrSpaceAsync(imageFile, cancellationToken);
        var faceBytes = CropFace(imageFile);

        // GPLX usually has a 12 digit number or similar.
        // GPLX ID: Usually 12 digits
        var textNoSpaces = text.Replace(" ", "");
        var idMatch = Regex.Match(textNoSpaces, @"(?<!\d)\d{12}(?!\d)");
        var idNumber = idMatch.Success ? idMatch.Value : null;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string? name = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Họ và tên") || lines[i].Contains("Full name"))
            {
                // GPLX has name on the same line or next line
                var split = lines[i].Split(new[] { "Full name:", "Họ và tên:" }, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length > 1 && !string.IsNullOrWhiteSpace(split.Last()))
                {
                    name = split.Last().Trim();
                }
                else if (i + 1 < lines.Length)
                {
                    name = lines[i + 1].Trim();
                }
            }
        }

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(idNumber))
        {
            return null;
        }

        return new OcrRecognitionResponse
        {
            Id = idNumber,
            Name = name,
            FaceImageBytes = faceBytes
        };
    }

    public async Task<OcrVehicleRegistrationResponse?> ExtractVehicleRegistrationAsync(IFormFile imageFile, CancellationToken cancellationToken = default)
    {
        var text = await ExtractTextFromOcrSpaceAsync(imageFile, cancellationToken);
        
        // Strict regex: requires at least one dash or dot (e.g. 47K1-137.87 or 43C-12345)
        var textNoSpaces = text.Replace(" ", "");
        var strictMatch = Regex.Match(textNoSpaces, @"\d{2}[A-Z][0-9A-Z]?[-.]\d{3}[-.]?\d{2}|\d{2}[A-Z][0-9A-Z]?[-.]\d{4,5}");
        string? plateNumber = strictMatch.Success ? strictMatch.Value : null;

        // If strict regex fails, fallback to something broader without requiring dashes/dots
        if (string.IsNullOrEmpty(plateNumber))
        {
            var broadMatch = Regex.Match(textNoSpaces, @"\d{2}[A-Z][0-9A-Z]?[-.]?\d{4,5}");
            if (broadMatch.Success) plateNumber = broadMatch.Value;
        }

        string? brand = null;
        string? model = null;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Extract Brand
            if (string.IsNullOrEmpty(brand) && (line.Contains("Nhãn hiệu") || line.Contains("Brand") || line.Contains("Mark")))
            {
                var split = line.Split(new[] { ":", "Nhãn hiệu", "Brand", "Mark" }, StringSplitOptions.RemoveEmptyEntries);
                var lastPart = split.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(lastPart))
                {
                    brand = lastPart.Trim().Trim(':', '-', ' ', ')', '(');
                }
                else if (i + 1 < lines.Length)
                {
                    brand = lines[i + 1].Trim().Trim(':', '-', ' ', ')', '(');
                }
            }

            // Extract Model
            if (string.IsNullOrEmpty(model) && (line.Contains("Số loại") || line.Contains("Loại xe") || line.Contains("Model") || line.Contains("Type")))
            {
                var split = line.Split(new[] { ":", "Số loại", "Loại xe", "Model", "Type" }, StringSplitOptions.RemoveEmptyEntries);
                var lastPart = split.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(lastPart))
                {
                    model = lastPart.Trim().Trim(':', '-', ' ', ')', '(');
                }
                else if (i + 1 < lines.Length)
                {
                    model = lines[i + 1].Trim().Trim(':', '-', ' ', ')', '(');
                }
            }
        }

        var fullBrand = string.Join(" - ", new[] { brand, model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(fullBrand)) fullBrand = null;

        return new OcrVehicleRegistrationResponse
        {
            PlateNumber = plateNumber,
            Brand = fullBrand
        };
    }

    private async Task<string> ExtractTextFromOcrSpaceAsync(IFormFile imageFile, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_apiKey), "apikey");
        content.Add(new StringContent("eng"), "language"); // Fallback to English for better stability with this key
        
        using var stream = imageFile.OpenReadStream();
        using var fileContent = new StreamContent(stream);
        
        if (MediaTypeHeaderValue.TryParse(imageFile.ContentType, out var parsedContentType))
        {
            fileContent.Headers.ContentType = parsedContentType;
        }
        else
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        content.Add(fileContent, "file", imageFile.FileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.ocr.space/parse/image");
        request.Content = content;

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode) return string.Empty;

        var result = JsonSerializer.Deserialize<OcrSpaceResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (result?.ParsedResults != null && result.ParsedResults.Count > 0)
        {
            return result.ParsedResults[0].ParsedText ?? string.Empty;
        }

        return string.Empty;
    }

    private byte[]? CropFace(IFormFile imageFile)
    {
        try
        {
            using var stream = imageFile.OpenReadStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var imageBytes = ms.ToArray();

            // Load into OpenCV Mat
            using var src = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (src.Empty()) return null;

            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);

            // Load Haar Cascade
            var cascadePath = Path.Combine(Directory.GetCurrentDirectory(), "haarcascade_frontalface_default.xml");
            if (!File.Exists(cascadePath)) return null;

            using var cascade = new CascadeClassifier(cascadePath);
            var faces = cascade.DetectMultiScale(
                gray, 
                scaleFactor: 1.1, 
                minNeighbors: 4, 
                flags: HaarDetectionTypes.ScaleImage, 
                minSize: new Size(30, 30)
            );

            if (faces.Length == 0) return null;

            // Get largest face
            var largestFace = faces.OrderByDescending(f => f.Width * f.Height).First();

            // Add some padding
            int paddingX = (int)(largestFace.Width * 0.2);
            int paddingY = (int)(largestFace.Height * 0.3);

            int x = Math.Max(0, largestFace.X - paddingX);
            int y = Math.Max(0, largestFace.Y - paddingY);
            int w = Math.Min(src.Width - x, largestFace.Width + paddingX * 2);
            int h = Math.Min(src.Height - y, largestFace.Height + paddingY * 2);

            var faceRect = new Rect(x, y, w, h);
            using var faceMat = new Mat(src, faceRect);

            // Encode to jpeg bytes
            Cv2.ImEncode(".jpg", faceMat, out byte[] faceJpg);
            return faceJpg;
        }
        catch
        {
            return null; // Ignore OpenCV errors, just return null face
        }
    }
}

public class OcrRecognitionResponse
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Dob { get; set; }
    public string? Sex { get; set; }
    public string? Address { get; set; }
    public byte[]? FaceImageBytes { get; set; }
}

public class OcrVehicleRegistrationResponse
{
    public string? PlateNumber { get; set; }
    public string? Brand { get; set; }
}

public class OcrSpaceResponse
{
    public List<OcrSpaceParsedResult>? ParsedResults { get; set; }
    public int OcrExitCode { get; set; }
    public bool IsErroredOnProcessing { get; set; }
    public object? ErrorMessage { get; set; }
}

public class OcrSpaceParsedResult
{
    public string? ParsedText { get; set; }
}
