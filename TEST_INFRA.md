# Valve E2E Test Infrastructure & Methodology (TEST_INFRA.md)

## 1. Executive Overview & Test Philosophy

The Valve Test Infrastructure provides an independent, opaque-box, requirement-driven test harness designed to rigorously verify the NetLimiter-class network traffic control engine across Windows 10 and Windows 11.

### Core Principles
1. **Opaque-Box & Requirement-Driven**: Tests are derived strictly from documented specifications and the 311 checklist items in `spec_miner_survey_1/checklist_spec_report.md`. Tests verify observable behavioral contracts and system guarantees rather than private implementation details.
2. **Deterministic & Self-Contained**: Every test establishes its own isolated environment, creates unique temporary working directories, and cleans up after execution. Tests are independent of execution order and leave zero persistent filesystem or system state artifacts.
3. **No Facade Tests**: Every test exercises real computational and algorithmic logic with rigorous assertions. Zero dummy assertions (e.g. `Assert.True(true)`) or untested stub executions are permitted.
4. **Strict Pass/Fail Semantics**: The test runner enforces deterministic exit codes: `0` indicates that 100% of executed test cases passed; `1` indicates one or more assertions failed.

---

## 2. 4-Tier Test Methodology & Architecture

The test suite is structured into four distinct, progressive tiers:

```
+-----------------------------------------------------------------------+
|                       VALVE E2E TEST HARNESS                          |
+-----------------------------------------------------------------------+
|  Tier 1: Feature Coverage (Checklist Verification)                    |
|  - Traffic monitoring, rate calculation, units (B/s, b/s, kbit/s)     |
|  - Token-bucket rate limiting (inbound & outbound)                    |
|  - 4-tier traffic priority queuing (Critical, High, Normal, Low)      |
|  - Connection blocker states & WFP ALE filter contracts               |
|  - Filter rules (IP, port, protocol, path, zone, tag, domain)         |
|  - Quotas & schedules (midnight crossing, day masks)                  |
|  - AtomicStorage crash-resilient persistence & backup healing         |
+-----------------------------------------------------------------------+
|  Tier 2: Boundary & Corner Cases                                      |
|  - Exact minute schedule windows & midnight wrap (23:00 - 02:00)      |
|  - Zero bandwidth, 10 Gbps extreme limits, microsecond bursts         |
|  - Wildcard domains (*.example.com), subnets, empty/malformed values  |
|  - Torn-write simulation (null-byte padding) & auto-recovery          |
|  - Quota boundaries (exact threshold, overflow, clamp)                |
+-----------------------------------------------------------------------+
|  Tier 3: Pairwise Interactions                                        |
|  - Static limits vs. Schedule window overrides (tightest cap wins)    |
|  - Filter rules vs. Blocker decisions                                 |
|  - Bandwidth rate limiting combined with Priority queues              |
|  - Quota exceeded triggering rule actions                             |
|  - Multi-condition compound filter evaluations                        |
+-----------------------------------------------------------------------+
|  Tier 4: Real-World Scenarios                                         |
|  - Concurrent multi-threaded packet traffic bursts                    |
|  - Sudden power-loss / torn-write recovery with .bak auto-healing     |
|  - High-frequency rule & schedule configuration churn                 |
|  - Multi-process bandwidth contention simulation                      |
+-----------------------------------------------------------------------+
```

### Tier Descriptions & Mapping to Checklist Items

| Tier | Category | Focus | Checklist Item Alignment |
|---|---|---|---|
| **Tier 1** | **Feature Coverage** | Primary behavioral verification of discrete core features: rate calculation, token-bucket shaper, 4-tier priority, WFP connection blocker states, filter criteria matching, schedule windows, and atomic persistence. | Sections 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 21, 24, 26, 27 |
| **Tier 2** | **Boundary & Corner Cases** | Edge cases, zero and extreme limits, midnight-crossing schedules, wildcard edge cases, torn-write corrupt payloads, truncated JSON, and counter overflow protection. | Sections 1.05, 1.06, 4.02, 5.01, 7.02, 27.05 |
| **Tier 3** | **Pairwise Interactions** | Interaction matrix between subsystems: schedules interacting with static caps, blocker states interacting with application filters, priority levels interacting with rate limiters. | Sections 2.01, 3.01, 4.04, 5.04, 7.04, 24.01 |
| **Tier 4** | **Real-World Scenarios** | Complex end-to-end stress scenarios: multi-threaded concurrent traffic flow, abrupt process/power failure recovery from backup, and concurrent rule mutation during active traffic monitoring. | Sections 8.01, 10.01, 26.02, 27.01, 28.01 |

