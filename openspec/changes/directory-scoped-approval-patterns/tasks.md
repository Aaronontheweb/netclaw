## 1. Pattern Extraction

- [x] 1.1 Add `ShellTokenizer.ExtractDirectoryScope()` — scan all args for first `LooksLikePath()` token, extract parent directory, normalize, enforce minimum depth
- [x] 1.2 Add `ExtractParentDirectory()` and `CountPathSegments()` helpers to `ShellTokenizer`
- [x] 1.3 Unit tests for `ExtractDirectoryScope` (verb+directory, grep path vs search term, glob handling, null cases)

## 2. Pattern Matching

- [x] 2.1 Add `MatchesDirectoryScope()` to `ApprovalPatternMatching` using `PathUtility.IsWithinRoot()` for boundary-safe containment
- [x] 2.2 Unit tests for directory-prefix matching (same dir, nested, sibling, verb mismatch)

## 3. IToolApprovalMatcher Extension

- [x] 3.1 Add `ExtractDirectoryPatterns()` to `IToolApprovalMatcher` interface
- [x] 3.2 Implement on `ShellApprovalMatcher` with compound command + `bash -c` recursion via shared `TraverseSegments` helper
- [x] 3.3 Implement on `DefaultApprovalMatcher` and `FilePathApprovalMatcher` (return empty list)

## 4. Protocol and Pipeline Wiring

- [x] 4.1 Add `DirectoryPatterns` property to `ToolInteractionRequest` in `SessionOutput.cs`
- [x] 4.2 Add `DirectoryPatterns` to `ToolApprovalContext` record in `ToolAccessPolicy.cs`
- [x] 4.3 Compute directory patterns and customize B/C labels in `CheckApprovalGate()`
- [x] 4.4 Pass `DirectoryPatterns` from `ToolApprovalContext` to `ToolInteractionRequest` in `SessionToolExecutionPipeline`
- [x] 4.5 Propagate `DirectoryPatterns` through `DispatchingToolExecutor` re-approval path

## 5. Session Actor Recording

- [x] 5.1 Add `DirectoryPatterns` field to `PendingToolInteraction` record in `LlmSessionActor`
- [x] 5.2 Store `DirectoryPatterns` from `ToolInteractionRequest` in pending interaction
- [x] 5.3 Record directory patterns (when non-empty) instead of exact patterns for B/C decisions in `RecordApprovalAsync`

## 6. Code Quality

- [x] 6.1 Narrow bare `catch` in `MatchesDirectoryScope` to `ArgumentException | IOException`
- [x] 6.2 Unify `CollectPatterns`/`CollectDirectoryPatterns` into shared `TraverseSegments` helper
- [x] 6.3 Use `PathUtility.ExpandAndNormalize()` in `ExtractDirectoryScope` instead of separate calls
- [x] 6.4 Make `DirectoryPatterns` non-nullable on `ToolApprovalContext`
- [x] 6.5 Verify: `dotnet slopwatch analyze` passes, copyright headers present, all tests green
