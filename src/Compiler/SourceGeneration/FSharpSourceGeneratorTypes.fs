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

[<NoComparison>]
type FSharpSourceGeneratorRunResult =
    {
        GeneratedSources: FSharpGeneratedSource list
        OrderedSourceFiles: string list
        Diagnostics: FSharpSourceGeneratorDiagnostic list
        ElapsedTime: TimeSpan
    }

    static member Empty =
        {
            GeneratedSources = []
            OrderedSourceFiles = []
            Diagnostics = []
            ElapsedTime = TimeSpan.Zero
        }

type IFSharpSourceGenerator =
    abstract Generate:
        context: FSharpSourceGeneratorContext ->
            FSharpSourceGeneratorOutput
