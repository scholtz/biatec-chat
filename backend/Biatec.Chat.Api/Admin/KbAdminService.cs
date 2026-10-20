using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Onboarding;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Biatec.Chat.Tools.Kb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Biatec.Chat.Api.Admin;

public enum KbChange { Created, Replaced, Unchanged, Deleted, NotFound, Invalid, UnsupportedType, TooLarge }

/// <summary><see cref="ReloadError"/> is set when the change is stored but this replica could not reload; it picks the change up on its next refresh.</summary>
public sealed record KbWriteResult(KbChange Change, string? Error = null, KbStoredPageView? Page = null, int Served = 0, string? ReloadError = null);

/// <summary>An uploaded page without its body.</summary>
public sealed record KbStoredPageView(string Id, string ContentType, int SizeBytes, string Sha256, DateTimeOffset UpdatedAt)
{
    public static KbStoredPageView Of(KbStoredPage p) => new(p.Id, p.ContentType, p.SizeBytes, p.Sha256, p.UpdatedAt);
}

/// <summary>A page this replica serves right now.</summary>
public sealed record KbServedPageView(string Id, string Title, string Source);

/// <summary>
/// Operator knowledge-base management (T-096): uploads go to the shared <c>KbPages</c> table, then this replica
/// reloads at once and the others within <c>Kb:RefreshSeconds</c>. Only UTF-8 markdown or plain text within the size
/// limit is accepted; the content stays untrusted data that reaches the model only as a <c>kb.search</c> result.
/// </summary>
public sealed partial class KbAdminService(ChatDbContext db, KbLibrary library, KbSearchService kb, IOptions<KbOptions> options, IEvidenceLog evidence)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public const int MaxIdLength = 64;

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z")]
    private static partial Regex IdRegex();

    public static bool ValidId(string id) => id.Length is > 0 and <= MaxIdLength && IdRegex().IsMatch(id);

    private const string LockSql = "SELECT pg_advisory_xact_lock(hashtextextended('biatec-chat-admin:kb', 0))";

    /// <summary>"text/markdown" or "text/plain" for an accepted Content-Type (charset, if any, must be UTF-8), else null.</summary>
    public static string? AcceptedType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mt)) return null;
        if (mt.Charset.HasValue && !mt.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return null;
        return mt.MediaType.Value?.ToLowerInvariant() switch
        {
            "text/markdown" or "text/x-markdown" => "text/markdown",
            "text/plain" => "text/plain",
            _ => null,
        };
    }

    public int MaxPageBytes => Math.Max(1, options.Value.MaxPageBytes);

    public async Task<(IReadOnlyList<KbStoredPageView> Uploaded, IReadOnlyList<KbServedPageView> Served)> ListAsync(CancellationToken ct)
    {
        var rows = await db.KbPages.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new KbStoredPageView(p.Id, p.ContentType, p.SizeBytes, p.Sha256, p.UpdatedAt)).ToListAsync(ct);
        // What this replica serves now, from its own snapshot (it can lag the table until its next refresh).
        var served = kb.Pages.Select(p => new KbServedPageView(p.Id, p.Title, p.Source)).ToList();
        return (rows, served);
    }

    /// <summary>The page as the admin evidence chain records it: type, size and hash, never the content.</summary>
    private static JsonObject Fingerprint(string contentType, int sizeBytes, string sha256) =>
        new() { ["contentType"] = contentType, ["sizeBytes"] = sizeBytes, ["sha256"] = sha256 };

    /// <summary>Creates or replaces a page. A change commits together with its <see cref="AdminEvidence"/> entry, or not at all.</summary>
    public async Task<KbWriteResult> UploadAsync(string id, string? contentType, byte[] body, OperatorIdentity op, string reason, CancellationToken ct)
    {
        if (!ValidId(id)) return new(KbChange.Invalid, "The page id must be 1-64 characters: lower-case letters, digits and inner hyphens.");
        if (AcceptedType(contentType) is not { } type)
            return new(KbChange.UnsupportedType, "Content-Type must be text/markdown or text/plain (UTF-8).");
        if (body.Length > MaxPageBytes) return new(KbChange.TooLarge, $"A page is at most {MaxPageBytes} bytes.");

        string text;
        try { text = StrictUtf8.GetString(body); }
        catch (DecoderFallbackException) { return new(KbChange.UnsupportedType, "The page must be valid UTF-8 text."); }
        text = text.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(text)) return new(KbChange.Invalid, "The page is empty.");
        // Text only: a control character other than tab and line breaks means binary or a disguised payload.
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return new(KbChange.UnsupportedType, "The page must be plain text: no control characters other than tab and line breaks.");

        var bytes = Encoding.UTF8.GetByteCount(text);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        KbChange change;
        KbStoredPage page;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // One knowledge-base write at a time, so the page limit holds and two uploads of one id never collide.
            await db.Database.ExecuteSqlRawAsync(LockSql, ct);
            var existing = await db.KbPages.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (existing is null)
            {
                if (await db.KbPages.CountAsync(ct) >= Math.Max(0, options.Value.MaxPages))
                    return new(KbChange.Invalid, $"The knowledge base holds at most {options.Value.MaxPages} uploaded pages; delete one first.");
                page = new KbStoredPage { Id = id };
                db.KbPages.Add(page);
                change = KbChange.Created;
            }
            else
            {
                page = existing;
                change = existing.Sha256 == sha && existing.ContentType == type ? KbChange.Unchanged : KbChange.Replaced;
            }

            if (change != KbChange.Unchanged)
            {
                var before = existing is null ? null : Fingerprint(existing.ContentType, existing.SizeBytes, existing.Sha256);
                page.ContentType = type;
                page.Body = text;
                page.SizeBytes = bytes;
                page.Sha256 = sha;
                page.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await AdminEvidence.AppendAsync(evidence, op, AdminEvidence.KbPageUpload, "kb-page:" + id, reason, before, Fingerprint(type, bytes, sha), ct);
            }
            await tx.CommitAsync(ct);
        }

        if (change == KbChange.Unchanged) return new(change, Page: KbStoredPageView.Of(page), Served: kb.PageCount);
        var reload = await library.ReloadAsync(reseed: false, ct);
        return new(change, Page: KbStoredPageView.Of(page), Served: reload.Served, ReloadError: reload.Error);
    }

    /// <summary>Removes an uploaded page. The delete commits together with its <see cref="AdminEvidence"/> entry, or not at all.</summary>
    public async Task<KbWriteResult> DeleteAsync(string id, OperatorIdentity op, string reason, CancellationToken ct)
    {
        if (!ValidId(id)) return new(KbChange.NotFound, "Unknown page.");
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // The same lock as uploads, so a delete never races a replace of the same page.
            await db.Database.ExecuteSqlRawAsync(LockSql, ct);
            var existing = await db.KbPages.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new { p.ContentType, p.SizeBytes, p.Sha256 }).FirstOrDefaultAsync(ct);
            if (existing is null) return new(KbChange.NotFound, "Unknown page.");
            await db.KbPages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            await AdminEvidence.AppendAsync(evidence, op, AdminEvidence.KbPageDelete, "kb-page:" + id, reason,
                Fingerprint(existing.ContentType, existing.SizeBytes, existing.Sha256), null, ct);
            await tx.CommitAsync(ct);
        }
        var reload = await library.ReloadAsync(reseed: false, ct);
        return new(KbChange.Deleted, Served: reload.Served, ReloadError: reload.Error);
    }
}
