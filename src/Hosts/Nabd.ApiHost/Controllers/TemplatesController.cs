namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;

[ApiController]
[Route("api/v1/templates")]
public sealed class TemplatesController(ITemplateCatalog templates) : ControllerBase
{
    // Global catalog — one GET, every authenticated role. No tenant segment, no
    // body, no errors but Forbidden (layer 2's assert is the only failure shape).
    [Viewer(Operations.TemplateRead)]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await templates.ListTemplatesAsync(caller, ct);

        return result.IsSuccess ? Ok(result.Value)
            : StatusCode(403, new { code = result.Error });
    }
}