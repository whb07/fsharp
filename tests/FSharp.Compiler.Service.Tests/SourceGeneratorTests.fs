module FSharp.Compiler.Service.Tests.SourceGeneratorTests

open System
open System.IO
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Service.Tests.Common
open FSharp.Compiler.SourceGeneration

/// A simple source generator whose output is configured at construction time.
type CapturedGenerator(outputs: FSharpGeneratedSource list, ?diagnostics: FSharpSourceGeneratorDiagnostic list) =
    interface IFSharpSourceGenerator with
        member _.Generate(_context) =
            {
                GeneratedSources = outputs
                Diagnostics = defaultArg diagnostics []
            }

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "FSharpSourceGenTests", Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    dir

let private writeSourceFile dir name (source: string) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, source)
    path

let private buildLibraryArgv (dllPath: string) (sourceFiles: string list) =
    let args = mkProjectCommandLineArgsSilent (dllPath, sourceFiles)
    // The compiler driver drops the first argv element (the executable name).
    Array.append [| "fsc.exe" |] args

let private defaultGeneratorOptions outputDir =
    {
        OutputDirectory = outputDir
        EmitGeneratedFiles = true
        AdditionalFiles = []
        AnalyzerConfigFiles = []
        MaxPasses = 1
    }

let checker = FSharpChecker.Create()

[<Fact>]
let ``CompileWithSourceGenerators_GeneratedFileCompiles`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet value = Generated.Greeting\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ sourceFile ]

        let generatedPath = Path.Combine(dir, "Generated.fs")
        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GreetingGenerator"
                        FileName = generatedPath
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let diagnostics, _runResult, compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        for d in diagnostics do
            printfn "%A" d

        Assert.True(compileException.IsNone, sprintf "Compile threw: %A" compileException)
        Assert.Empty(diagnostics |> Seq.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error))
        Assert.True(File.Exists dllPath, "Expected the output dll to be produced")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``CompileWithSourceGenerators_GeneratedFsiBeforeFsCompiles`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ sourceFile ]

        let sigPath = Path.Combine(dir, "Generated.fsi")
        let implPath = Path.Combine(dir, "Generated.fs")

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GeneratedSig"
                        FileName = sigPath
                        SourceText = "module Generated\nval Greeting : int\n"
                        Kind = FSharpGeneratedSourceKind.Signature
                        Order = FSharpGeneratedSourceOrder.EndOfProject
                    }
                    {
                        HintName = "GeneratedImpl"
                        FileName = implPath
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.EndOfProject
                    }
                ]
            )

        let diagnostics, runResult, compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        for d in diagnostics do
            printfn "%A" d

        Assert.True(compileException.IsNone, sprintf "Compile threw: %A" compileException)
        Assert.Empty(diagnostics |> Seq.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error))

        // The signature file must precede the implementation file in the ordered list.
        let ordered = runResult.OrderedSourceFiles
        let sigIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(sigPath, StringComparison.OrdinalIgnoreCase))
        let implIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(implPath, StringComparison.OrdinalIgnoreCase))

        Assert.True(sigIdx.IsSome && implIdx.IsSome, "Generated sig and impl must both be present in ordered source list")
        Assert.True(sigIdx.Value < implIdx.Value, "Generated .fsi must precede its .fs")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``CompileWithSourceGenerators_BeforeFileOrderingWorks`` () =
    let dir = tempDir ()
    try
        let firstFile = writeSourceFile dir "First.fs" "module First\nlet A = 1\n"
        let secondFile = writeSourceFile dir "Second.fs" "module Second\nlet B = First.A\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ firstFile; secondFile ]

        let generatedPath = Path.Combine(dir, "Inserted.fs")
        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "InsertedGenerator"
                        FileName = generatedPath
                        SourceText = "module Inserted\nlet Value = 99\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile secondFile
                    }
                ]
            )

        let _diagnostics, runResult, _compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        let ordered = runResult.OrderedSourceFiles
        let insertedIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(generatedPath, StringComparison.OrdinalIgnoreCase))
        let secondIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(secondFile, StringComparison.OrdinalIgnoreCase))

        Assert.True(insertedIdx.IsSome, "Inserted generated file must be present in ordered list")
        Assert.True(secondIdx.IsSome, "Second file must be present in ordered list")
        Assert.True(insertedIdx.Value < secondIdx.Value, "Generated file ordered BeforeFile must precede its anchor")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``CompileWithSourceGenerators_AfterFileOrderingWorks`` () =
    let dir = tempDir ()
    try
        let firstFile = writeSourceFile dir "First.fs" "module First\nlet A = 1\n"
        let secondFile = writeSourceFile dir "Second.fs" "module Second\nlet B = First.A\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ firstFile; secondFile ]

        let generatedPath = Path.Combine(dir, "AfterFirst.fs")
        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "AfterFirstGenerator"
                        FileName = generatedPath
                        SourceText = "module AfterFirst\nlet Value = 7\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.AfterFile firstFile
                    }
                ]
            )

        let _diagnostics, runResult, _compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        let ordered = runResult.OrderedSourceFiles
        let generatedIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(generatedPath, StringComparison.OrdinalIgnoreCase))
        let firstIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(firstFile, StringComparison.OrdinalIgnoreCase))
        let secondIdx = ordered |> List.tryFindIndex (fun p -> p.Equals(secondFile, StringComparison.OrdinalIgnoreCase))

        Assert.True(generatedIdx.IsSome && firstIdx.IsSome && secondIdx.IsSome, "All files must be present in ordered list")
        Assert.True(firstIdx.Value < generatedIdx.Value, "Generated file ordered AfterFile must follow its anchor")
        Assert.True(generatedIdx.Value < secondIdx.Value, "Generated file should be placed between its anchor and the next file")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``CompileWithSourceGenerators_DuplicateHintNameFails`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ sourceFile ]

        let generatedPath = Path.Combine(dir, "Dup.fs")
        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "Duplicate"
                        FileName = generatedPath
                        SourceText = "module Dup\nlet X = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.EndOfProject
                    }
                    {
                        HintName = "Duplicate"
                        FileName = generatedPath
                        SourceText = "module Dup\nlet Y = 2\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.EndOfProject
                    }
                ]
            )

        let diagnostics, _runResult, _compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        // The ordering engine reports an error diagnostic for the duplicate HintName.
        let errors =
            diagnostics
            |> Seq.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

        Assert.True(errors |> Seq.length > 0, "Expected an error diagnostic for the duplicate HintName")
        Assert.Contains(errors, fun d -> d.Message.Contains("Duplicate"))
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``ParseAndCheckProjectWithSourceGenerators_SeesGeneratedFile`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet value = Generated.Greeting\n"
        let projFile = Path.Combine(dir, "Program.fsproj")
        let dllName = Path.Combine(dir, "Program.dll")
        let args = mkProjectCommandLineArgsSilent (dllName, [])
        let options = checker.GetProjectOptionsFromCommandLineArgs(projFile, Array.append [| "fsc.exe" |] args)
        let options = { options with SourceFiles = [| sourceFile |] }

        let generatedPath = Path.Combine(dir, "Generated.fs")
        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GreetingGenerator"
                        FileName = generatedPath
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let checkResults, runResult =
            checker.ParseAndCheckProjectWithSourceGenerators(options, [ generator ], defaultGeneratorOptions dir)
            |> Async.RunSynchronously

        Assert.True(checkResults.HasCriticalErrors |> not, sprintf "Project check had critical errors: %A" checkResults.Diagnostics)

        // The generated file must be part of the updated, ordered source list.
        Assert.Contains(
            runResult.OrderedSourceFiles,
            fun p -> p.Equals(generatedPath, StringComparison.OrdinalIgnoreCase)
        )
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

