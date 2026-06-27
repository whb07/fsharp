Yes. The clean minimal fork is:

```text
Patch FSharp.Compiler.Service
Expose a library-first source-generation API
Patch the shared compiler driver only enough to accept an optional source-generation hook
Do not make users call fsc.exe
Do not make users shell out
```

The reason this works: `fsc.exe` is not the only path into the compiler. `FSharpChecker.Compile` already compiles in-process from command-line arguments and returns diagnostics plus an optional terminating exception.  In `service.fs`, `FSharpChecker.Compile` calls `CompileHelpers.compileFromArgs`, which calls the same internal compiler driver path used by the command-line compiler.

So the best minimal patch is **not** “modify `fsc.exe`.” It is:

```text
Add source-generation support to FSharp.Compiler.Service.dll
Make FSharpChecker expose CompileWithSourceGenerators(...)
Optionally route that through the same Driver.main1 pipeline
```

---

# Recommended minimal design

## Public usage you want

After the fork, consuming code should look like this:

```fsharp
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceGeneration

let checker = FSharpChecker.Create()

let generators : IFSharpSourceGenerator list =
    [
        MyGenerator() :> IFSharpSourceGenerator
    ]

let generatorOptions =
    {
        OutputDirectory = "obj/Generated/FSharp"
        EmitGeneratedFiles = true
        AdditionalFiles = [ "schema.json" ]
        AnalyzerConfigFiles = []
        MaxPasses = 1
    }

let argv =
    [|
        "fsc.exe"
        "-o:bin/Debug/net8.0/MyApp.dll"
        "--target:library"
        "--noframework"
        "-r:/path/to/FSharp.Core.dll"
        "File1.fs"
        "File2.fs"
    |]

let! diagnostics, generatorResult, compileException =
    checker.CompileWithSourceGenerators(
        argv,
        generators,
        generatorOptions
    )
```

No `fsc.exe` process. No shelling out. You reference your forked `FSharp.Compiler.Service` package/project and call it directly.

---

# The minimal patch shape

You need three layers:

```text
1. SourceGeneration abstractions
2. FCS-facing generator runner
3. Tiny compiler-driver hook
```

The driver hook should be optional, so the stock compiler path remains unchanged when no generators are supplied.

---

# 1. Add source-generation abstractions

Add:

```text
src/Compiler/SourceGeneration/FSharpSourceGeneratorTypes.fsi
src/Compiler/SourceGeneration/FSharpSourceGeneratorTypes.fs
src/Compiler/SourceGeneration/FSharpSourceGeneratorDriver.fsi
src/Compiler/SourceGeneration/FSharpSourceGeneratorDriver.fs
```

Place the low-level types early enough in `FSharp.Compiler.Service.fsproj` to be visible to `Driver/fsc.fs`. Put the FCS-rich driver implementation later, after `FSharpCheckerResults`, if it needs `FSharpProjectOptions` or `FSharpCheckProjectResults`.

The current project file compiles driver files before service files, and `FSharpCheckerResults` / `BackgroundCompiler` / `service.fs` appear later in the service section.  So split the source-generation code into:

```text
SourceGeneration/CompilerVisibleSourceGenerationTypes.fs
    must not depend on FSharpProjectOptions

SourceGeneration/FSharpSourceGeneratorDriver.fs
    can depend on FSharpProjectOptions and FSharpCheckProjectResults
```

## Public minimal API

```fsharp
namespace FSharp.Compiler.SourceGeneration

open System
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

[<RequireQualifiedAccess>]
type FSharpGeneratedSourceOrder =
    | BeforeFile of targetFilePath: string
    | AfterFile of targetFilePath: string
    | EndOfProject
    | BeforeImplementation of implementationFilePath: string

[<RequireQualifiedAccess>]
type FSharpGeneratedSourceKind =
    | Implementation
    | Signature

[<NoComparison>]
type FSharpGeneratedSource =
    {
        HintName: string
        FileName: string
        SourceText: string
        Kind: FSharpGeneratedSourceKind
        Order: FSharpGeneratedSourceOrder
    }

[<NoComparison>]
type FSharpSourceGeneratorDiagnostic =
    {
        Id: string
        Message: string
        Severity: FSharpDiagnosticSeverity
        Range: range option
    }

[<NoComparison>]
type FSharpSourceGeneratorOptions =
    {
        OutputDirectory: string
        EmitGeneratedFiles: bool
        AdditionalFiles: string list
        AnalyzerConfigFiles: string list
        MaxPasses: int
    }

[<NoComparison>]
type FSharpSourceGeneratorContext =
    {
        ProjectFileName: string option
        ProjectDirectory: string
        SourceFiles: string list
        OtherOptions: string list
        References: string list
        DefineConstants: string list
        OutputFile: string option
        AssemblyName: string option
        AdditionalFiles: Map<string, string>
        CancellationToken: CancellationToken
    }

[<NoComparison>]
type FSharpSourceGeneratorOutput =
    {
        GeneratedSources: FSharpGeneratedSource list
        Diagnostics: FSharpSourceGeneratorDiagnostic list
    }

type IFSharpSourceGenerator =
    abstract Generate:
        context: FSharpSourceGeneratorContext ->
            FSharpSourceGeneratorOutput
```

