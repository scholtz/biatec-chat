# Hackathon AWS hosting plan

Plan and scripts only. **Nothing here has been run and nothing was provisioned.** Everything that bills while idle needs the
user's explicit approval before `--apply`. Rules come from [AGENTS.md](../AGENTS.md): region `eu-central-1`, no IAM users or
access keys, Claude 4.5 only with `eu.` ids, every resource tagged `Project=biatec`, code freeze Tuesday 20 October 2026 13:30.

## Options and cost per day

Prices are approximate on-demand list prices for eu-central-1 (check the AWS pricing page before approving). Bedrock token
cost is the same in every option and depends on use (the team shares 50 requests/min; Opus is 25/min).

| Option | What runs where | Idle-billing resources | Approx. cost per day (infra only) |
| --- | --- | --- | --- |
| A. Local backend, SSO profile `hackathon` | Backend + compose (postgres, redis, builders) on the presenter laptop; Bedrock via `AWS_PROFILE=hackathon` | None | 0 USD |
| B. EC2 `t4g.small` with instance role | Same compose stack on one instance; Bedrock and KMS via the instance role (IMDSv2) | EC2 about 0.44 USD, 20 GB gp3 about 0.06 USD, public IPv4 about 0.12 USD, KMS key about 0.03 USD | about 0.65 USD (about 20 USD for 30 days) |

Per-hour basis: t4g.small 0.0184 USD, gp3 0.0952 USD per GB-month, public IPv4 0.005 USD, KMS key 1 USD per month plus
0.03 USD per 10k requests.

**Recommendation.** Use **Option A** for the demo: zero idle cost, nothing to approve, and the SSO profile is the supported
local credential path. Choose **Option B** only if the demo must be reachable from a phone and a second device without the
presenter laptop; the stack then needs a public address, which this plan deliberately does not open (no inbound rules; use
SSM Session Manager port forwarding, or add one rule for your own IP on purpose).

## Option A: local with the SSO profile

```
aws sso login --profile hackathon
export AWS_PROFILE=hackathon AWS_REGION=eu-central-1
docker compose up -d
dotnet run --project backend/Biatec.Chat.Api
```

No keys are written anywhere. Local runs use the dev key provider unless `Kms__KeyId` is set to a key you were given.

## Option B: EC2 with an instance role

Created by `infra/aws/create.sh` (dry-run by default):

1. KMS symmetric key plus alias `alias/biatec-chat-vault` for the token vault (idle-billing, about 1 USD/month).
2. IAM role `biatec-chat-instance-role` and instance profile, with the inline policy in
   [`infra/aws/bedrock-role-policy.json`](../infra/aws/bedrock-role-policy.json) and `AmazonSSMManagedInstanceCore` (Session
   Manager instead of SSH).
3. Security group with no inbound rules.
4. One Amazon Linux 2023 arm64 `t4g.small` (allowed type), 20 GB encrypted gp3, IMDSv2 required with hop limit 2 so the
   containers reach the role. User data installs docker and clones the repo.

After the instance is up, open a session (`aws ssm start-session --target <id>`), put the settings in `/opt/biatec-chat/.env`
(typed by the operator, never committed): `Kms__KeyId=<key id>`, the three Bedrock model ids, OIDC settings and secrets.
Then `docker compose up -d --build`.

### Role policy scope

- `bedrock:InvokeModel` and `bedrock:InvokeModelWithResponseStream` only on the three `eu.` inference profiles in this
  account and the matching Claude 4.5 foundation models in EU regions (the profile fans out across EU regions):
  - `eu.anthropic.claude-opus-4-5-20251101-v1:0`
  - `eu.anthropic.claude-sonnet-4-5-20250929-v1:0`
  - `eu.anthropic.claude-haiku-4-5-20251001-v1:0`
- `kms:GenerateDataKey` and `kms:Decrypt` only on the vault key ARN, and only with encryption context
  `purpose=biatec-chat-token-vault` (the context `KmsKeyProvider` uses).
- Nothing else: no wildcard model access, no IAM, no S3.

## Approval gate

Idle-billing resources in Option B that need the user's explicit approval: EC2 instance, EBS volume, public IPv4 address, KMS
key. The scripts enforce it twice: they only print commands unless you pass `--apply`, and `--apply` is refused unless
`BIATEC_AWS_APPROVED=yes` is set by the person who approved.

```
infra/aws/create.sh                                  # dry-run: prints every command
BIATEC_AWS_APPROVED=yes infra/aws/create.sh --apply  # only after explicit approval
```

## Teardown

`infra/aws/teardown.sh` finds everything tagged `Project=biatec` in eu-central-1 through the Resource Groups Tagging API and
deletes it in dependency order: instances, security groups, volumes, then the IAM role and profile, then the KMS alias and
key. KMS keys can't be deleted immediately; they are scheduled for deletion with the minimum 7-day window (the key stops
billing once scheduled). Tagged resources of a type the script doesn't know are reported as a warning, not skipped. Also
dry-run by default:

```
infra/aws/teardown.sh
BIATEC_AWS_APPROVED=yes infra/aws/teardown.sh --apply
```

Every delete tolerates resources that are already gone (so teardown can be re-run after a partial failure): volumes removed with the instance and keys already pending deletion are skipped. IAM is global and its tags are not returned by the regional Tagging API, so the role and instance profile are looked up and deleted by the names `create.sh` uses (`biatec-chat-instance-role`, `biatec-chat-instance-profile`); a hand-made tagged IAM resource under another name is not found. `create.sh` waits for the instance profile (`aws iam wait instance-profile-exists`) and retries `run-instances` on `Invalid IAM Instance Profile`; it is not idempotent, and on failure it reminds you to run teardown.

Teardown's discovery step is a read-only call, so even its dry-run needs a logged-in `hackathon` profile. Run it after the demo
and again in dry-run mode to confirm the tagged list is empty.

## Verification status

Scripts are syntax-checked with `bash -n infra/aws/*.sh`. They have not been executed against AWS. Costs are estimates.
