using System.Drawing;
using System.Globalization;
using QRCoder;

namespace Curto.Api.Services;

/// <summary>Renders QR codes as SVG documents.</summary>
internal static class QrCodeRenderer
{
    /// <summary>Smallest accepted side length in pixels.</summary>
    public const int MinSize = 64;

    /// <summary>Largest accepted side length in pixels.</summary>
    public const int MaxSize = 2048;

    /// <summary>Side length used when the request does not specify one.</summary>
    public const int DefaultSize = 256;

    /// <summary>Default module colour.</summary>
    public const string DefaultDark = "#000000";

    /// <summary>Default background colour.</summary>
    public const string DefaultLight = "#ffffff";

    /// <summary>Renders <paramref name="text"/> as a QR code with error correction level M and a standard quiet zone.</summary>
    /// <param name="text">Payload (the short URL).</param>
    /// <param name="size">Side length in pixels, <see cref="MinSize"/> to <see cref="MaxSize"/>.</param>
    /// <param name="darkHex">Module colour as <c>#rgb</c> or <c>#rrggbb</c>; must already be validated.</param>
    /// <param name="lightHex">Background colour as <c>#rgb</c> or <c>#rrggbb</c>; must already be validated.</param>
    public static string RenderSvg(string text, int size, string darkHex, string lightHex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(new Size(size, size), darkHex, lightHex, drawQuietZones: true);
    }

    /// <summary>Normalises a colour query value to <c>#rrggbb</c>/<c>#rgb</c> lower-case, accepting an optional leading <c>#</c>.</summary>
    /// <param name="value">Raw query-string value.</param>
    /// <param name="hex">Normalised colour.</param>
    /// <returns>False when the value is not a 3 or 6 digit hexadecimal colour.</returns>
    public static bool TryParseHexColor(string? value, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var digits = value.StartsWith('#') ? value.AsSpan(1) : value.AsSpan();
        if (digits.Length is not (3 or 6))
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        hex = string.Create(CultureInfo.InvariantCulture, $"#{digits}").ToLowerInvariant();
        return true;
    }
}
