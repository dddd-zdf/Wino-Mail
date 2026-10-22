using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>
/// Finds a one-time verification code in a new message so its notification can offer to copy it.
/// A number only counts when it sits close to a word such as "code" or "verification", which keeps
/// order numbers, prices and dates in ordinary mail from being offered.
/// </summary>
public static partial class VerificationCodeDetector
{
    private const int MaximumKeywordDistance = 80;

    public static bool TryDetect(string? subject, string? previewText, [NotNullWhen(true)] out string? code)
    {
        code = FindCode(subject) ?? FindCode(previewText);
        return code != null;
    }

    private static string? FindCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var keywords = KeywordRegex().Matches(text);
        if (keywords.Count == 0)
            return null;

        string? bestCode = null;
        var bestDistance = int.MaxValue;

        foreach (Match candidate in CandidateRegex().Matches(text))
        {
            var value = candidate.Value.Replace(" ", string.Empty).Replace("-", string.Empty);

            if (IsLikelyYear(value))
                continue;

            foreach (Match keyword in keywords)
            {
                var distance = GetDistance(keyword, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCode = value;
                }
            }
        }

        return bestDistance <= MaximumKeywordDistance ? bestCode : null;
    }

    private static int GetDistance(Match keyword, Match candidate)
    {
        if (candidate.Index >= keyword.Index + keyword.Length)
            return candidate.Index - (keyword.Index + keyword.Length);

        if (keyword.Index >= candidate.Index + candidate.Length)
            return keyword.Index - (candidate.Index + candidate.Length);

        return 0;
    }

    private static bool IsLikelyYear(string value)
        => value.Length == 4 &&
           int.TryParse(value, out var number) &&
           number is >= 1900 and <= 2099;

    // "code" has no leading word boundary so compounds such as "Bestätigungscode" match.
    [GeneratedRegex(
        @"code|codice|código|\bkod\b|\bкод|\bpasscode\b|\botp\b|\bone[- ]time\b|\bverif\w*|\b2fa\b|\btwo[- ]factor\b|\bpin\b|\blog[- ]?in\b|\bsign[- ]?in\b|\bconfirm\w*|\bauthenticat\w*|验证码|驗證碼|校验码|認証|確認コード|인증",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordRegex();

    // Digit codes of 4 to 8 digits, also written as "123 456" or "123-456", and upper-case codes of
    // 6 to 8 letters and digits such as "A1B2C3". The guards reject prices, order numbers, times,
    // decimals, dates and parts of longer words or numbers.
    [GeneratedRegex(
        @"(?<![\w$€£¥#+.,/:])(?:\d{3}[ -]\d{3}|\d{4,8})(?![\w%/:]|[.,-]\d)|(?<![\w-])(?=[A-Z]*\d)(?=\d*[A-Z])[A-Z\d]{6,8}(?![\w-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidateRegex();
}
