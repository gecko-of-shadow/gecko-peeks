# logic-fixes progress

Branch `logic-fixes`, one commit per item. Run `dotnet test -c Release` after each.

Baseline on `main` (before any item): 184/185 pass. `PendingLogTests.ReadAll_does_not_throw_while_append_holds_the_writer`
failed 4 of 4 runs on main here, then passed after item 1: a flaky pending-file sharing race in CsvLog, outside items 1-6, left as is.

Decision from the user (item 4): an indeterminate (blank/Unknown) test while a route change awaits
confirmation is skipped; it neither confirms nor resets the pending change.

| # | Item | Status | Commit | What changed |
|---|------|--------|--------|--------------|
| 1 | Riot ping target = TraceTarget | done | a851e9c | RiotPingTarget = TraceTarget; note text + README updated; TestRunner.cs linked into tests |
| 2 | Last-hop latency mtr-style (TTL-limited echoes) | done | bbaec23 | LastHop() skips the target; TTL=hop echoes to TraceTarget, only Time Exceeded from that IP counts; README updated |
| 3 | 3 probes per TTL | done | b0523d6 | ProbesPerHop=3; MergeProbes(): first answering IP, its lowest RTT, any Success reaches target; silent only if all 3 fail |
| 4 | False change alerts (HopDiff stars, city names, 2-test confirm) | done | (see next row's commit) | HopDiff ignores IP<->*; RouteChange compares Display() names, blank-city-after-city is indeterminate; RouteChangeConfirmer (in RouteChange.cs) gates the balloon on 2 agreeing tests |
| 5 | Adapter/VPN via GetBestInterfaceEx | todo | | |
| 6 | City codes (cs prefix, 4-letter known codes, kanc/palo) | todo | | |

A commit cannot contain its own hash, so each item's hash is filled in by the next commit.

Resume: read this file and `git log logic-fixes`, then start at the first `todo` row.