---

## 3. Directory Layout

The test harness and test suites reside in `Valve\e2e_tests\`:

```
Valve/
├── TEST_INFRA.md                          # This architecture & methodology document
├── TEST_READY.md                          # Test readiness & status signoff
└── e2e_tests/                             # E2E Test Project & Harness
    ├── Valve.E2ETests.csproj              # .NET 8.0 Windows x64 Test Executable Project
    ├── TestRunner.cs                      # CLI Runner with filtering, tier selection, & JSON export
    ├── run-e2e-tests.ps1                  # PowerShell execution wrapper
    ├── run-e2e-tests.bat                  # Windows Batch execution wrapper
    ├── Framework/                         # Test runner framework primitives
    │   ├── TestCase.cs                    # Test attribute & test case abstraction
    │   ├── TestAssert.cs                  # Behavioral assertions with detailed failure messages
    │   ├── TestResult.cs                  # Structured execution result representation
    │   └── TestTier.cs                    # Tier 1..4 enum flags
    └── Suites/                            # Test Suites
        ├── Tier1_FeatureCoverage/         # Tier 1: Feature tests
        │   ├── TrafficRateCalculationTests.cs
        │   ├── BandwidthLimiterTests.cs
        │   ├── PriorityQueueTests.cs
        │   ├── ConnectionBlockingTests.cs
        │   ├── FilterRuleEngineTests.cs
        │   ├── QuotaAndScheduleTests.cs
        │   └── PersistenceStorageTests.cs
        ├── Tier2_BoundaryCornerCases/     # Tier 2: Boundary tests
        │   ├── ScheduleMidnightBoundaryTests.cs
        │   ├── TokenBucketBoundaryTests.cs
        │   ├── FilterRuleEdgeCasesTests.cs
        │   ├── TornWriteCorruptStorageTests.cs
        │   └── QuotaBoundaryTests.cs
        ├── Tier3_PairwiseInteractions/    # Tier 3: Pairwise interaction tests
        │   ├── ScheduleAndStaticCapInteractions.cs
        │   ├── FilterAndBlockerInteractions.cs
        │   ├── RateAndPriorityInteractions.cs
        │   └── QuotaAndRuleActionInteractions.cs
        └── Tier4_RealWorldScenarios/      # Tier 4: Real-world stress scenarios
            ├── ConcurrentTrafficSimulationTests.cs
            ├── SuddenPowerFailureRecoveryScenario.cs
            └── RapidConfigurationChurnScenario.cs
```

---

## 4. Execution Instructions

### Option A: Using the PowerShell Script
```powershell
# Run all tiers (Tiers 1, 2, 3, 4)
powershell -ExecutionPolicy Bypass -File .\e2e_tests\run-e2e-tests.ps1

# Run a specific tier (e.g. Tier 1 or Tier 2)
powershell -ExecutionPolicy Bypass -File .\e2e_tests\run-e2e-tests.ps1 -Tier 1
powershell -ExecutionPolicy Bypass -File .\e2e_tests\run-e2e-tests.ps1 -Tier 2

# Run tests matching a specific pattern
powershell -ExecutionPolicy Bypass -File .\e2e_tests\run-e2e-tests.ps1 -Filter "Storage"

# Export structured JSON test report
powershell -ExecutionPolicy Bypass -File .\e2e_tests\run-e2e-tests.ps1 -JsonOutput "test_results.json"
```

### Option B: Using the Windows Batch Script
```cmd
# Run all tests
.\e2e_tests\run-e2e-tests.bat

