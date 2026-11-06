using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Security;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Http;

public sealed class ToolApprovalValidationException() : Exception(
    new ToolApprovalSignatureException().Message);

/// <summary>
/// Gateway chat adapter. AzureAuth binds approvals to its resolved user;
/// HeaderAuth explicitly opts into issuer-only signing, independent of credentials.
/// Configuration stays in the host; provider identity hashing is unaffected.
/// </summary>
public sealed class ToolApprovalSecurity(
    IOptions<EndUserIdHashingOptions> options,
    IEndUserIdResolver identity,
    bool bindToUser = true)
{
    private ToolApprovalSigner? Signer(ChatRequest request)
    {
        var secret = options.Value.SecretSalt;
        if (string.IsNullOrWhiteSpace(secret)) return null;
        if (!bindToUser) return new ToolApprovalSigner(secret);
        var user = identity.Resolve(request);
        return string.IsNullOrWhiteSpace(user) ? null : new ToolApprovalSigner(secret, user);
    }

    public void ValidateIncoming(ChatRequest request)
    {
        try { Signer(request)?.ValidateIncoming(request.Messages); }
        catch (ToolApprovalSignatureException) { throw new ToolApprovalValidationException(); }
    }

    public string CreateSignature(string? user, string approvalId, ToolCallPart tool)
    {
        var secret = options.Value.SecretSalt;
        if (string.IsNullOrWhiteSpace(secret) || bindToUser && string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("Signed tool approvals require a configured EndUserIdHashing:SecretSalt and, for user-bound hosts, a resolved user identity.");
        try { return new ToolApprovalSigner(secret, bindToUser ? user : null).CreateSignature(approvalId, tool); }
        catch (ToolApprovalSignatureException) { throw new ToolApprovalValidationException(); }
    }

    public IAsyncEnumerable<UIMessagePart> SignOutgoing(
        ChatRequest request, IAsyncEnumerable<UIMessagePart> stream,
        CancellationToken cancellationToken = default)
        // Without the host's prerequisites, preserve the legacy stream exactly.
        => Signer(request)?.SignOutgoing(stream, cancellationToken) ?? stream;
}
