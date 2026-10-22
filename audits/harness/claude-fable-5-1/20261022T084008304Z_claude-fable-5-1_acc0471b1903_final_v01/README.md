# Harness: cycle 1, commit acc0471b1903

Re-runnable checks for [the cycle 1 report](../../../reports/20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01.md).
Produced by an AI-run cycle; a human assessor must re-run it (report limitation L1).

## Run

```bash
git checkout acc0471b19035fcc1852270955d6808878fa9d5e   # audited commit; use a later commit for regression
docker compose up -d redis postgres                      # TEST-001 only
BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01/run-all.sh
BIATEC_ROOT="$PWD" bash .../TEST-008.sh                  # a single test
```

Each script prints `PASS` or `FAIL` and exits non-zero on failure. `run-all.sh` exits with the number of failures.
Prerequisites: .NET 10 SDK, pnpm 10, Node 22+, network access to package registries for TEST-003 and TEST-004.
Expected runtime: about 5 minutes (TEST-001 dominates).

## Expected result at the audited commit

| Test | Expected | Meaning |
| --- | --- | --- |
| TEST-001, 002, 005, 006, 007 | PASS | baseline and invariants hold |
| TEST-003, 004 | FAIL | recorded dependency advisories (BCHAT-046) |
| TEST-008 to 015 | FAIL | one open finding each (BCHAT-001, 005, 006, 012, 015, 008, 017, 003) |

A test flipping from FAIL to PASS means a finding was fixed: retest it per instructions section 11.
A PASS flipping to FAIL is a regression and a new finding. TEST-008 to 015 are static source checks and
only prove the fix is present; each finding also needs the unit test described in the report.
TEST-016 to 026 have no script: they are the code-review ledger or need `staging`, `device` or `live` access.
