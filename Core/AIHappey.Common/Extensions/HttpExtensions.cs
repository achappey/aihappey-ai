using System.Security.Claims;
using System.Text.Json;
using AIHappey.Common.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;

namespace AIHappey.Common.Extensions;

public static class HttpExtensions
{
    public static string? GetUserUpn(this HttpContext context) =>
        context.User.FindFirst(ClaimTypes.Upn)?.Value;

    public static string? GetUserOid(this HttpContext context) =>
        context.User.FindFirst(ClaimConstants.ObjectId)?.Value;

   
}
