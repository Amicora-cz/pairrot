using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using StbImageSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class PaymentQrTests(PairrotFixture fx)
{
    private const string Own = "/v1/tenant/bank-connections";

    private static string ChildPath(Guid childId) => $"/v1/subtenants/{childId}/bank-connections";

    private static readonly object Yoga = new { amount = 45000, currency = "CZK", variable_symbol = "0000123456", message = "Yoga October" };

    [Fact] // AC 1, 4, 8, 10
    public async Task Administrator_and_tenant_key_get_the_same_payload_and_a_decodable_png()
    {
        var tenant = await fx.CreateChildAsync();
        var account = BankConnectionTests.NewAccount();
        var browser = await fx.SignInAsync(tenant.Id);
        var connection = await browser.PostAsync(Own, new { account_number = account.AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{connection.Str("id")}/payment-qr";
        Assert.Equal("not_created", connection.Str("address_status"));

        var auditBefore = await CountAuditEventsAsync(tenant.Id);
        var data = await browser.PostAsync(path, Yoga).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(connection.Str("id"), data.Str("bank_connection_id"));
        Assert.Equal(account.AccountNumber, data.Str("account_number"));
        Assert.Equal(account.Iban, data.Str("iban"));
        Assert.Equal(45000, data["amount"]!.GetValue<long>());
        Assert.Equal("CZK", data.Str("currency"));
        Assert.Equal("0000123456", data.Str("variable_symbol"));
        Assert.Equal("Yoga October", data.Str("message"));
        Assert.Equal("SPAYD", data.Str("format"));
        var payload = data.Str("payload");
        Assert.Equal($"SPD*1.0*ACC:{account.Iban}*AM:450.00*CC:CZK*X-VS:0000123456*MSG:Yoga October", payload);

        // The image decodes, with an independent decoder, to exactly the JSON payload.
        var png = await browser.PostAsync($"{path}.png", Yoga);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Equal("payment-qr-0000123456.png", png.Content.Headers.ContentDisposition?.FileNameStar ?? png.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("no-store", png.Headers.CacheControl?.ToString());
        Assert.Equal(payload, Decode(await png.Content.ReadAsByteArrayAsync()));

        // A tenant key gets the same result; repeating creates nothing.
        var key = fx.Client(tenant.Key);
        var again = await key.PostAsJsonAsync(path, Yoga).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(payload, again.Str("payload"));
        var keyPng = await key.PostAsJsonAsync($"{path}.png", Yoga);
        Assert.Equal(payload, Decode(await keyPng.Content.ReadAsByteArrayAsync()));

        Assert.Equal(auditBefore, await CountAuditEventsAsync(tenant.Id));
        await fx.WithDbAsync(async db =>
        {
            var stored = await db.BankConnections.SingleAsync(b => b.TenantId == tenant.Id);
            Assert.Null(stored.InboundAddress);
        });
    }

    [Fact] // AC 2
    public async Task Parent_generates_for_a_direct_child_but_siblings_and_other_roots_cannot()
    {
        var studio = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var connection = await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var id = connection.Str("id");

        var data = await management.PostAsJsonAsync($"{ChildPath(studio.Id)}/{id}/payment-qr", Yoga).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(studio.Id.ToString(), data.Str("tenant_id"));
        var png = await management.PostAsJsonAsync($"{ChildPath(studio.Id)}/{id}/payment-qr.png", Yoga);
        Assert.Equal(data.Str("payload"), Decode(await png.Content.ReadAsByteArrayAsync()));

        var parentBrowser = await fx.SignInAsync(fx.RootId);
        await parentBrowser.PostAsync($"{ChildPath(studio.Id)}/{id}/payment-qr", Yoga).ExpectAsync(HttpStatusCode.OK);

        await management.PostAsJsonAsync($"{ChildPath(sibling.Id)}/{id}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).PostAsJsonAsync($"{Own}/{id}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).PostAsJsonAsync($"{Own}/{id}/payment-qr.png", Yoga).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).PostAsJsonAsync($"{ChildPath(studio.Id)}/{id}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await fx.Client(fx.RootTenantKey).PostAsJsonAsync($"{ChildPath(studio.Id)}/{id}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        var otherRoot = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await fx.Client(otherRoot.ManagementKey!.Secret).PostAsJsonAsync($"{ChildPath(studio.Id)}/{id}/payment-qr", Yoga)
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact] // AC 3, 5, 6, 7
    public async Task Inputs_are_validated_and_the_saved_account_cannot_be_overridden()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connection = await client.PostAsJsonAsync(Own, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{connection.Str("id")}/payment-qr";
        var other = BankConnectionTests.NewAccount();

        // Account, owner, or a ready-made payload cannot be supplied.
        foreach (var json in new[]
        {
            $$"""{"amount":100,"currency":"CZK","iban":"{{other.Iban}}"}""",
            $$"""{"amount":100,"currency":"CZK","account_number":"{{other.AccountNumber}}"}""",
            $$"""{"amount":100,"currency":"CZK","tenant_id":"{{Guid.NewGuid()}}"}""",
            """{"amount":100,"currency":"CZK","payload":"SPD*1.0*ACC:CZ00*AM:1.00"}""",
        })
        {
            await client.PostRawAsync(path, json).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        }

        // Bounds work; CZK 450.00 is sent as 45000 minor units and encoded as 450.00.
        Assert.Contains("*AM:0.01*", await PayloadAsync(client, path, """{"amount":1,"currency":"CZK"}"""));
        Assert.Contains("*AM:9999999.99*", await PayloadAsync(client, path, """{"amount":999999999,"currency":"CZK"}"""));
        Assert.Contains("*AM:450.00*", await PayloadAsync(client, path, """{"amount":45000,"currency":"CZK"}"""));

        foreach (var amount in new[] { "0", "-1", "45000.5", "45000.0", "4.5e4", "\"45000\"", "1000000000", "null", "99999999999999999999" })
        {
            await ExpectFieldErrorAsync(client.PostRawAsync(path, $$"""{"amount":{{amount}},"currency":"CZK"}"""), "amount");
        }

        await ExpectFieldErrorAsync(client.PostRawAsync(path, """{"currency":"CZK"}"""), "amount");
        await ExpectFieldErrorAsync(client.PostRawAsync(path, """{"amount":100}"""), "currency", "unsupported_currency");
        await ExpectFieldErrorAsync(client.PostRawAsync(path, """{"amount":100,"currency":"EUR"}"""), "currency", "unsupported_currency");

        // Variable symbols keep leading zeros; omitted or null ones are absent.
        Assert.Contains("*X-VS:0000000001", await PayloadAsync(client, path, """{"amount":100,"currency":"CZK","variable_symbol":"0000000001"}"""));
        Assert.DoesNotContain("X-VS", await PayloadAsync(client, path, """{"amount":100,"currency":"CZK","variable_symbol":null}"""));
        foreach (var symbol in new[] { "\"\"", "\"12345678901\"", "\"12a\"", "123" })
        {
            await ExpectFieldErrorAsync(client.PostRawAsync(path, $$"""{"amount":100,"currency":"CZK","variable_symbol":{{symbol}}}"""),
                "variable_symbol", symbol == "123" ? "invalid_request" : "invalid_variable_symbol");
        }

        // Czech text goes in unchanged; delimiters and '%' are refused rather than escaped; long messages are refused too.
        const string message = "Příliš žluťoučký kůň: 1+1 / ok";
        var payload = await PayloadAsync(client, path, new { amount = 100, currency = "CZK", message = $"  {message} " });
        Assert.EndsWith($"*MSG:{message}", payload);
        Assert.Equal(6, payload.Split('*').Length); // SPD, 1.0, ACC, AM, CC, MSG
        Assert.DoesNotContain("MSG", await PayloadAsync(client, path, new { amount = 100, currency = "CZK", message = "   " }));
        var png = await client.PostAsJsonAsync($"{path}.png", new { amount = 100, currency = "CZK", message });
        Assert.Equal(payload, Decode(await png.Content.ReadAsByteArrayAsync()));

        foreach (var invalid in new[] { "a*X-VS:999*AM:1.00", "100% paid" })
        {
            await ExpectFieldErrorAsync(client.PostAsJsonAsync(path, new { amount = 100, currency = "CZK", message = invalid }),
                "message", "invalid_message");
        }

        await ExpectFieldErrorAsync(client.PostAsJsonAsync(path, new { amount = 100, currency = "CZK", message = new string('ž', 61) }),
            "message", "message_too_long");
        var pngError = await client.PostAsJsonAsync($"{path}.png", new { amount = 100, currency = "CZK", message = new string('x', 61) });
        Assert.Equal("application/json", pngError.Content.Headers.ContentType?.MediaType);
        await ExpectFieldErrorAsync(Task.FromResult(pngError), "message", "message_too_long");
    }

    [Fact] // AC 9
    public async Task Archived_connections_and_inactive_tenants_cannot_generate_codes()
    {
        var studio = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var first = await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var second = await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var archivedPath = $"{ChildPath(studio.Id)}/{first.Str("id")}";
        var activePath = $"{ChildPath(studio.Id)}/{second.Str("id")}";

        await management.DeleteAsync(archivedPath).ExpectAsync(HttpStatusCode.NoContent);
        await management.PostAsJsonAsync($"{archivedPath}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        await management.PostAsJsonAsync($"{archivedPath}/payment-qr.png", Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");

        await management.PatchJsonAsync($"/v1/subtenants/{studio.Id}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await management.PostAsJsonAsync($"{activePath}/payment-qr", Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        var own = await fx.Client(studio.Key).PostAsJsonAsync($"{Own}/{second.Str("id")}/payment-qr", Yoga);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);

        await management.DeleteAsync($"/v1/subtenants/{studio.Id}").ExpectAsync(HttpStatusCode.NoContent);
        await management.PostAsJsonAsync($"{activePath}/payment-qr.png", Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
    }

    [Fact] // AC 9
    public async Task Browser_requests_need_antiforgery_and_the_current_tenant_context()
    {
        var a = await fx.CreateChildAsync();
        var b = await fx.CreateChildAsync();
        var tabA = new BrowserClient(fx.Factory);
        await tabA.LoginAsync(await fx.CreateUserAsync(a.Id, b.Id));
        await tabA.SelectTenantAsync(a.Id);
        var connection = await tabA.PostAsync(Own, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{connection.Str("id")}/payment-qr";

        await tabA.SendAsync(HttpMethod.Post, path, Yoga, antiforgery: false).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        await tabA.PostAsync(path, Yoga).ExpectAsync(HttpStatusCode.OK);

        var tabB = tabA.Clone(fx.Factory);
        await tabB.SelectTenantAsync(b.Id);
        await tabA.PostAsync(path, Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        await tabA.PostAsync($"{path}.png", Yoga).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        await tabB.PostAsync(path, Yoga).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
    }

    private static async Task<string> PayloadAsync(HttpClient client, string path, object body)
    {
        var request = body is string json ? client.PostRawAsync(path, json) : client.PostAsJsonAsync(path, body);
        return (await request.ExpectAsync(HttpStatusCode.OK)).Str("payload");
    }

    private static async Task ExpectFieldErrorAsync(Task<HttpResponseMessage> request, string field, string? code = null)
    {
        var body = await request.ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Equal(field, body["error"]!.Str("field"));
        if (code is not null)
        {
            Assert.Equal(code, body["error"]!.Str("code"));
        }
    }

    private async Task<int> CountAuditEventsAsync(Guid tenantId)
    {
        var count = 0;
        await fx.WithDbAsync(async db => count = await db.AuditEvents.CountAsync(e => e.TargetTenantId == tenantId));
        return count;
    }

    /// <summary>Decodes a PNG with ZXing, which shares no code with the QRCoder renderer.</summary>
    internal static string Decode(byte[] png)
    {
        var image = ImageResult.FromMemory(png, ColorComponents.RedGreenBlue);
        var source = new RGBLuminanceSource(image.Data, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)),
            new Dictionary<DecodeHintType, object> { [DecodeHintType.PURE_BARCODE] = true });
        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.QR_CODE, result.BarcodeFormat);
        Assert.Equal("M", result.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL].ToString());
        return result.Text;
    }
}