This is intentionally simpler than the full Roslyn-like incremental model. Get the compiler hook working first. Then replace `IFSharpSourceGenerator` with `IFSharpIncrementalGenerator` later.

---

# 2. Add a library-first FCS entry point

Add to `src/Compiler/Service/service.fsi`:

```fsharp
namespace FSharp.Compiler.CodeAnalysis

open FSharp.Compiler.Diagnostics
open FSharp.Compiler.SourceGeneration

type FSharpChecker with

    /// Compile in-process after running F# source generators.
    member CompileWithSourceGenerators:
        argv: string[] *
        generators: IFSharpSourceGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions *
        ?userOpName: string ->
            Async<FSharpDiagnostic[] * FSharpSourceGeneratorRunResult * exn option>

    /// Run source generators and return a new source file order without compiling.
    member RunSourceGenerators:
        argv: string[] *
        generators: IFSharpSourceGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions *
        ?userOpName: string ->
            Async<FSharpSourceGeneratorRunResult>
```

Add a run-result type:

```fsharp
namespace FSharp.Compiler.SourceGeneration

open System

[<NoComparison>]
type FSharpSourceGeneratorRunResult =
    {
        GeneratedSources: FSharpGeneratedSource list
        OrderedSourceFiles: string list
        Diagnostics: FSharpSourceGeneratorDiagnostic list
        ElapsedTime: TimeSpan
    }
```

Then in `service.fs`, implement:

```fsharp
member _.CompileWithSourceGenerators(argv, generators, generatorOptions, ?userOpName) =
    let userOpName = defaultArg userOpName "CompileWithSourceGenerators"

    async {
        let ctok = CompilationThreadToken()

        let hook =
            FSharp.Compiler.Driver.SourceGenerationHook(fun request ->
                FSharpSourceGeneratorDriver.runFromCompilerRequest
                    request
                    generators
                    generatorOptions)

        let diagnostics, compileException =
            CompileHelpers.compileFromArgsWithSourceGenerationHook(
                ctok,
                argv,
                legacyReferenceResolver,
                None,
                None,
                Some hook
            )

        let generatorResult =
            hook.LastRunResult
            |> Option.defaultValue FSharpSourceGeneratorRunResult.Empty

        return diagnostics, generatorResult, compileException
    }
```

That requires one small addition to `CompileHelpers`.

Current `compileFromArgs` calls `CompileFromCommandLineArguments(...)` directly.  Add a sibling:

```fsharp
module CompileHelpers =

    let compileFromArgsWithSourceGenerationHook
        (
            ctok,
            argv: string[],
            legacyReferenceResolver,
            tcImportsCapture,
            dynamicAssemblyCreator,
            sourceGenerationHook
        ) =

        let diagnostics, diagnosticsLogger, loggerProvider =
            mkCompilationDiagnosticsHandlers (argv |> Array.contains "--flaterrors")

        let result =
            tryCompile diagnosticsLogger (fun exiter ->
                CompileFromCommandLineArguments(
                    ctok,
                    argv,
                    legacyReferenceResolver,
                    true,
                    ReduceMemoryFlag.Yes,
                    CopyFSharpCoreFlag.No,
                    exiter,
                    loggerProvider,
                    tcImportsCapture,
                    dynamicAssemblyCreator,
                    sourceGenerationHook
                ))

        diagnostics.ToArray(), result
```

Keep the existing `compileFromArgs` as:

