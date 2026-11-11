using System.Text.RegularExpressions;

namespace NexusPort.Modules.Gate.Domain.Validation;

public sealed record Iso6346ValidationResult(
    bool IsValid,
    string NormalizedNumber,
    int? ExpectedCheckDigit,
    int? ActualCheckDigit,
    string? ErrorMessage);

public sealed record ExtractedContainerNumberResult(
    string? ContainerNumber,
    bool IsIso6346Valid,
    bool CorrectedFromOcrAmbiguity,
    string? ValidationMessage);

/// <summary>
/// Chuẩn hóa và kiểm tra mã Container + mã ISO Size/Type theo tiêu chuẩn quốc tế ISO 6346.
/// </summary>
public static class Iso6346Validator
{
    private static readonly Dictionary<char, int> CharWeights = new()
    {
        ['A'] = 10, ['B'] = 12, ['C'] = 13, ['D'] = 14, ['E'] = 15,
        ['F'] = 16, ['G'] = 17, ['H'] = 18, ['I'] = 19, ['J'] = 20,
        ['K'] = 21, ['L'] = 23, ['M'] = 24, ['N'] = 25, ['O'] = 26,
        ['P'] = 27, ['Q'] = 28, ['R'] = 29, ['S'] = 30, ['T'] = 31,
        ['U'] = 32, ['V'] = 34, ['W'] = 35, ['X'] = 36, ['Y'] = 37,
        ['Z'] = 38
    };

    private static readonly Dictionary<char, char> DigitToLetterConfusions = new()
    {
        ['0'] = 'O',
        ['1'] = 'I',
        ['2'] = 'Z',
        ['5'] = 'S',
        ['6'] = 'G',
        ['8'] = 'B'
    };

    private static readonly Dictionary<char, char> LetterToDigitConfusions = new()
    {
        ['O'] = '0',
        ['D'] = '0',
        ['Q'] = '0',
        ['I'] = '1',
        ['L'] = '1',
        ['Z'] = '2',
        ['A'] = '4',
        ['S'] = '5',
        ['G'] = '6',
        ['T'] = '7',
        ['B'] = '8'
    };

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return Regex.Replace(raw, @"[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
    }

    public static Iso6346ValidationResult Validate(string? containerNumber)
    {
        var clean = Normalize(containerNumber);
        if (string.IsNullOrEmpty(clean))
        {
            return new Iso6346ValidationResult(false, string.Empty, null, null, "Số container không được để trống.");
        }

        if (clean.Length != 11)
        {
            return new Iso6346ValidationResult(false, clean, null, null, $"Số container '{clean}' phải gồm đúng 11 ký tự theo chuẩn ISO 6346 (hiện có {clean.Length} ký tự).");
        }

        if (!Regex.IsMatch(clean, @"^[A-Z]{4}\d{7}$"))
        {
            return new Iso6346ValidationResult(false, clean, null, null, $"Số container '{clean}' sai cấu trúc ISO 6346 (cần 4 chữ cái đầu + 7 chữ số).");
        }

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var ch = clean[i];
            int val;
            if (ch is >= '0' and <= '9')
            {
                val = ch - '0';
            }
            else if (CharWeights.TryGetValue(ch, out var mapped))
            {
                val = mapped;
            }
            else
            {
                return new Iso6346ValidationResult(false, clean, null, null, $"Ký tự không hợp lệ '{ch}' trong mã container '{clean}'.");
            }

            sum += val * (1 << i);
        }

        var expected = (sum % 11) % 10;
        var actual = clean[10] - '0';

        if (expected != actual)
        {
            return new Iso6346ValidationResult(
                false,
                clean,
                expected,
                actual,
                $"Sai chữ số kiểm tra ISO 6346: đang ghi {actual}, chuẩn ISO cần {expected}.");
        }

