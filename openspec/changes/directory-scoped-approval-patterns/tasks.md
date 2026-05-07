## 1. Pattern Extraction

- [x] 1.1 Add `ShellTokenizer.ExtractDirectoryRoots()` and shell approval-unit traversal that splits on `&&`, `||`, and `;` but keeps `|` in the same unit
- [x] 1.2 Add helpers to derive reusable parent directory roots, normalize paths, and enforce minimum depth
- [x] 1.3 Unit tests for root extraction (later path args, pipelines, control-operator splits, glob handling, null cases)

## 2. Pattern Matching

- [x] 2.1 Add directory-root matching using `PathUtility.IsWithinRoot()` for boundary-safe containment
- [x] 2.2 Unit tests for root matching (same dir, nested, sibling, multi-path coverage, prefix collision)

## 3. IToolApprovalMatcher Extension

- [x] 3.1 Rename `ExtractDirectoryPatterns()` to `ExtractDirectoryRoots()` on `IToolApprovalMatcher`
- [x] 3.2 Implement on `ShellApprovalMatcher` with approval-unit traversal and `bash -c` recursion via shared traversal helper
- [x] 3.3 Implement on `DefaultApprovalMatcher` and `FilePathApprovalMatcher` (return empty list)

## 4. Protocol and Pipeline Wiring

- [x] 4.1 Rename `DirectoryPatterns` to `DirectoryRoots` on `ToolInteractionRequest` in `SessionOutput.cs`
- [x] 4.2 Rename `DirectoryPatterns` to `DirectoryRoots` on `ToolApprovalContext` in `ToolAccessPolicy.cs`
- [x] 4.3 Compute directory roots and customize B/C labels in `CheckApprovalGate()`
- [x] 4.4 Pass `DirectoryRoots` from `ToolApprovalContext` to `ToolInteractionRequest` in `SessionToolExecutionPipeline`
- [x] 4.5 Propagate `DirectoryRoots` through `DispatchingToolExecutor` re-approval path

## 5. Session Actor Recording

- [x] 5.1 Rename the pending interaction field from `DirectoryPatterns` to `DirectoryRoots` in `LlmSessionActor`
- [x] 5.2 Store `DirectoryRoots` from `ToolInteractionRequest` in pending interaction
- [x] 5.3 Record directory roots (when non-empty) instead of exact patterns for B/C decisions in `RecordApprovalAsync`

## 6. Code Quality

- [x] 6.1 Narrow bare `catch` in directory-root matching to `ArgumentException | IOException`
- [x] 6.2 Unify exact-pattern collection and directory-root extraction into shared approval-unit traversal
- [x] 6.3 Use `PathUtility.ExpandAndNormalize()` in `ExtractDirectoryRoots` instead of separate calls
- [x] 6.4 Make `DirectoryRoots` non-nullable on `ToolApprovalContext`
- [x] 6.5 Verify: `dotnet slopwatch analyze` passes, copyright headers present, all tests green