```fsharp
let compileFromArgs (ctok, argv, legacyReferenceResolver, tcImportsCapture, dynamicAssemblyCreator) =
    compileFromArgsWithSourceGenerationHook(
        ctok,
        argv,
        legacyReferenceResolver,
        tcImportsCapture,
        dynamicAssemblyCreator,
        None
    )
```

That preserves existing behavior.

---

# 3. Add one optional hook to the shared compiler driver

In `src/Compiler/Driver/fsc.fs`, add an internal hook type near the driver argument types:

```fsharp
[<NoComparison>]
type SourceGenerationCompilerRequest =
    {
        Ctok: CompilationThreadToken
        TcConfigBuilder: TcConfigBuilder
        TcConfig: TcConfig
        OriginalSourceFiles: string list
        OutputFile: string option
        AssemblyName: string
        ImplicitIncludeDir: string
        CancellationToken: CancellationToken
    }

[<NoComparison>]
type SourceGenerationCompilerResponse =
    {
        OrderedSourceFiles: string list
    }

type SourceGenerationHook =
    abstract Run:
        request: SourceGenerationCompilerRequest ->
            SourceGenerationCompilerResponse
```

Then change `main1` to accept an optional hook:

```fsharp
let main1
    (
        ctok,
        argv,
        legacyReferenceResolver,
        bannerAlreadyPrinted,
        reduceMemoryUsage: ReduceMemoryFlag,
        defaultCopyFSharpCore: CopyFSharpCoreFlag,
        exiter: Exiter,
        diagnosticsLoggerProvider: IDiagnosticsLoggerProvider,
        sourceGenerationHook: SourceGenerationHook option,
        disposables: DisposablesTracker
    ) =
    ...
```

Then change `CompileFromCommandLineArguments` to accept and pass the hook:

```fsharp
let CompileFromCommandLineArguments
    (
        ctok,
        argv,
        legacyReferenceResolver,
        bannerAlreadyPrinted,
        reduceMemoryUsage,
        defaultCopyFSharpCore,
        exiter: Exiter,
        loggerProvider,
        tcImportsCapture,
        dynamicAssemblyCreator,
        sourceGenerationHook: SourceGenerationHook option
    ) =
    use disposables = new DisposablesTracker()

    main1 (
        ctok,
        argv,
        legacyReferenceResolver,
        bannerAlreadyPrinted,
        reduceMemoryUsage,
        defaultCopyFSharpCore,
        exiter,
        loggerProvider,
        sourceGenerationHook,
        disposables
    )
    |> main2
    |> main3
    |> main4 (tcImportsCapture, dynamicAssemblyCreator)
    |> main5
    |> main6 dynamicAssemblyCreator
```

The current compiler driver’s main entry point is `CompileFromCommandLineArguments`, which calls `main1 |> main2 |> ... |> main6`.  You are only adding one optional parameter and leaving the rest of the pipeline unchanged.

---

# 4. Exact hook position inside `main1`

Put the source-generation hook **after output names are decided** but **before source files are converted into compiler source documents and parsed**.

Why?

The current `main1`:

1. Processes command-line flags and source files.
2. Decides output file / PDB / assembly name.
3. Creates `TcConfig`.
4. Resolves imports.
5. Builds `ilSourceDocs` from `sourceFiles`.
6. Parses source files.
7. Typechecks inputs.

You want generated files to participate in parsing/typechecking, debug docs, and later emit. But you **do not** want generated files to change default output naming. Therefore:

```text
Process command line
Decide output names using original source files
Create TcConfig
Resolve references
Run source generation
Replace sourceFiles with generated ordered sourceFiles
Build ilSourceDocs
ParseInputFiles
TypeCheck
```

Patch location conceptually:

```fsharp
// Existing:
let outfile, pdbfile, assemblyName =
    try
        tcConfigB.DecideNames sourceFiles
    with e ->
        ...

let tcConfig =
    try
        TcConfig.Create(tcConfigB, validate = false)
    with e ->
        ...

// Resolve assemblies/imports as today.

// NEW:
let sourceFiles =
    match sourceGenerationHook with
    | None ->
        sourceFiles

    | Some hook ->
        let response =
            hook.Run {
                Ctok = ctok
                TcConfigBuilder = tcConfigB
                TcConfig = tcConfig
                OriginalSourceFiles = sourceFiles
                OutputFile = outfile
                AssemblyName = assemblyName
                ImplicitIncludeDir = tcConfigB.implicitIncludeDir
                CancellationToken = CancellationToken.None
            }

        response.OrderedSourceFiles

// Existing should now use updated sourceFiles:
let ilSourceDocs =
    [
        for sourceFile in sourceFiles ->
            tcGlobals.memoize_file (FileIndex.fileIndexOfFile sourceFile)
    ]

let inputs =
    ParseInputFiles(tcConfig, lexResourceManager, sourceFiles, diagnosticsLogger, false)
```