# Run with arguments
.\e2e_tests\run-e2e-tests.bat --tier 1 --json results.json
```

### Option C: Direct `dotnet run`
```powershell
dotnet run --project ".\e2e_tests\Valve.E2ETests.csproj" -- --tier all
dotnet run --project ".\e2e_tests\Valve.E2ETests.csproj" -- --tier 1 --verbose
dotnet run --project ".\e2e_tests\Valve.E2ETests.csproj" -- --filter "Midnight"
```

---

## 5. Pass/Fail Semantics & Output Formats

### Exit Code Contract
- **Exit Code 0 (`SUCCESS`)**: Every executed test case passed without assertion failures or unhandled exceptions.
- **Exit Code 1 (`FAILURE`)**: At least one test case failed an assertion or threw an unhandled exception.

### Interactive Console Output
The test runner provides high-visibility console output formatted with ANSI colors:
```
================================================================================
 VALVE E2E TEST RUNNER - 4-TIER VERIFICATION SUITE
================================================================================
 [TIER 1] TrafficRateCalculationTests.FormatRate_BytesPerSecond_FormatsCorrectly ... PASS (2 ms)
 [TIER 1] BandwidthLimiterTests.TokenBucket_LimitsThroughputToConfiguredRate ... PASS (15 ms)
 [TIER 2] ScheduleMidnightBoundaryTests.ScheduleCrossingMidnight_ActiveAtMidnight ... PASS (1 ms)
 [TIER 3] ScheduleAndStaticCapInteractions.EffectiveLimit_TighterStaticCapWins ... PASS (1 ms)
 [TIER 4] ConcurrentTrafficSimulationTests.ConcurrentPackets_CalculatesTotalBytesAccurately ... PASS (42 ms)
--------------------------------------------------------------------------------
 Summary: 38 Passed, 0 Failed, 0 Skipped (Total: 38)
 Duration: 00:00:01.245
 Result: ALL TESTS PASSED (Exit Code: 0)
================================================================================
```

### Machine-Readable JSON Output (`--json <path>`)
When invoked with `--json <path>`, a structured JSON artifact is written:
```json
{
  "timestampUtc": "2026-10-29T15:40:00Z",
  "totalTests": 38,
  "passed": 38,
  "failed": 0,
  "durationMs": 1245,
  "success": true,
  "results": [
    {
      "name": "TokenBucket_LimitsThroughputToConfiguredRate",
      "suite": "BandwidthLimiterTests",
      "tier": "Tier1",
      "passed": true,
      "durationMs": 15,
      "errorMessage": null
    }
  ]
}
```

---

## 6. Automated Phase Gating & CI Verification

During project execution, the orchestrator and milestone audits execute the test harness at each phase boundary:
1. **Phase 1 Boundary**: Verifies Tier 1 Traffic Rate Calculation and Traffic Monitoring suites.
2. **Phase 2 Boundary**: Verifies Bandwidth Limiting (Token Bucket) and Priority Queue suites.
3. **Phase 3 Boundary**: Verifies Filter Rule Engine and Multi-Condition Matching suites.
4. **Phase 4 Boundary**: Verifies Connection Blocker and WFP ALE Filter contract suites.
5. **Phase 5 Boundary**: Verifies Quotas, Schedules, and Midnight Crossing suites.
6. **Phase 7-10 Boundaries**: Verifies AtomicStorage, Power-Failure Resilience, and Concurrent Churn scenarios.

---

## 7. Privileges & Environment Setup

- **Administrator Privileges**: While live WFP filter installation requires elevated privileges, all unit and behavioral E2E tests in this harness are engineered with clean isolation and mockable/pure contracts to execute reliably in development environments.
- **Driverless Mode**: The test suite exercises the core interception algorithms (token-bucket rate limiting, port-to-PID mapping, ALE rule models, atomic JSON persistence, and schedule algebra) without requiring active kernel drivers or testsigning mode.
