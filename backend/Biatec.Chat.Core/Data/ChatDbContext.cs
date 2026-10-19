using Microsoft.EntityFrameworkCore;

namespace Biatec.Chat.Core.Data;

public class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ActionPreview> ActionPreviews => Set<ActionPreview>();
    public DbSet<ActionStep> ActionSteps => Set<ActionStep>();
    public DbSet<ConsentRequest> ConsentRequests => Set<ConsentRequest>();
    public DbSet<EvidenceEntry> EvidenceEntries => Set<EvidenceEntry>();
    public DbSet<EvidenceAnchor> EvidenceAnchors => Set<EvidenceAnchor>();
    public DbSet<EvidenceAnchorLeaf> EvidenceAnchorLeaves => Set<EvidenceAnchorLeaf>();
    public DbSet<PublicConversationState> PublicConversationStates => Set<PublicConversationState>();
    public DbSet<PopupAuthRequest> PopupAuthRequests => Set<PopupAuthRequest>();
    public DbSet<PendingRevocation> PendingRevocations => Set<PendingRevocation>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SafeApprovalCount> SafeApprovalCounts => Set<SafeApprovalCount>();
    public DbSet<SoftCapSpend> SoftCapSpends => Set<SoftCapSpend>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored by name so rows stay readable and reordering an enum is safe.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Conversation>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.OidcSub });
            e.HasIndex(x => x.LastMessageAt);
        });

        b.Entity<ChatSession>(e =>
        {
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.AbsoluteExpiresAt);
            e.HasIndex(x => x.LastActivityAt);
        });

        b.Entity<Message>(e =>
        {
            e.HasKey(x => new { x.ConversationId, x.Seq });
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ToolCalls).HasColumnType("jsonb");
            e.Property(x => x.ToolResults).HasColumnType("jsonb");
            e.Property(x => x.Usage).HasColumnType("jsonb");
        });

        b.Entity<ActionPreview>(e =>
        {
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.Property(x => x.CanonicalJson).HasColumnType("jsonb");
            e.Property(x => x.PredictedValueEur).HasPrecision(28, 8);
        });

        b.Entity<ActionStep>(e =>
        {
            e.HasOne<ActionPreview>().WithMany().HasForeignKey(x => x.PreviewId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PreviewId, x.Seq }).IsUnique();
            e.HasIndex(x => x.SubjectHash);
            e.Property(x => x.UnsignedGroup).HasColumnType("jsonb");
            e.Property(x => x.LimitsRequest).HasColumnType("jsonb");
        });

        b.Entity<ConsentRequest>(e =>
        {
            e.HasOne<ActionStep>().WithMany().HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AuthReqId).IsUnique();
            e.HasIndex(x => x.StepId);
            e.Property(x => x.Receipt).HasColumnType("jsonb");
        });

        // Append-only chain per conversation. No FK to Conversation: evidence outlives conversation deletion.
        b.Entity<EvidenceEntry>(e =>
        {
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => x.AnchorRef);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        b.Entity<EvidenceAnchor>(e =>
        {
            e.HasIndex(x => x.SequenceNumber).IsUnique();
            e.HasIndex(x => x.AnchorTxId).IsUnique();
        });

        b.Entity<EvidenceAnchorLeaf>(e =>
        {
            e.HasKey(x => new { x.AnchorId, x.ConversationId });
            e.HasOne<EvidenceAnchor>().WithMany().HasForeignKey(x => x.AnchorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.SiblingHashes).HasColumnType("jsonb");
        });

        b.Entity<PublicConversationState>(e =>
        {
            e.HasKey(x => x.ConversationId);
            e.HasIndex(x => x.ContinuationTokenHash);
            e.HasIndex(x => x.PreviousTokenHash);
        });

        b.Entity<PopupAuthRequest>(e =>
        {
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<PendingRevocation>(e =>
        {
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Host).IsUnique();
            e.Property(x => x.BedrockBudgets).HasColumnType("jsonb");
            e.Property(x => x.SafeApprovalCaps).HasColumnType("jsonb");
            e.Property(x => x.ClientKeys).HasColumnType("jsonb");
            e.Property(x => x.Suggestions).HasColumnType("jsonb");
            e.Property(x => x.ConsentThresholdCeilingEur).HasPrecision(28, 8);
            e.Property(x => x.SoftCapEurPerHour).HasPrecision(28, 8);
        });

        // Rolling-window sums: (sub, tenant, kind or status, time).
        b.Entity<SafeApprovalCount>(e =>
        {
            e.HasIndex(x => new { x.OidcSub, x.TenantId, x.Kind, x.ApprovedAt });
            e.Property(x => x.ValueEur).HasPrecision(28, 8);
        });

        b.Entity<SoftCapSpend>(e =>
        {
            e.HasIndex(x => new { x.OidcSub, x.TenantId, x.ReservedAt });
            e.Property(x => x.AmountEur).HasPrecision(28, 8);
        });
    }
}