That is the smallest real compiler-pipeline patch.

---

# 5. The generator driver should write real files

Do not initially try to feed generated source as in-memory text. The F# compiler pipeline is very file-path oriented, and existing parse/check paths assume file names and filesystem access in many places. FCS parse/check APIs also document that project checking reads files through the file system.

So for MVP:

```text
Generator returns source text
Driver writes it to obj/Generated/FSharp/...
Driver returns generated file paths
Compiler sees those paths as normal source files
```

Generated path example:

```text
obj/Generated/FSharp/
  MyGeneratorAssembly/
    MyGenerator.TypeName/
      GeneratedModels.fsi
      GeneratedModels.fs
```

---

# 6. Ordering engine

Your source generator driver must return a final ordered source list:

```fsharp
val orderSources:
    originalSourceFiles: string list ->
    generatedSources: FSharpGeneratedSource list ->
        string list * FSharpSourceGeneratorDiagnostic list
```

Minimal rules:

```text
Generated .fs defaults to EndOfProject.
Generated .fsi must be before matching .fs.
BeforeFile/AfterFile anchors must exist.
Cycles are errors.
Duplicate HintName is an error.
```

If ordering succeeds:

```fsharp
{
    OrderedSourceFiles =
        [
            "A.fs"
            "obj/Generated/FSharp/MyGen/Generated.fsi"
            "obj/Generated/FSharp/MyGen/Generated.fs"
            "B.fs"
        ]
}
```

The driver hook gives this ordered list back to `main1`.

---

# 7. Keep `FSharpProjectOptions` unchanged

Do **not** add fields to `FSharpProjectOptions` for the minimal patch. It is a public record containing `ProjectFileName`, `ProjectId`, `SourceFiles`, `OtherOptions`, references, flags, load time, unresolved references, and stamp.   Adding a field is high-churn and likely breaks consumers who construct the record.

Instead, add explicit APIs:

```fsharp
checker.CompileWithSourceGenerators(...)
checker.RunSourceGenerators(...)
checker.ParseAndCheckProjectWithSourceGenerators(...)
```

Later, if you want transparent IDE/project behavior, you can encode generator config in `OtherOptions` or add a separate wrapper type.

---

# 8. Add FCS parse/check API too

For editor/IDE/testing, also add:

```fsharp
type FSharpChecker with

    member RunSourceGeneratorsAndUpdateProject:
        options: FSharpProjectOptions *
        generators: IFSharpSourceGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions *
        ?userOpName: string ->
            Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult>

    member ParseAndCheckProjectWithSourceGenerators:
        options: FSharpProjectOptions *
        generators: IFSharpSourceGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions *
        ?userOpName: string ->
            Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult>
```

Implementation shape:

```fsharp
member checker.RunSourceGeneratorsAndUpdateProject(options, generators, generatorOptions, ?userOpName) =
    async {
        let userOpName = defaultArg userOpName "RunSourceGeneratorsAndUpdateProject"

        let! result =
            FSharpSourceGeneratorDriver.runFromProjectOptions
                checker
                options
                generators
                generatorOptions
                userOpName

        let updatedOptions =
            { options with SourceFiles = result.OrderedSourceFiles |> List.toArray }

        return updatedOptions, result
    }

member checker.ParseAndCheckProjectWithSourceGenerators(options, generators, generatorOptions, ?userOpName) =
    async {
        let! updatedOptions, genResult =
            checker.RunSourceGeneratorsAndUpdateProject(
                options,
                generators,
                generatorOptions,
                ?userOpName = userOpName
            )

        let! checkResults =
            checker.ParseAndCheckProject(updatedOptions, ?userOpName = userOpName)

        return checkResults, genResult
    }
```

This uses the existing `ParseAndCheckProject` API, which typechecks a whole project from `FSharpProjectOptions`.

