namespace FSharp.Compiler.SourceGeneration

open System
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

/// Describes where a generated source file should be inserted into the
/// ordered source list fed to the compiler.
[<RequireQualifiedAccess>]
type FSharpGeneratedSourceOrder =
    /// Insert the generated file immediately before the given target source file.
    | BeforeFile of targetFilePath: string
    /// Insert the generated file immediately after the given target source file.
    | AfterFile of targetFilePath: string
    /// Append the generated file to the end of the project's source list.
    | EndOfProject
    /// Insert the generated file before the implementation file with the given path.
    | BeforeImplementation of implementationFilePath: string

/// Whether a generated file is an implementation (.fs) or a signature (.fsi).
[<RequireQualifiedAccess>]
type FSharpGeneratedSourceKind =
    | Implementation
    | Signature

/// A single unit of generated F# source produced by a source generator.
[<NoComparison>]
type FSharpGeneratedSource =
    {
        /// A short, human-readable, unique name identifying the generator that produced this
        /// source (and, where relevant, the logical item within it). Used for diagnostics and
        /// for de-duplicating outputs.
        HintName: string

        /// The absolute (or project-relative) file path that the generated source should be
        /// written to and referenced by during compilation.
        FileName: string

        /// The text of the generated source.
        SourceText: string

        /// Whether this generated file is an implementation or signature file.
        Kind: FSharpGeneratedSourceKind

        /// Where this generated file should be placed in the ordered source list.
        Order: FSharpGeneratedSourceOrder
    }

/// A diagnostic produced by a source generator.
[<NoComparison>]
type FSharpSourceGeneratorDiagnostic =
    {
        Id: string
        Message: string
        Severity: FSharpDiagnosticSeverity
        Range: range option
    }

/// Configuration supplied to source generators and the source-generation driver.
[<NoComparison>]
type FSharpSourceGeneratorOptions =
    {
        /// The directory under which generated source files are written.
        OutputDirectory: string

        /// When true, generated files are written to disk and included in the compilation.
        /// When false, generators are still run but no files are emitted.
        EmitGeneratedFiles: bool

        /// Additional, non-source files (e.g. schema.json) made available to generators via
        /// the generator context.
        AdditionalFiles: string list

        /// Paths to analyzer-config files (e.g. editorconfig) made available to generators.
        AnalyzerConfigFiles: string list

        /// The maximum number of source-generation passes to run. A pass may produce
        /// sources that reference types produced by an earlier pass.
        MaxPasses: int
    }

/// The information about the compilation that is made available to source generators.
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

        /// Additional files keyed by their path, with their contents as the value.
        AdditionalFiles: Map<string, string>
        CancellationToken: CancellationToken
    }

/// The output of a single source generator.
[<NoComparison>]
type FSharpSourceGeneratorOutput =
    {
        GeneratedSources: FSharpGeneratedSource list
        Diagnostics: FSharpSourceGeneratorDiagnostic list
    }

/// The result of running a set of source generators against a project.
[<NoComparison>]
type FSharpSourceGeneratorRunResult =
    {
        /// All generated sources produced by the generators.
        GeneratedSources: FSharpGeneratedSource list

        /// The final, ordered list of source files (originals plus generated) that should be
        /// passed to the compiler.
        OrderedSourceFiles: string list

        /// Diagnostics produced by the generators and by the ordering engine.
        Diagnostics: FSharpSourceGeneratorDiagnostic list

        /// The time spent running the generators and ordering their outputs.
        ElapsedTime: TimeSpan
    }

    static member Empty: FSharpSourceGeneratorRunResult

/// A simple, non-incremental F# source generator. Given the compilation
/// context, produce zero or more generated source files.
type IFSharpSourceGenerator =
    abstract Generate:
        context: FSharpSourceGeneratorContext ->
            FSharpSourceGeneratorOutput
