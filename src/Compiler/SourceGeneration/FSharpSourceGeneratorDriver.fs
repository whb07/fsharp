namespace FSharp.Compiler.SourceGeneration

open System
open System.Diagnostics
open System.IO
open FSharp.Compiler.CodeAnalysis

/// The library-first source generator driver. Runs a set of source generators
/// against a compilation context, writes the generated sources to disk, and
/// produces the final ordered source list handed back to the compiler.
module internal FSharpSourceGeneratorDriver =

    let private readAdditionalFile path =
        try
            path, File.ReadAllText(path)
        with _ ->
            path, String.Empty

    let private writeGeneratedSource (outputRoot: string) (source: FSharpGeneratedSource) =
        let target = source.FileName

        let target =
            if Path.IsPathRooted(target) then
                target
            else
                Path.Combine(outputRoot, target)

        let dir = Path.GetDirectoryName(target)

        match dir with
        | null -> ()
        | d when d.Length > 0 -> Directory.CreateDirectory(d) |> ignore
        | _ -> ()

        File.WriteAllText(target, source.SourceText)
        target

    /// Run the generators against a fully-formed generator context and return
    /// the run result (generated sources, ordered file list, diagnostics).
    let run
        (context: FSharpSourceGeneratorContext)
        (generators: IFSharpSourceGenerator list)
        (options: FSharpSourceGeneratorOptions)
        : FSharpSourceGeneratorRunResult =

        let sw = Stopwatch.StartNew()

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
                                {
                                    Id = "FSGEN_GeneratorException"
                                    Message = sprintf "Generator '%A' threw: %s" (generator.GetType()) ex.Message
                                    Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
                                    Range = None
                                }
                            ]
                    })
            |> List.fold
                (fun (accSources, accDiags) (output: FSharpSourceGeneratorOutput) ->
                    (accSources @ output.GeneratedSources), (accDiags @ output.Diagnostics))
                ([], [])

        let emittedSourcesForOrdering =
            if options.EmitGeneratedFiles then
                outputs
                |> List.map (fun s ->
                    // Write the generated source to disk and update the file name to the
                    // on-disk path so the ordering engine and compiler see the real file.
                    let written = writeGeneratedSource options.OutputDirectory s
                    { s with FileName = written })
            else
                outputs

        let ordered, orderingDiagnostics =
            FSharpGeneratedSourceOrdering.orderSources context.SourceFiles emittedSourcesForOrdering

        sw.Stop()

        {
            GeneratedSources = emittedSourcesForOrdering
            OrderedSourceFiles = ordered
            Diagnostics = generatorDiagnostics @ orderingDiagnostics
            ElapsedTime = sw.Elapsed
        }

    /// Build a generator context from an FSharpProjectOptions and run the generators.
    let runFromProjectOptions
        (options: FSharpProjectOptions)
        (generators: IFSharpSourceGenerator list)
        (generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpSourceGeneratorRunResult> =

        async {
            let additionalFiles =
                generatorOptions.AdditionalFiles
                |> List.map readAdditionalFile
                |> Map.ofList

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
                    CancellationToken = Threading.CancellationToken.None
                }

            return run context generators generatorOptions
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
            ElapsedTime = result.ElapsedTime
        }
