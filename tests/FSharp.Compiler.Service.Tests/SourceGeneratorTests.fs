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
