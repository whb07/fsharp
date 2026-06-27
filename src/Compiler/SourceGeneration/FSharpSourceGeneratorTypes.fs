namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Concurrent
open System.IO
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

/// An immutable, case-insensitive store of generated source text keyed by absolute
/// file path.
[<Sealed; NoComparison; NoEquality>]
type FSharpGeneratedSourceStore(entries: (string * string) seq) =

    // Normalize a path to a case-insensitive absolute key. Falls back to the raw
    // path if it cannot be canonicalized.
    let normalizePath (path: string) =
        if String.IsNullOrWhiteSpace path then
            path
        else
            try Path.GetFullPath(path)
            with _ ->
                path

    let map =
        let d = ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        for (p, t) in entries do
            d[normalizePath p] <- t
        d

    static member Empty = FSharpGeneratedSourceStore([])

    member _.TryGet(fileName: string) =
        match map.TryGetValue(normalizePath fileName) with
        | true, v -> Some v
        | _ -> None

    member _.Contains(fileName: string) = map.ContainsKey(normalizePath fileName)

    member _.ToOverlayMap() =
        map |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

[<NoComparison>]
type FSharpSourceGeneratorRunResult =
    {
        GeneratedSources: FSharpGeneratedSource list
        OrderedSourceFiles: string list
        Diagnostics: FSharpSourceGeneratorDiagnostic list
        ElapsedTime: TimeSpan
        Store: FSharpGeneratedSourceStore
        CacheHit: bool
    }

    static member Empty =
        {
            GeneratedSources = []
            OrderedSourceFiles = []
            Diagnostics = []
            ElapsedTime = TimeSpan.Zero
            Store = FSharpGeneratedSourceStore.Empty
            CacheHit = false
        }

type IFSharpSourceGenerator =
    abstract Generate:
        context: FSharpSourceGeneratorContext ->
            FSharpSourceGeneratorOutput

[<Interface>]
type IFSharpSourceGeneratorWithId =
    abstract GeneratorId: string
