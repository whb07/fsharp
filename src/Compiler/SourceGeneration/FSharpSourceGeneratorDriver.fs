namespace FSharp.Compiler.SourceGeneration

open System
open System.Diagnostics
open System.IO
open System.Text
open FSharp.Compiler.CodeAnalysis
open Internal.Utilities.Collections
open Internal.Utilities.Library

/// The library-first source generator driver. Runs a set of source generators
/// against a compilation context, populates an in-memory store of generated
/// source (the source of truth, never requiring disk), optionally writes the
/// generated files to disk for inspection, and produces the final ordered source
/// list handed back to the compiler.
module internal FSharpSourceGeneratorDriver =

    // ---- diagnostics helpers -------------------------------------------------

    let private error id message =
        {
            Id = id
            Message = message
            Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
            Range = None
        }

    let private warning id message =
        {
            Id = id
            Message = message
            Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning
            Range = None
        }

    // ---- generator identity & path normalization -----------------------------

    let private generatorId (generator: IFSharpSourceGenerator) =
        match generator with
        | :? IFSharpSourceGeneratorWithId as wid -> wid.GeneratorId
        | _ ->
            // Type.FullName is annotated nullable; fall back to Name if null.
            match generator.GetType().FullName with
            | null -> generator.GetType().Name
            | name -> name

    /// Sanitize an arbitrary identifier into a path-safe segment.
    let private sanitizeForPath (s: string) =
        if String.IsNullOrWhiteSpace s then
            "Unknown"
        else
            let sb = StringBuilder(s.Length)
            for c in s do
                if Char.IsLetterOrDigit(c) || c = '_' || c = '-' || c = '.' then
                    sb.Append(c) |> ignore
                else
                    sb.Append('_') |> ignore
            let result = sb.ToString()
            if String.IsNullOrWhiteSpace(result) then "Unknown" else result

    /// Normalize a path to an absolute, case-insensitive key. Falls back to the
    /// raw path if it cannot be canonicalized.
    let internal normalizePath (path: string) =
        if String.IsNullOrWhiteSpace path then
            path
        else
            try Path.GetFullPath(path)
            with _ ->
                path

    /// Resolve a generated source's FileName to a deterministic absolute path.
    /// Relative paths are rooted under <outputDirectory>/<discriminator>/.
    /// Absolute paths are kept (canonicalized).
    let private resolveFileName (discriminator: string) (outputDirectory: string) (source: FSharpGeneratedSource) =
        let fn = source.FileName
        let resolved =
            if String.IsNullOrWhiteSpace(fn) then
                let ext = if source.Kind = FSharpGeneratedSourceKind.Signature then ".fsi" else ".fs"
                normalizePath (Path.Combine(outputDirectory, sanitizeForPath discriminator, sanitizeForPath(source.HintName) + ext))
            elif Path.IsPathRooted(fn) then
                normalizePath fn
            else
                normalizePath (Path.Combine(outputDirectory, sanitizeForPath discriminator, fn))
        { source with FileName = resolved }

    // ---- additional files ----------------------------------------------------

    let private readAdditionalFile path =
        try
            path, File.ReadAllText(path)
        with _ ->
            path, String.Empty

    // ---- disk emission (best-effort) ----------------------------------------

    let private tryWriteToDisk (source: FSharpGeneratedSource) : FSharpSourceGeneratorDiagnostic option =
        try
            let target = source.FileName // already resolved absolute
            let dir = Path.GetDirectoryName(target)
            match dir with
            | null -> ()
            | d when d.Length > 0 -> Directory.CreateDirectory(d) |> ignore
            | _ -> ()
            File.WriteAllText(target, source.SourceText)
            None
        with ex ->
            Some(
                warning
                    "FSGEN_DiskWriteFailed"
                    (sprintf
                        "Failed to write generated file '%s' to disk: %s. The compiler will read the generated source from the in-memory store."
                        source.FileName
                        ex.Message)
            )

    // ---- hashing helpers -----------------------------------------------------

    let private combine (acc: int64) (x: 'a) =
        acc * 31L + int64 (hash x)

    /// Hash the project-options identity (everything AreSameForChecking would
    /// check, plus the host's own stamp if present). Shared by the run-cache key
    /// and the project Stamp.
    let private hashOptionsIdentity (options: FSharpProjectOptions) acc =
        let mutable a = acc
        a <- combine a options.Stamp
        a <- combine a options.ProjectFileName
        a <- combine a options.ProjectId
        a <- combine a (String.concat "\n" options.SourceFiles)
        a <- combine a (String.concat "\n" options.OtherOptions)
        a <- combine a options.UseScriptResolutionRules
        a <- combine a options.IsIncompleteTypeCheckEnvironment
        a <- combine a options.LoadTime
        a <- combine a options.ReferencedProjects.Length
        a <- combine a (options.ReferencedProjects |> Array.map (fun rp -> rp.OutputFile) |> String.concat "\n")
        a <- combine a options.UnresolvedReferences
        a <- combine a (options.OriginalLoadReferences |> List.length)
        a

    let private hashGenerators (generators: IFSharpSourceGenerator list) acc =
        let mutable a = acc
        for g in generators do
            a <- combine a (generatorId g)
            a <- combine a (g.GetType().Assembly.Location)
            a <- combine a (g.GetType().Assembly.FullName)
        a

    let private hashGeneratorOptions (generatorOptions: FSharpSourceGeneratorOptions) acc =
        let mutable a = acc
        a <- combine a generatorOptions.OutputDirectory
        a <- combine a generatorOptions.EmitGeneratedFiles
        a <- combine a generatorOptions.MaxPasses
        a <- combine a (String.concat "\n" generatorOptions.AdditionalFiles)
        a <- combine a (String.concat "\n" generatorOptions.AnalyzerConfigFiles)
        a

    let private hashAdditionalContents (additionalFileContents: Map<string, string>) acc =
        (acc, additionalFileContents |> Map.toList |> List.sortBy fst)
        ||> List.fold (fun a (k, v) -> combine (combine a k) v)

    /// Run-cache key: encodes (options identity + generator set + generator
    /// options + additional-file contents). Excludes generated-source content
    /// (not known until after the run). For non-deterministic generators, the
    /// caller's FSharpProjectOptions.Stamp (set via computeStamp, which includes
    /// generated content) still forces a fresh builder on a cache hit.
    let internal computeRunKey
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        (additionalFileContents: Map<string, string>)
        : int64 =
        0L
        |> hashOptionsIdentity options
        |> hashGenerators generators
        |> hashGeneratorOptions generatorOptions
        |> hashAdditionalContents additionalFileContents

    /// Project Stamp: run-cache key PLUS each generated source's path/content/
    /// order. Used by service.fs to set FSharpProjectOptions.Stamp so that a
    /// generator content change (stable HintName/FileName) invalidates the
    /// incremental builder.
    let internal computeStamp
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        (additionalFileContents: Map<string, string>)
        (generatedSources: FSharpGeneratedSource list)
        : int64 =
        let mutable a = computeRunKey options generators generatorOptions additionalFileContents
        for s in generatedSources do
            a <- combine a s.FileName
            a <- combine a s.SourceText
            a <- combine a s.HintName
            a <- combine a s.Kind
            a <- combine a (string s.Order)
        a

    // ---- core run ------------------------------------------------------------

    /// Run the generators against a fully-formed generator context and return
    /// the run result (generated sources, ordered file list, diagnostics, store).
    let run
        (context: FSharpSourceGeneratorContext)
        (generators: IFSharpSourceGenerator list)
        (options: FSharpSourceGeneratorOptions)
        : FSharpSourceGeneratorRunResult =

        let sw = Stopwatch.StartNew()

        // Run each generator, capturing per-generator exceptions as diagnostics.
        let outputs, generatorDiagnostics =
            generators
            |> List.map (fun generator ->
                try
                    generator.Generate context
                with ex ->
                    {
                        GeneratedSources = []
                        Diagnostics =
                            [
                                error
                                    "FSGEN_GeneratorException"
                                    (sprintf "Generator '%s' threw: %s" (generatorId generator) ex.Message)
                            ]
                    })
            |> List.fold
                (fun (accSources, accDiags) (output: FSharpSourceGeneratorOutput) ->
                    (accSources @ output.GeneratedSources), (accDiags @ output.Diagnostics))
                ([], [])

        // Resolve every generated source to a deterministic absolute path. The
        // HintName is used as the per-source subfolder discriminator so distinct
        // hints get distinct, stable paths even when generators are anonymous.
        let resolvedOutputs, resolutionDiagnostics =
            (([], []), outputs)
            ||> List.fold (fun (acc, diags) s ->
                let resolved = resolveFileName s.HintName options.OutputDirectory s
                let collisionDiag =
                    if
                        context.SourceFiles
                        |> List.exists (fun f -> String.Equals(normalizePath f, resolved.FileName, StringComparison.OrdinalIgnoreCase))
                    then
                        Some(
                            warning
                                "FSGEN_GeneratedPathCollidesWithSource"
                                (sprintf "Generated file path '%s' collides with an original source file." resolved.FileName)
                        )
                    else
                        None
                (acc @ [resolved]), (match collisionDiag with Some d -> diags @ [d] | None -> diags))

        // The in-memory store is the source of truth, populated unconditionally.
        let store =
            FSharpGeneratedSourceStore(resolvedOutputs |> List.map (fun s -> s.FileName, s.SourceText))

        // Disk emission is a best-effort side-effect layered on top of the store.
        let diskDiagnostics =
            if options.EmitGeneratedFiles then
                resolvedOutputs |> List.choose tryWriteToDisk
            else
                []

        let ordered, orderingDiagnostics =
            FSharpGeneratedSourceOrdering.orderSources context.SourceFiles resolvedOutputs

        sw.Stop()

        {
            GeneratedSources = resolvedOutputs
            OrderedSourceFiles = ordered
            Diagnostics = generatorDiagnostics @ resolutionDiagnostics @ diskDiagnostics @ orderingDiagnostics
            ElapsedTime = sw.Elapsed
            Store = store
            CacheHit = false
        }

    // ---- FCS / IDE entry point (with run cache) ------------------------------

    /// Internal helper: build the context from FSharpProjectOptions and run the
    /// generators (no cache).
    let private runWithOptions
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        (additionalFiles: Map<string, string>)
        (ct: System.Threading.CancellationToken)
        : Async<FSharpSourceGeneratorRunResult> =

        async {
            let otherOptions = options.OtherOptions |> Array.toList

            let references =
                otherOptions
                |> List.filter (fun x -> x.StartsWith("-r:", StringComparison.Ordinal) || x.StartsWith("--reference:", StringComparison.Ordinal))
                |> List.map (fun x ->
                    let idx = x.IndexOf(':')
                    if idx >= 0 then x.Substring(idx + 1) else x)

            let defineConstants =
                otherOptions
                |> List.choose (fun x ->
                    if x.StartsWith("--define:", StringComparison.Ordinal) then
                        Some(x.Substring("--define:".Length))
                    else
                        None)

            let context =
                {
                    ProjectFileName = Some options.ProjectFileName
                    ProjectDirectory = match Path.GetDirectoryName(options.ProjectFileName) with null -> "" | d -> d
                    SourceFiles = options.SourceFiles |> Array.toList
                    OtherOptions = otherOptions
                    References = references
                    DefineConstants = defineConstants
                    OutputFile = None
                    AssemblyName = None
                    AdditionalFiles = additionalFiles
                    CancellationToken = ct
                }

            return run context generators generatorOptions
        }

    /// Build a generator context from an FSharpProjectOptions and run the
    /// generators, consulting the per-checker run cache when supplied.
    let runFromProjectOptions
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        (cache: MruCache<AnyCallerThreadToken, int64, FSharpSourceGeneratorRunResult> option)
        : Async<FSharpSourceGeneratorRunResult> =

        async {
            let! ct = Async.CancellationToken

            let additionalFiles =
                generatorOptions.AdditionalFiles
                |> List.map readAdditionalFile
                |> Map.ofList

            let key = computeRunKey options generators generatorOptions additionalFiles

            match cache with
            | Some c ->
                match c.TryGet(AnyCallerThread, key) with
                | Some cached ->
                    return { cached with CacheHit = true }
                | None ->
                    let! result = runWithOptions options generators generatorOptions additionalFiles ct
                    c.Set(AnyCallerThread, key, result)
                    return result
            | None ->
                return! runWithOptions options generators generatorOptions additionalFiles ct
        }

    /// Run the generators from a compiler-driver hook request and return the
    /// response consumed by the shared compiler driver (fsc.fs).
    let runFromCompilerRequest
        (request: FSharp.Compiler.Driver.SourceGenerationCompilerRequest)
        (generators: IFSharpSourceGenerator list)
        (options: FSharpSourceGeneratorOptions)
        : FSharp.Compiler.Driver.SourceGenerationCompilerResponse =

        let additionalFiles =
            options.AdditionalFiles
            |> List.map readAdditionalFile
            |> Map.ofList

        let references =
            request.OtherOptions
            |> List.filter (fun x -> x.StartsWith("-r:", StringComparison.Ordinal) || x.StartsWith("--reference:", StringComparison.Ordinal))
            |> List.map (fun x ->
                let idx = x.IndexOf(':')
                if idx >= 0 then x.Substring(idx + 1) else x)

        let defineConstants =
            request.OtherOptions
            |> List.choose (fun x ->
                if x.StartsWith("--define:", StringComparison.Ordinal) then
                    Some(x.Substring("--define:".Length))
                else
                    None)

        let context =
            {
                ProjectFileName = None
                ProjectDirectory = request.ProjectDirectory
                SourceFiles = request.OriginalSourceFiles
                OtherOptions = request.OtherOptions
                References = references
                DefineConstants = defineConstants
                OutputFile = request.OutputFile
                AssemblyName = Some request.AssemblyName
                AdditionalFiles = additionalFiles
                CancellationToken = request.CancellationToken
            }

        let result = run context generators options

        {
            OrderedSourceFiles = result.OrderedSourceFiles
            GeneratedSources = result.GeneratedSources
            Diagnostics = result.Diagnostics
            Store = result.Store
            ElapsedTime = result.ElapsedTime
        }
