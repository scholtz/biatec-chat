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
    public DbSet<EvidenceSigningKey> EvidenceSigningKeys => Set<EvidenceSigningKey>();
    public DbSet<EvidenceAnchor> EvidenceAnchors => Set<EvidenceAnchor>();
    public DbSet<EvidenceAnchorLeaf> EvidenceAnchorLeaves => Set<EvidenceAnchorLeaf>();
    public DbSet<EvidenceAnchorAttempt> EvidenceAnchorAttempts => Set<EvidenceAnchorAttempt>();
    public DbSet<PublicConversationState> PublicConversationStates => Set<PublicConversationState>();
    public DbSet<PopupAuthRequest> PopupAuthRequests => Set<PopupAuthRequest>();
    public DbSet<PendingRevocation> PendingRevocations => Set<PendingRevocation>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SafeApprovalCount> SafeApprovalCounts => Set<SafeApprovalCount>();
    public DbSet<SoftCapSpend> SoftCapSpends => Set<SoftCapSpend>();
    public DbSet<KnownSafe> KnownSafes => Set<KnownSafe>();
    public DbSet<KbStoredPage> KbPages => Set<KbStoredPage>();
    public DbSet<KnownUser> KnownUsers => Set<KnownUser>();

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
            // A writer that leaves CreatedAt unset gets the database clock, never year 1 (which retention would clear at once).
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.CreatedAt);
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
            // Serves the session liveness EXISTS (T-080) and every StepId lookup.
            e.HasIndex(x => new { x.StepId, x.Status, x.ExpiresAt });
            e.Property(x => x.Receipt).HasColumnType("jsonb");
        });

        // Append-only chain per conversation. No FK to Conversation: evidence outlives conversation deletion.
        b.Entity<EvidenceEntry>(e =>
        {
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => x.AnchorRef);
            e.HasIndex(x => x.SignedAt);
            e.Property(x => x.AnchorRef).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        b.Entity<EvidenceSigningKey>(e =>
        {
            e.HasKey(x => x.Kid);
            e.Property(x => x.Kid).HasMaxLength(64);
            e.HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\"");
        });

        b.Entity<EvidenceAnchor>(e =>
        {
            e.HasIndex(x => x.SequenceNumber).IsUnique();
            e.HasIndex(x => x.AnchorTxId).IsUnique();
        });

        b.Entity<EvidenceAnchorAttempt>(e =>
        {
            e.HasKey(x => x.SequenceNumber);
            e.Property(x => x.SequenceNumber).ValueGeneratedNever();
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Leaves).HasColumnType("jsonb");
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

        b.Entity<KnownSafe>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Network, x.AppId }).IsUnique();
            e.HasIndex(x => x.DeployTxId);
            e.Property(x => x.TenantId).HasMaxLength(128);
            e.Property(x => x.Network).HasMaxLength(32);
            e.Property(x => x.DeployTxId).HasMaxLength(64);
        });

        b.Entity<KbStoredPage>(e =>
        {
            e.ToTable("KbPages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ContentType).HasMaxLength(32);
            e.Property(x => x.Sha256).HasMaxLength(64);
        });

        b.Entity<KnownUser>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.OidcSub }).IsUnique();
            e.HasIndex(x => x.LastSeenAt);
            e.Property(x => x.TenantId).HasMaxLength(128);
            e.Property(x => x.OidcSub).HasMaxLength(256);
        });
    }
}
