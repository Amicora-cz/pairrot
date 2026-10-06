using Pairrot.Core.Banking;
using Pairrot.Core.Payments;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Tenants;
using QRCoder;

namespace Pairrot.Infrastructure.Payments;

public sealed record PaymentQrInput(long? Amount, string? Currency, string? VariableSymbol, string? Message);

public sealed record PaymentQr(BankConnection Connection, SpaydPayment Payment);

/// <summary>
/// Payment QR codes for saved bank connections. Generation only: nothing is stored, no symbol is
/// reserved, and no audit event is written, so repeating a request is always safe. Codes are
/// rendered in-process; payment details never leave Pairrot.
/// </summary>
public class PaymentQrService(BankConnectionService banks)
{
    /// <summary>Pixels per QR module. Fixed, so the image grows with the content and stays sharp.</summary>
    private const int PixelsPerModule = 8;

    public async Task<ServiceResult<PaymentQr>> GenerateAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, PaymentQrInput input, CancellationToken ct = default)
    {
        // Input is checked before the connection is read; a format error reveals nothing about it.
        // The IBAN placeholder is replaced with the saved account below.
        var check = SpaydPayment.Create("", input.Amount, input.Currency, input.VariableSymbol, input.Message);
        if (check.Error is { } error)
        {
            return ServiceError.Validation(error.Code, error.Field, error.Message);
        }

        var connection = await banks.GetPayableAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection.Error is { } failure)
        {
            return failure;
        }

        return new PaymentQr(connection.Value!, check.Payment! with { Iban = connection.Value!.Iban });
    }

    /// <summary>
    /// A black-on-white PNG with a quiet zone, error correction level M as QR Platba recommends.
    /// The payload is encoded as UTF-8 bytes, so Czech letters in the message survive.
    /// </summary>
    public static byte[] RenderPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        return new PngByteQRCode(data).GetGraphic(PixelsPerModule, [0, 0, 0], [255, 255, 255], drawQuietZones: true);
    }
}