---

# 9. Minimal driver implementation

```fsharp
namespace FSharp.Compiler.SourceGeneration

open System
open System.Diagnostics
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

module FSharpSourceGeneratorDriver =

    let private readAdditionalFile path =
        path, File.ReadAllText(path)

    let private writeGeneratedSource (outputRoot: string) (source: FSharpGeneratedSource) =
        Directory.CreateDirectory(Path.GetDirectoryName(source.FileName)) |> ignore
        File.WriteAllText(source.FileName, source.SourceText)
        source.FileName

    let run
        (context: FSharpSourceGeneratorContext)
        (generators: IFSharpSourceGenerator list)
        (options: FSharpSourceGeneratorOptions)
        : FSharpSourceGeneratorRunResult =

        let sw = Stopwatch.StartNew()

        let outputs =
            generators
            |> List.collect (fun generator ->
                let output = generator.Generate context
                output.GeneratedSources)

        for source in outputs do
            writeGeneratedSource options.OutputDirectory source |> ignore

        let ordered, orderingDiagnostics =
            FSharpGeneratedSourceOrdering.orderSources
                context.SourceFiles
                outputs

        sw.Stop()

        {
            GeneratedSources = outputs
            OrderedSourceFiles = ordered
            Diagnostics = orderingDiagnostics
            ElapsedTime = sw.Elapsed
        }

    let runFromProjectOptions
        (checker: FSharpChecker)
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        (userOpName: string)
        : Async<FSharpSourceGeneratorRunResult> =

        async {
            let additionalFiles =
                generatorOptions.AdditionalFiles
                |> List.map readAdditionalFile
                |> Map.ofList

            let context =
                {
                    ProjectFileName = Some options.ProjectFileName
                    ProjectDirectory = options.ProjectDirectory
                    SourceFiles = options.SourceFiles |> Array.toList
                    OtherOptions = options.OtherOptions |> Array.toList
                    References =
                        options.OtherOptions
                        |> Array.toList
                        |> List.filter (fun x -> x.StartsWith("-r:") || x.StartsWith("--reference:"))
                    DefineConstants =
                        options.OtherOptions
                        |> Array.toList
                        |> List.choose (fun x ->
                            if x.StartsWith("--define:") then
                                Some(x.Substring("--define:".Length))
                            else
                                None)
                    OutputFile = None
                    AssemblyName = None
                    AdditionalFiles = additionalFiles
                    CancellationToken = Threading.CancellationToken.None
                }

            return run context generators generatorOptions
        }
```

For the compiler-driver hook path, add a sibling:

```fsharp
val runFromCompilerRequest:
    request: FSharp.Compiler.Driver.SourceGenerationCompilerRequest ->
    generators: IFSharpSourceGenerator list ->
    options: FSharpSourceGeneratorOptions ->
        FSharp.Compiler.Driver.SourceGenerationCompilerResponse
```

---

# 10. Typed generators: do this in the FCS layer, not in `main1`

If a generator needs typed project info, do **not** try to get that from the first `main1` hook. At that point, the compiler has not yet parsed/typechecked the project.

For typed generation, use the FCS-facing API:

```text
Original FSharpProjectOptions
   ↓
checker.ParseAndCheckProject(originalOptions)
   ↓
generator receives FSharpCheckProjectResults
   ↓
write generated files
   ↓
updated FSharpProjectOptions
   ↓
checker.ParseAndCheckProject(updatedOptions)
```

Add a separate typed interface:

```fsharp
type FSharpTypedSourceGeneratorContext =
    {
        ProjectOptions: FSharpProjectOptions
        CheckResults: FSharpCheckProjectResults
        AdditionalFiles: Map<string, string>
        CancellationToken: CancellationToken
    }

type IFSharpTypedSourceGenerator =
    abstract Generate:
        context: FSharpTypedSourceGeneratorContext ->
            FSharpSourceGeneratorOutput
```

Then add:

```fsharp
member ParseAndCheckProjectWithTypedSourceGenerators:
    options: FSharpProjectOptions *
    generators: IFSharpTypedSourceGenerator list *
    generatorOptions: FSharpSourceGeneratorOptions *
    ?userOpName: string ->
        Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult>
```

This is a two-pass model. It is much easier than splicing typed generation into `main1`.

---

# 11. What about `fsc.exe`?

