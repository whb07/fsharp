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

        /// The file path that the generated source should be referenced by during compilation.
        /// May be relative, in which case the driver resolves it to a deterministic absolute
        /// path under the output directory.
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
        /// The directory under which generated source files are (optionally) written to disk
        /// and under which relative generated FileNames are resolved to absolute paths.
        OutputDirectory: string

        /// When true, generated files are additionally written to disk for human inspection /
        /// source-link / pdb embedding. When false, generators are still run and the compiler
        /// reads generated source through the in-memory store; disk output is never required
        /// for correctness.
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

/// An immutable, case-insensitive store of generated source text keyed by absolute
/// file path. Populated unconditionally for every generated source, regardless of
/// whether the generated files are also written to disk. The compiler reads
/// generated source through this store (via the FileSystem overlay), so disk
/// output is never required for correctness.
[<Sealed; NoComparison; NoEquality>]
type FSharpGeneratedSourceStore =
    /// Create a store from a sequence of (absolute-or-relative path, source text) pairs.
    /// Paths are normalized to absolute, case-insensitive keys.
    new: entries: (string * string) seq -> FSharpGeneratedSourceStore

    /// The empty store.
    static member Empty: FSharpGeneratedSourceStore

    /// Try to get the source text for the given path (path is normalized internally).
    member TryGet: fileName: string -> string option

    /// Whether the store contains the given path.
    member Contains: fileName: string -> bool

    /// A read-only view of the store as a map of normalized-path -> source text.
    member ToOverlayMap: unit -> Map<string, string>

/// The result of running a set of source generators against a project.
[<NoComparison>]
type FSharpSourceGeneratorRunResult =
    {
        /// All generated sources produced by the generators (with resolved absolute FileNames).
        GeneratedSources: FSharpGeneratedSource list

        /// The final, ordered list of source files (originals plus generated) that should be
        /// passed to the compiler. Always contains the logical (absolute) FileName, never
        /// depending on whether the file was written to disk.
        OrderedSourceFiles: string list

        /// Diagnostics produced by the generators and by the ordering engine.
        Diagnostics: FSharpSourceGeneratorDiagnostic list

        /// The time spent running the generators and ordering their outputs.
        ElapsedTime: TimeSpan

        /// The in-memory store of generated source text. The host holds this for the
        /// lifetime of a project check so the compiler can read generated source without
        /// requiring the files to exist on disk.
        Store: FSharpGeneratedSourceStore

        /// True if this result was served from the driver's run cache without re-running
        /// the generators.
        CacheHit: bool
    }

    static member Empty: FSharpSourceGeneratorRunResult

/// A simple, non-incremental F# source generator. Given the compilation
/// context, produce zero or more generated source files.
type IFSharpSourceGenerator =
    abstract Generate:
        context: FSharpSourceGeneratorContext ->
            FSharpSourceGeneratorOutput

/// Optional interface a generator can implement to supply a stable identifier used
/// to build deterministic generated file paths and cache identity. When not
/// implemented, the driver falls back to the generator's .NET type full name.
[<Interface>]
type IFSharpSourceGeneratorWithId =
    abstract GeneratorId: string