        return new Iso6346ValidationResult(true, clean, expected, actual, null);
    }

    public static ExtractedContainerNumberResult ExtractContainerNumber(string? rawOcrText)
    {
        if (string.IsNullOrWhiteSpace(rawOcrText))
        {
            return new ExtractedContainerNumberResult(null, false, false, "Không đọc được ký tự nào từ vùng ID.");
        }

        var compact = Normalize(rawOcrText);
        if (compact.Length < 11)
        {
            return new ExtractedContainerNumberResult(
                string.IsNullOrEmpty(compact) ? null : compact,
                false,
                false,
                $"Chuỗi nhận diện '{compact}' chưa đủ 11 ký tự ISO 6346.");
        }

        // 1. Ưu tiên khớp chính xác 4 chữ + 7 số và đạt ISO 6346
        var strictMatches = Regex.Matches(compact, @"(?=([A-Z]{4}\d{7}))");
        foreach (Match match in strictMatches)
        {
            var candidate = match.Groups[1].Value;
            var check = Validate(candidate);
            if (check.IsValid)
            {
                return new ExtractedContainerNumberResult(candidate, true, false, null);
            }
        }

        // 2. Thử trượt cửa sổ 11 ký tự và hiệu chỉnh các lỗi OCR hay gặp (O/0, I/1, S/5, B/8...)
        for (var i = 0; i <= compact.Length - 11; i++)
        {
            var window = compact.Substring(i, 11).ToCharArray();
            var modified = false;

            for (var pos = 0; pos < 4; pos++)
            {
                if (!char.IsLetter(window[pos]) && DigitToLetterConfusions.TryGetValue(window[pos], out var letter))
                {
                    window[pos] = letter;
                    modified = true;
                }
            }

            for (var pos = 4; pos < 11; pos++)
            {
                if (!char.IsDigit(window[pos]) && LetterToDigitConfusions.TryGetValue(window[pos], out var digit))
                {
                    window[pos] = digit;
                    modified = true;
                }
            }

            var candidate = new string(window);
            var check = Validate(candidate);
            if (check.IsValid)
            {
                return new ExtractedContainerNumberResult(candidate, true, modified, null);
            }
        }

        // 3. Fallback: trả về chuỗi gần đúng nhất nếu có 4 chữ + 7 số (dù sai check digit để Gate Officer kiểm tra)
        if (strictMatches.Count > 0)
        {
            var fallback = strictMatches[0].Groups[1].Value;
            var check = Validate(fallback);
            return new ExtractedContainerNumberResult(fallback, false, false, check.ErrorMessage);
        }

        return new ExtractedContainerNumberResult(compact.Length > 20 ? compact[..20] : compact, false, false,
            $"Chuỗi OCR '{compact}' không khớp định dạng mã container ISO 6346.");
    }

    public static string? ExtractIsoType(string? rawOcrText)
    {
        if (string.IsNullOrWhiteSpace(rawOcrText)) return null;
        var upper = rawOcrText.ToUpperInvariant();

        // Chuẩn ISO 6346 Size/Type 4 ký tự: 22G1, 45G1, 42G1, 45R1, 22R1, L5G1...
        var isoMatch = Regex.Match(upper, @"\b([0-9A-Z]{2}[GRUPTKBSA][0-9A-Z])\b");
        if (isoMatch.Success)
        {
            return isoMatch.Groups[1].Value;
        }

        var compact = Normalize(upper);
        var compactIso = Regex.Match(compact, @"([24L][025][GRUPTKBSA][0-9])");
        if (compactIso.Success)
        {
            return compactIso.Groups[1].Value;
        }

        var commercial = Regex.Match(compact, @"(20GP|40GP|40HC|45HC|20RF|40RF|20OT|40OT|20FR|40FR|20TK|40TK)");
        return commercial.Success ? commercial.Groups[1].Value : (compact.Length is >= 2 and <= 6 ? compact : null);
    }

    public static bool AreIsoTypesCompatible(string? detectedType, string? expectedType)
    {
        var d = Normalize(detectedType);
        var e = Normalize(expectedType);
        if (string.IsNullOrEmpty(d) || string.IsNullOrEmpty(e)) return true;
        if (d == e) return true;

        var dFamily = ToCanonicalFamily(d);
        var eFamily = ToCanonicalFamily(e);
        return dFamily == eFamily;
    }

    private static string ToCanonicalFamily(string code) => code switch
    {
        "22G0" or "22G1" or "20G1" or "20GP" => "20GP",
        "25G0" or "25G1" or "20HC" => "20HC",
        "42G0" or "42G1" or "40G1" or "40GP" => "40GP",
        "45G0" or "45G1" or "45HC" or "40HC" => "40HC",
        "L5G1" or "L2G1" or "45GP" => "45HC",
        "22R1" or "22R0" or "20RF" => "20RF",
        "42R1" or "45R1" or "45R0" or "40RF" => "40RF",
        "22U1" or "20OT" => "20OT",
        "42U1" or "45U1" or "40OT" => "40OT",
        "22P1" or "22P3" or "20FR" => "20FR",
        "42P1" or "42P3" or "45P1" or "40FR" => "40FR",
        "22T1" or "22T6" or "20TK" => "20TK",
        _ => code
    };
}