You do not need to use it.

The standalone compiler executable eventually calls the same driver pipeline:

```fsharp
CompileFromCommandLineArguments
    |> main1
    |> main2
    |> main3
    |> main4
    |> main5
    |> main6
```

But your application can call the in-process FCS API:

```fsharp
checker.CompileWithSourceGenerators(...)
```

That will go through the same compiler driver, not a child process.

If later you want the forked `fsc.exe` to support generators too, add command-line flags like:

```text
--source-generator:<path>
--source-generator-data:<path>
--generated-files-output:<dir>
```

Then have `CompileFromCommandLineArguments(..., sourceGenerationHook = None)` build a hook from those flags. But do not make that part of the first patch.

---

# 12. The smallest viable commit list

## Commit 1: Abstractions

Add:

```text
src/Compiler/SourceGeneration/FSharpSourceGeneratorTypes.fsi
src/Compiler/SourceGeneration/FSharpSourceGeneratorTypes.fs
```

Types:

```fsharp
IFSharpSourceGenerator
FSharpSourceGeneratorContext
FSharpSourceGeneratorOptions
FSharpGeneratedSource
FSharpGeneratedSourceOrder
FSharpSourceGeneratorOutput
FSharpSourceGeneratorRunResult
```

## Commit 2: Ordering and file writing

Add:

```text
src/Compiler/SourceGeneration/FSharpGeneratedSourceOrdering.fs
src/Compiler/SourceGeneration/FSharpSourceGeneratorDriver.fs
```

Implement:

```fsharp
orderSources
writeGeneratedSource
runFromProjectOptions
runFromCompilerRequest
```

## Commit 3: FCS APIs

Patch:

```text
src/Compiler/Service/service.fsi
src/Compiler/Service/service.fs
```

Add:

```fsharp
CompileWithSourceGenerators
RunSourceGeneratorsAndUpdateProject
ParseAndCheckProjectWithSourceGenerators
```

## Commit 4: Shared compiler-driver optional hook

Patch:

```text
src/Compiler/Driver/fsc.fs
```

Add:

```fsharp
SourceGenerationCompilerRequest
SourceGenerationCompilerResponse
SourceGenerationHook
```

Thread `sourceGenerationHook: SourceGenerationHook option` through:

```fsharp
CompileFromCommandLineArguments
main1
```

Run the hook after output names/config are decided and before `ilSourceDocs` / `ParseInputFiles`.

## Commit 5: Tests

Add tests for:

```text
CompileWithSourceGenerators_GeneratedFileCompiles
CompileWithSourceGenerators_GeneratedFsiBeforeFsCompiles
CompileWithSourceGenerators_BeforeFileOrderingWorks
CompileWithSourceGenerators_AfterFileOrderingWorks
CompileWithSourceGenerators_DuplicateHintNameFails
ParseAndCheckProjectWithSourceGenerators_SeesGeneratedFile
```

---

# 13. Important implementation choices

## Use explicit API overloads first

Prefer this:

```fsharp
checker.CompileWithSourceGenerators(argv, generators, options)
```

over this:

```fsharp
checker.Compile(argvWithMagicGeneratorFlags)
```

The explicit overload avoids command-line parsing and assembly loading complexity. You can pass generator instances directly.

## Do not add fields to `FSharpProjectOptions`

That public record is used widely. Use explicit methods or `OtherOptions` later.

## Write generated files to disk

This avoids touching the parser/file-system abstraction in the first patch.

## Patch the shared driver, not the executable

The executable and FCS compile path share the driver. Patch the driver once, call it through FCS.

## Keep source generation off by default

No behavior change unless a hook is supplied.

---

# 14. Final recommendation

Do the first fork patch like this:

```text
Public API:
    FSharpChecker.CompileWithSourceGenerators(...)
    FSharpChecker.ParseAndCheckProjectWithSourceGenerators(...)

Internal compiler change:
    fsc.fs accepts optional SourceGenerationHook
    main1 calls hook before ParseInputFiles

Generator model:
    simple IFSharpSourceGenerator first
    generated files written under obj/Generated/FSharp
    ordering engine returns final sourceFiles list

Consumption:
    reference forked FSharp.Compiler.Service
    call checker.CompileWithSourceGenerators
    never shell out to fsc.exe
```

That is the smallest patch that gives you a real compiler-integrated path while staying library-first.

