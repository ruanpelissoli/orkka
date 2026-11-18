# Run 20261118-0058-adhoc

| | |
|---|---|
| Suite | `adhoc` |
| Target | http://127.0.0.1:6750 |
| Started | 2026-11-18T03:58:48.071Z |
| Duration | 1.7 s |
| Result | 1 passed · 1 failed · 0 timed out · 0 skipped |
| Operator | claude · harness 7c790e44baab · node v24.18.0 · playwright 1.63.0 |

## Cases

| Result | Case IDs | Test | Duration | Cost | Project(s) |
|---|---|---|---|---|---|
| ❌ failed | `NEG-030` | NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression) | 89 ms | — | e2e-neg-20261118-0058cwmhk |
| ✅ passed | `NEG-031` | NEG-031 the removed cloud-execution surface is gone: routes 404 | 151 ms | — | e2e-neg-20261118-0058-9mgsd |

## Failures

### ❌ NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression)

- File: `tests/smoke/negatives.spec.ts`
- Cases: `NEG-030`
- Projects: `01M7PCXAHXNHAYT4EV1SQR60B9 e2e-neg-20261118-0058cwmhk`

```
Error: project reachable by its 26-char slug

[2mexpect([22m[31mreceived[39m[2m).[22mnot[2m.[22mtoBeInstanceOf[2m([22m[32mexpected[39m[2m)[22m

Expected constructor: not [32mApiError[39m

```
Logs: API + Worker console output of the stack at http://127.0.0.1:6750; runner log .runs/runner.log (grep for 01M7PCXAHXNHAYT4EV1SQR60B9)

## Cleanup

- Projects created this run: 3
  - `e2e-neg-20261118-0058cwmhk` (01M7PCXAHXNHAYT4EV1SQR60B9)
  - `e2e-neg-20261118-0058cwmhk` (01M7PCXAHXNHAYT4EV1SQR60B9)
  - `e2e-neg-20261118-0058-9mgsd` (01M7PCXAN3ZQPZ8BED23KCSXZ7)
- Leftovers after teardown: none