// ============================================================================
// Caveat 1 tests — in-memory store / FileSystem overlay (disk not required)
// ============================================================================

/// A generator whose output is driven by an additional file's content. Used to
/// test that generated-content changes (driven by an input change) invalidate.
type AdditionalFileValueGenerator(programFile: string, additionalFileName: string) =
    interface IFSharpSourceGenerator with
        member _.Generate(ctx) =
            let content =
                match ctx.AdditionalFiles.TryFind additionalFileName with
                | Some c -> c.Trim()
                | None -> "0"

            {
                GeneratedSources =
                    [
                        {
                            HintName = "AddFileValue"
                            FileName = "Generated.fs"
                            SourceText = sprintf "module Generated\nlet Value = %s\n" content
                            Kind = FSharpGeneratedSourceKind.Implementation
                            Order = FSharpGeneratedSourceOrder.BeforeFile programFile
                        }
                    ]
                Diagnostics = []
            }

let private buildProjectOptions (checker: FSharpChecker) dir sourceFile =
    let projFile = Path.Combine(dir, "Program.fsproj")
    let dllName = Path.Combine(dir, "Program.dll")
    let args = mkProjectCommandLineArgsSilent (dllName, [])
    let options = checker.GetProjectOptionsFromCommandLineArgs(projFile, Array.append [| "fsc.exe" |] args)
    { options with SourceFiles = [| sourceFile |] }

let private noEmitOptions outputDir =
    { defaultGeneratorOptions outputDir with EmitGeneratedFiles = false }

