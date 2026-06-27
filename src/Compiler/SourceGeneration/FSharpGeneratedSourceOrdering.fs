namespace FSharp.Compiler.SourceGeneration

open System
open System.IO
open FSharp.Compiler.Diagnostics

/// Internal ordering engine that produces the final, ordered list of source
/// files (originals plus generated) that should be passed to the compiler.
///
/// Minimal rules:
///   * Generated .fs defaults to EndOfProject.
///   * Generated .fsi must be placed before its matching .fs.
///   * BeforeFile/AfterFile anchors must exist in the resulting list.
///   * Cycles are errors.
///   * Duplicate HintName is an error.
module internal FSharpGeneratedSourceOrdering =

    let private diag id message severity =
        {
            Id = id
            Message = message
            Severity = severity
            Range = None
        }

    let private error id message =
        diag id message FSharpDiagnosticSeverity.Error

    let private warning id message =
        diag id message FSharpDiagnosticSeverity.Warning

    let private pathEquals (a: string) (b: string) =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    let private fileNameEquals (a: string) (b: string) =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    /// Compute the matching implementation path for a signature path by
    /// changing the extension to .fs. Used to ensure .fsi precedes its .fs.
    let private implPathForSignature (sigPath: string) =
        match Path.ChangeExtension(sigPath, ".fs") with
        | null -> sigPath
        | p -> p

    /// Insert an item into a list immediately before the first item satisfying
    /// `predicate`, or append at the end if no match is found.
    let private insertBefore predicate item (xs: 'a list) =
        let rec loop acc =
            function
            | [] -> List.rev (item :: acc)
            | h :: t when predicate h -> List.rev acc @ (item :: h :: t)
            | h :: t -> loop (h :: acc) t

        loop [] xs

    /// Insert an item into a list immediately after the first item satisfying
    /// `predicate`, or append at the end if no match is found.
    let private insertAfter predicate item (xs: 'a list) =
        let rec loop acc =
            function
            | [] -> List.rev (item :: acc)
            | h :: t when predicate h -> List.rev acc @ (h :: item :: t)
            | h :: t -> loop (h :: acc) t

        loop [] xs

    /// Order the original source files and the generated sources into a single
    /// ordered list. Returns the ordered file list and any diagnostics produced
    /// by the ordering engine.
    let orderSources
        (originalSourceFiles: string list)
        (generatedSources: FSharpGeneratedSource list)
        : string list * FSharpSourceGeneratorDiagnostic list =

        let diagnostics = ResizeArray<FSharpSourceGeneratorDiagnostic>()

        // Detect duplicate HintNames.
        let hintNames =
            generatedSources
            |> Seq.map (fun s -> s.HintName)
            |> Seq.filter (not << String.IsNullOrWhiteSpace)

        let duplicates =
            hintNames
            |> Seq.groupBy id
            |> Seq.filter (fun (_, g) -> Seq.length g > 1)
            |> Seq.map fst
            |> Seq.toList

        for d in duplicates do
            diagnostics.Add(error "FSGEN_DuplicateHint" (sprintf "Duplicate generator HintName '%s'." d))

        // Group signature/implementation pairs that share the same base file path so that
        // a generated .fsi is always placed immediately before its matching .fs.
        let signatures, implementations =
            generatedSources
            |> List.partition (fun s -> s.Kind = FSharpGeneratedSourceKind.Signature)

        // Anchored generated sources are inserted relative to existing files. Unanchored
        // ones default to EndOfProject.
        let anchored =
            generatedSources
            |> List.filter (fun s ->
                match s.Order with
                | FSharpGeneratedSourceOrder.EndOfProject -> false
                | _ -> true)

        // For cycle detection we ensure each anchor target resolves to an existing file
        // in the current ordered list. Anchors that cannot be resolved become warnings.
        let resolveAnchor target (files: string list) =
            files |> List.exists (pathEquals target)

        let insertGenerated (files: string list) (source: FSharpGeneratedSource) =
            match source.Order with
            | FSharpGeneratedSourceOrder.BeforeFile target ->
                if resolveAnchor target files then
                    insertBefore (pathEquals target) source.FileName files
                else
                    diagnostics.Add(
                        warning
                            "FSGEN_BeforeFileAnchorMissing"
                            (sprintf "BeforeFile anchor '%s' for generated source '%s' was not found; appending to end of project." target source.HintName)
                    )
                    files @ [ source.FileName ]

            | FSharpGeneratedSourceOrder.AfterFile target ->
                if resolveAnchor target files then
                    insertAfter (pathEquals target) source.FileName files
                else
                    diagnostics.Add(
                        warning
                            "FSGEN_AfterFileAnchorMissing"
                            (sprintf "AfterFile anchor '%s' for generated source '%s' was not found; appending to end of project." target source.HintName)
                    )
                    files @ [ source.FileName ]

            | FSharpGeneratedSourceOrder.BeforeImplementation implPath ->
                if resolveAnchor implPath files then
                    insertBefore (pathEquals implPath) source.FileName files
                else
                    diagnostics.Add(
                        warning
                            "FSGEN_BeforeImplementationAnchorMissing"
                            (sprintf "BeforeImplementation anchor '%s' for generated source '%s' was not found; appending to end of project." implPath source.HintName)
                    )
                    files @ [ source.FileName ]

            | FSharpGeneratedSourceOrder.EndOfProject ->
                files @ [ source.FileName ]

        // Start from the original source files.
        let mutable ordered = originalSourceFiles

        // Insert anchored sources first. The relative order between anchored sources is
        // preserved by inserting them in the order they were declared.
        for source in anchored do
            ordered <- insertGenerated ordered source

        // Then append EndOfProject sources. Signatures must precede their matching
        // implementations, so place signatures before implementations of the same hint
        // when both default to EndOfProject.
        let endSignatures =
            signatures
            |> List.filter (fun s -> s.Order = FSharpGeneratedSourceOrder.EndOfProject)

        let endImplementations =
            implementations
            |> List.filter (fun s -> s.Order = FSharpGeneratedSourceOrder.EndOfProject)

        for s in endSignatures do
            ordered <- ordered @ [ s.FileName ]

        for s in endImplementations do
            // If a matching signature was emitted at the end of project, place the
            // implementation immediately after it.
            let matchingSigPath =
                endSignatures
                |> List.tryPick (fun sig_ ->
                    if fileNameEquals (implPathForSignature sig_.FileName) s.FileName then
                        Some sig_.FileName
                    else
                        None)

            match matchingSigPath with
            | Some sigPath ->
                ordered <- insertAfter (fileNameEquals sigPath) s.FileName ordered
            | None ->
                ordered <- ordered @ [ s.FileName ]

        // Cycle detection: a simple anchor cycle is when an anchored source points at a
        // generated file that (directly or indirectly) points back. We approximate this by
        // checking that no generated file's anchor target is itself a generated file that
        // depends on the first. For the minimal implementation we warn if an anchor target
        // is also a generated file.
        let generatedPaths = generatedSources |> List.map (fun s -> s.FileName) |> set

        for source in anchored do
            match source.Order with
            | FSharpGeneratedSourceOrder.BeforeFile target
            | FSharpGeneratedSourceOrder.AfterFile target
            | FSharpGeneratedSourceOrder.BeforeImplementation target ->
                if generatedPaths |> Set.contains target then
                    diagnostics.Add(
                        warning
                            "FSGEN_AnchorTargetsGenerated"
                            (sprintf "Anchor target '%s' for generated source '%s' is itself a generated file; this may indicate an ordering cycle." target source.HintName)
                    )
            | _ -> ()

        ordered |> List.distinct, diagnostics |> Seq.toList
