namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabd.Ingestion;

// P3 spec 05 — the HTTP ingest door's transport adapter (plan/05 §4). THIN ON
// PURPOSE: the door (Nabd.Ingestion.HttpIngestDoor) owns the whole orchestration
// AND the status mapping ("dispositions live with the caller" — the door IS the
// HTTP caller); this controller only extracts the Bearer credential and copies
// the verdict out. It is NOT the JWT wall: [AllowAnonymous] + the door's own
// device-token lane (VerifyTokenAsync), per the spec's auth design — a bad
// device token IS a definitive 401 here (no anti-enum dance; that's the login
// lane's law, not this door's).
[ApiController]
[Route("ingest")]
public sealed class IngestController(HttpIngestDoor door) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("v1")]
    [RequestSizeLimit(1_000_000)]   // plan/05 §4: max body 1 MB
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        // Bearer extraction: the door answers 401 E401_AUTH for anything that
        // isn't a well-formed credential — including a missing/foreign header.
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : "";

        // Raw body → bytes: the payload is parsed by the pipeline (stage 3),
        // NOT by MVC — the wire format is payload v1, byte-exact with MQTT.
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);

        var verdict = await door.IngestAsync(token, body.ToArray(), ct);

        if (verdict.RetryAfterSeconds is { } retry)
            Response.Headers.RetryAfter = retry.ToString();

        if (verdict.Status == 200)
            return Ok(new { stored = verdict.Stored, warnings = verdict.Warnings });

        return StatusCode(verdict.Status, new { code = verdict.Code, detail = verdict.Detail });
    }
}