[<Fact>]
let ``CompileWithSourceGenerators_EmitFalseCompilesFromMemory`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet value = Generated.Greeting\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ sourceFile ]

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GreetingGenerator"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let diagnostics, runResult, compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], noEmitOptions dir)
            |> Async.RunSynchronously

        for d in diagnostics do
            printfn "%A" d

        Assert.True(compileException.IsNone, sprintf "Compile threw: %A" compileException)
        Assert.Empty(diagnostics |> Seq.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error))
        Assert.True(File.Exists dllPath, "Expected the output dll to be produced")

        // The generated file must NOT be on disk — proving the in-memory overlay,
        // not the disk, fed the compiler.
        let generatedOnDisk =
            runResult.OrderedSourceFiles
            |> List.exists (fun p -> p.EndsWith("Generated.fs", StringComparison.OrdinalIgnoreCase) && File.Exists(p))

        Assert.False(generatedOnDisk, "Generated file should not exist on disk when EmitGeneratedFiles=false")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``ParseAndCheckProjectWithSourceGenerators_ResolvesSymbolFromMemory`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet value = Generated.Greeting\n"
        let options = buildProjectOptions checker dir sourceFile

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GreetingGenerator"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let checkResults, runResult =
            checker.ParseAndCheckProjectWithSourceGenerators(options, [ generator ], noEmitOptions dir)
            |> Async.RunSynchronously

        // No critical errors means the generated symbol was resolved (otherwise
        // Program.fs's reference to Generated.Greeting would be undefined).
        Assert.True(checkResults.HasCriticalErrors |> not, sprintf "Project check had critical errors: %A" checkResults.Diagnostics)

        // And the generated file was never written to disk.
        let generatedOnDisk =
            runResult.OrderedSourceFiles
            |> List.exists (fun p -> p.EndsWith("Generated.fs", StringComparison.OrdinalIgnoreCase) && File.Exists(p))

        Assert.False(generatedOnDisk, "Generated file should not exist on disk when EmitGeneratedFiles=false")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``CompileWithSourceGenerators_ReadOnlyOutputDirectoryEmitsWarningButCompiles`` () =
    let dir = tempDir ()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet value = Generated.Greeting\n"
        let dllPath = Path.Combine(dir, "Program.dll")
        let argv = buildLibraryArgv dllPath [ sourceFile ]

        // Make the output directory un-writable by occupying its path with a file:
        // the driver will try to create <blocker>/<hint>/Generated.fs and fail.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "x")
        let outputDir = Path.Combine(blocker, "sub")

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "GreetingGenerator"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Greeting = 42\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let opts = { defaultGeneratorOptions outputDir with EmitGeneratedFiles = true }

        let diagnostics, _runResult, compileException =
            checker.CompileWithSourceGenerators(argv, [ generator ], opts)
            |> Async.RunSynchronously

        // The build must still succeed via the in-memory overlay.
        Assert.True(compileException.IsNone, sprintf "Compile threw: %A" compileException)
        Assert.True(File.Exists dllPath, "Expected the output dll to be produced despite read-only output dir")

        // And a best-effort disk-write warning must have been reported.
        let runWarnings =
            diagnostics
            |> Seq.filter (fun d -> d.Severity <> FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

        Assert.True(
            runWarnings |> Seq.exists (fun d -> d.Message.Contains("FSGEN_DiskWriteFailed") || d.Message.Contains("Failed to write generated file")),
            sprintf "Expected an FSGEN_DiskWriteFailed warning, got: %A" diagnostics
        )
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

// ============================================================================
// Caveat 2 tests — incremental invalidation / run cache
// ============================================================================

let private runAndUpdate (checker: FSharpChecker) options generator genOpts =
    checker.RunSourceGeneratorsAndUpdateProject(options, generator, genOpts)
    |> Async.RunSynchronously

[<Fact>]
let ``RunCache_SecondIdenticalCallIsCacheHit`` () =
    let dir = tempDir ()
    let c = FSharpChecker.Create()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet x = Generated.Value\n"
        let options = buildProjectOptions c dir sourceFile
        let genOpts = noEmitOptions dir

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "FixedValue"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Value = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let updated1, result1 = runAndUpdate c options [ generator ] genOpts
        let updated2, result2 = runAndUpdate c options [ generator ] genOpts

        Assert.False(result1.CacheHit, "First run should not be a cache hit")
        Assert.True(result2.CacheHit, "Second identical run should be a cache hit")
        Assert.Equal(updated1.Stamp, updated2.Stamp)
        Assert.Equal<string>(result1.OrderedSourceFiles |> List.toArray, result2.OrderedSourceFiles |> List.toArray)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``RunCache_AdditionalFileContentChangeInvalidates`` () =
    let dir = tempDir ()
    let c = FSharpChecker.Create()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet x = Generated.Value\n"
        let options = buildProjectOptions c dir sourceFile
        let addFile = writeSourceFile dir "schema.json" "1"
        let genOpts = { noEmitOptions dir with AdditionalFiles = [ addFile ] }
        let generator = AdditionalFileValueGenerator(sourceFile, addFile)

        let updated1, result1 = runAndUpdate c options [ generator ] genOpts
        Assert.False(result1.CacheHit)

        // Change the additional file's content. The run-cache key and the project
        // Stamp both incorporate additional-file content, so this must invalidate.
        File.WriteAllText(addFile, "2")

        let updated2, result2 = runAndUpdate c options [ generator ] genOpts

        Assert.False(result2.CacheHit, "Additional-file content change must invalidate the run cache")
        Assert.NotEqual(updated1.Stamp, updated2.Stamp)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``RunCache_GeneratorSetChangeInvalidates`` () =
    let dir = tempDir ()
    let c = FSharpChecker.Create()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet x = Generated.Value\n"
        let options = buildProjectOptions c dir sourceFile
        let genOpts = noEmitOptions dir

        let gen =
            CapturedGenerator(
                [
                    {
                        HintName = "FixedValue"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Value = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let gen2 =
            CapturedGenerator(
                [
                    {
                        HintName = "ExtraValue"
                        FileName = "Extra.fs"
                        SourceText = "module Extra\nlet E = 0\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let _u1, result1 = runAndUpdate c options [ gen ] genOpts
        Assert.False(result1.CacheHit)

        let updated2, result2 = runAndUpdate c options [ gen; gen2 ] genOpts

        Assert.False(result2.CacheHit, "Changing the generator set must invalidate the run cache")
        Assert.NotEqual(_u1.Stamp, updated2.Stamp)
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``RunCache_ClearCachesFlushesRunCache`` () =
    let dir = tempDir ()
    let c = FSharpChecker.Create()
    try
        let sourceFile = writeSourceFile dir "Program.fs" "module Program\nlet x = Generated.Value\n"
        let options = buildProjectOptions c dir sourceFile
        let genOpts = noEmitOptions dir

        let generator =
            CapturedGenerator(
                [
                    {
                        HintName = "FixedValue"
                        FileName = "Generated.fs"
                        SourceText = "module Generated\nlet Value = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceFile
                    }
                ]
            )

        let _u1, result1 = runAndUpdate c options [ generator ] genOpts
        Assert.False(result1.CacheHit)

        c.InvalidateAll()

        let _u2, result2 = runAndUpdate c options [ generator ] genOpts
        Assert.False(result2.CacheHit, "InvalidateAll must flush the run cache so the next run re-runs")
    finally
        if Directory.Exists(dir) then Directory.Delete(dir, true)

[<Fact>]
let ``RunCache_ConcurrentProjectsDoNotCrossContaminate`` () =
    let dirA = tempDir ()
    let dirB = tempDir ()
    let c = FSharpChecker.Create()
    try
        let sourceA = writeSourceFile dirA "Program.fs" "module Program\nlet x = GeneratedA.Value\n"
        let sourceB = writeSourceFile dirB "Program.fs" "module Program\nlet x = GeneratedB.Value\n"
        let optionsA = buildProjectOptions c dirA sourceA
        let optionsB = buildProjectOptions c dirB sourceB
        let genOptsA = noEmitOptions dirA
        let genOptsB = noEmitOptions dirB

        let genA =
            CapturedGenerator(
                [
                    {
                        HintName = "GenA"
                        FileName = "GeneratedA.fs"
                        SourceText = "module GeneratedA\nlet Value = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceA
                    }
                ]
            )

        let genB =
            CapturedGenerator(
                [
                    {
                        HintName = "GenB"
                        FileName = "GeneratedB.fs"
                        SourceText = "module GeneratedB\nlet Value = 1\n"
                        Kind = FSharpGeneratedSourceKind.Implementation
                        Order = FSharpGeneratedSourceOrder.BeforeFile sourceB
                    }
                ]
            )

        let workA =
            async {
                let cr, _ = c.ParseAndCheckProjectWithSourceGenerators(optionsA, [ genA ], genOptsA) |> Async.RunSynchronously
                return cr
            }

        let workB =
            async {
                let cr, _ = c.ParseAndCheckProjectWithSourceGenerators(optionsB, [ genB ], genOptsB) |> Async.RunSynchronously
                return cr
            }

        let results = Async.Parallel [| workA; workB |] |> Async.RunSynchronously
        let crA = results.[0]
        let crB = results.[1]

        // Each project must resolve its OWN generated symbol (no cross-contamination).
        Assert.True(crA.HasCriticalErrors |> not, sprintf "Project A had errors: %A" crA.Diagnostics)
        Assert.True(crB.HasCriticalErrors |> not, sprintf "Project B had errors: %A" crB.Diagnostics)
    finally
        if Directory.Exists(dirA) then Directory.Delete(dirA, true)
        if Directory.Exists(dirB) then Directory.Delete(dirB, true)
