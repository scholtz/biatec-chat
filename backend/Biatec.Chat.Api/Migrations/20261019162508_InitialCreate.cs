using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Biatec.Chat.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    OidcSub = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceAnchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    PreviousAnchorTxId = table.Column<string>(type: "text", nullable: true),
                    AnchorTxId = table.Column<string>(type: "text", nullable: false),
                    MerkleRoot = table.Column<string>(type: "text", nullable: false),
                    AnchoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GapFrom = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceAnchors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    AnchorRef = table.Column<Guid>(type: "uuid", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    Salt = table.Column<string>(type: "text", nullable: true),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    PrevHash = table.Column<string>(type: "text", nullable: false),
                    EntryHash = table.Column<string>(type: "text", nullable: false),
                    Kid = table.Column<string>(type: "text", nullable: false),
                    SignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingRevocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    EncryptedRefreshToken = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRevocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PopupAuthRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NonceHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PopupAuthRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicConversationStates",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityHash = table.Column<string>(type: "text", nullable: false),
                    ContinuationTokenHash = table.Column<string>(type: "text", nullable: false),
                    PreviousTokenHash = table.Column<string>(type: "text", nullable: true),
                    LastReplyRef = table.Column<string>(type: "text", nullable: true),
                    AdoptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicConversationStates", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "SafeApprovalCounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OidcSub = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValueEur = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SafeApprovalCounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoftCapSpends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OidcSub = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SpentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AmountEur = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    ValueSource = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftCapSpends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    SignInPageUrl = table.Column<string>(type: "text", nullable: true),
                    BedrockBudgets = table.Column<string>(type: "jsonb", nullable: false),
                    SafeApprovalCaps = table.Column<string>(type: "jsonb", nullable: false),
                    ConsentThresholdCeilingEur = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    LimitsUpdateEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    X402Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OidcClientId = table.Column<string>(type: "text", nullable: false),
                    ClientSecretRef = table.Column<string>(type: "text", nullable: false),
                    RedirectUri = table.Column<string>(type: "text", nullable: false),
                    AllowedOrigins = table.Column<string[]>(type: "text[]", nullable: false),
                    ClientKeys = table.Column<string>(type: "jsonb", nullable: false),
                    SoftCapEurPerHour = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    TrustAnchorVerified = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultModel = table.Column<string>(type: "text", nullable: true),
                    Suggestions = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActionPreviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "text", nullable: false),
                    CanonicalJson = table.Column<string>(type: "jsonb", nullable: false),
                    PredictedValueEur = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    PredictedNeedsConsent = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RebuiltAsPreviewId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionPreviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionPreviews_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    OidcSub = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatSessions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ToolCalls = table.Column<string>(type: "jsonb", nullable: true),
                    ToolResults = table.Column<string>(type: "jsonb", nullable: true),
                    ModelId = table.Column<string>(type: "text", nullable: true),
                    Usage = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => new { x.ConversationId, x.Seq });
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceAnchorLeaves",
                columns: table => new
                {
                    AnchorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnchoredSeq = table.Column<long>(type: "bigint", nullable: false),
                    HeadHash = table.Column<string>(type: "text", nullable: false),
                    LeafIndex = table.Column<int>(type: "integer", nullable: false),
                    SiblingHashes = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceAnchorLeaves", x => new { x.AnchorId, x.ConversationId });
                    table.ForeignKey(
                        name: "FK_EvidenceAnchorLeaves_EvidenceAnchors_AnchorId",
                        column: x => x.AnchorId,
                        principalTable: "EvidenceAnchors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectHash = table.Column<string>(type: "text", nullable: false),
                    UnsignedGroup = table.Column<string>(type: "jsonb", nullable: true),
                    LimitsRequest = table.Column<string>(type: "jsonb", nullable: true),
                    SenderAddress = table.Column<string>(type: "text", nullable: true),
                    Network = table.Column<string>(type: "text", nullable: true),
                    FirstValid = table.Column<long>(type: "bigint", nullable: true),
                    LastValid = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TxIds = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionSteps_ActionPreviews_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "ActionPreviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConsentRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectHash = table.Column<string>(type: "text", nullable: false),
                    AuthReqId = table.Column<string>(type: "text", nullable: false),
                    BindingCode = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Receipt = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentRequests_ActionSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "ActionSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionPreviews_ConversationId_Status",
                table: "ActionPreviews",
                columns: new[] { "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionSteps_PreviewId_Seq",
                table: "ActionSteps",
                columns: new[] { "PreviewId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActionSteps_SubjectHash",
                table: "ActionSteps",
                column: "SubjectHash");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRequests_AuthReqId",
                table: "ConsentRequests",
                column: "AuthReqId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRequests_StepId",
                table: "ConsentRequests",
                column: "StepId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastMessageAt",
                table: "Conversations",
                column: "LastMessageAt");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId_OidcSub",
                table: "Conversations",
                columns: new[] { "TenantId", "OidcSub" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceAnchorLeaves_ConversationId",
                table: "EvidenceAnchorLeaves",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceAnchors_AnchorTxId",
                table: "EvidenceAnchors",
                column: "AnchorTxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceAnchors_SequenceNumber",
                table: "EvidenceAnchors",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEntries_AnchorRef",
                table: "EvidenceEntries",
                column: "AnchorRef");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEntries_ConversationId_Seq",
                table: "EvidenceEntries",
                columns: new[] { "ConversationId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_AbsoluteExpiresAt",
                table: "ChatSessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ConversationId",
                table: "ChatSessions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_LastActivityAt",
                table: "ChatSessions",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRevocations_ExpiresAt",
                table: "PendingRevocations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRevocations_TenantId",
                table: "PendingRevocations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PopupAuthRequests_ConversationId",
                table: "PopupAuthRequests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_PopupAuthRequests_ExpiresAt",
                table: "PopupAuthRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PublicConversationStates_ContinuationTokenHash",
                table: "PublicConversationStates",
                column: "ContinuationTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_PublicConversationStates_PreviousTokenHash",
                table: "PublicConversationStates",
                column: "PreviousTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_SafeApprovalCounts_OidcSub_TenantId_Kind_ApprovedAt",
                table: "SafeApprovalCounts",
                columns: new[] { "OidcSub", "TenantId", "Kind", "ApprovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftCapSpends_OidcSub_TenantId_ReservedAt",
                table: "SoftCapSpends",
                columns: new[] { "OidcSub", "TenantId", "ReservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Host",
                table: "Tenants",
                column: "Host",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentRequests");

            migrationBuilder.DropTable(
                name: "EvidenceAnchorLeaves");

            migrationBuilder.DropTable(
                name: "EvidenceEntries");

            migrationBuilder.DropTable(
                name: "ChatSessions");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "PendingRevocations");

            migrationBuilder.DropTable(
                name: "PopupAuthRequests");

            migrationBuilder.DropTable(
                name: "PublicConversationStates");

            migrationBuilder.DropTable(
                name: "SafeApprovalCounts");

            migrationBuilder.DropTable(
                name: "SoftCapSpends");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "ActionSteps");

            migrationBuilder.DropTable(
                name: "EvidenceAnchors");

            migrationBuilder.DropTable(
                name: "ActionPreviews");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
