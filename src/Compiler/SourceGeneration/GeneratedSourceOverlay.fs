namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open FSharp.Compiler.IO

/// Internal FileSystem overlay that lets the compiler read generated source from
/// memory without the files existing on disk. Installed once as a decorator over
/// the global `FileSystem` shim; the active overlay map is mutated by
/// `registerGeneratedSourceOverlay` (and removed on dispose).
///
/// This is the single hook the spec calls for: the compiler's file-open/read and
/// file-existence/timestamp paths consult this overlay, so generated paths are
/// readable in both the fsc.exe/main1 path (ParseOneInputFile) and the FCS/IDE
/// path (IncrementalBuilder's OnDisk fallback through FSharpSource).
module internal GeneratedSourceOverlay =

    let private sync = obj()

    // Active overlay: normalized absolute path -> (source text, stable timestamp).
    let private entries =
        ConcurrentDictionary<string, string * DateTime>(StringComparer.OrdinalIgnoreCase)

    let mutable private installed = false

    let normalize (path: string) =
        if String.IsNullOrWhiteSpace path then
            String.Empty
        else
            try Path.GetFullPath(path)
            with _ ->
                path

    /// Install the overlay decorator over the global FileSystem shim. Idempotent
    /// and thread-safe. Only the read/exists/timestamp members consult the overlay;
    /// everything else delegates to the previously-installed shim.
    let ensureInstalled () =
        lock sync (fun () ->
            if not installed then
                let inner = FileSystem

                let fs =
                    { new IFileSystem with
                        member _.AssemblyLoader = inner.AssemblyLoader

                        member _.OpenFileForReadShim(filePath, ?useMemoryMappedFile, ?shouldShadowCopy) =
                            match entries.TryGetValue(normalize filePath) with
                            | true, (content, _) ->
                                upcast new MemoryStream(Encoding.UTF8.GetBytes content, writable = false)
                            | _ ->
                                inner.OpenFileForReadShim(
                                    filePath,
                                    ?useMemoryMappedFile = useMemoryMappedFile,
                                    ?shouldShadowCopy = shouldShadowCopy
                                )

                        member _.OpenFileForWriteShim(filePath, ?fileMode, ?fileAccess, ?fileShare) =
                            inner.OpenFileForWriteShim(
                                filePath,
                                ?fileMode = fileMode,
                                ?fileAccess = fileAccess,
                                ?fileShare = fileShare
                            )

                        member _.GetFullPathShim fileName = inner.GetFullPathShim fileName
                        member _.GetFullFilePathInDirectoryShim dir fileName = inner.GetFullFilePathInDirectoryShim dir fileName
                        member _.IsPathRootedShim path = inner.IsPathRootedShim path
                        member _.NormalizePathShim path = inner.NormalizePathShim path
                        member _.IsInvalidPathShim path = inner.IsInvalidPathShim path
                        member _.GetTempPathShim() = inner.GetTempPathShim()
                        member _.GetDirectoryNameShim path = inner.GetDirectoryNameShim path

                        member _.GetLastWriteTimeShim fileName =
                            match entries.TryGetValue(normalize fileName) with
                            | true, (_, ts) -> ts
                            | _ -> inner.GetLastWriteTimeShim fileName

                        member _.GetCreationTimeShim path =
                            match entries.TryGetValue(normalize path) with
                            | true, (_, ts) -> ts
                            | _ -> inner.GetCreationTimeShim path

                        member _.CopyShim(src, dest, overwrite) = inner.CopyShim(src, dest, overwrite)

                        member _.FileExistsShim fileName =
                            match entries.ContainsKey(normalize fileName) with
                            | true -> true
                            | _ -> inner.FileExistsShim fileName

                        member _.FileDeleteShim fileName = inner.FileDeleteShim fileName
                        member _.DirectoryCreateShim path = inner.DirectoryCreateShim path
                        member _.DirectoryExistsShim path = inner.DirectoryExistsShim path
                        member _.DirectoryDeleteShim path = inner.DirectoryDeleteShim path
                        member _.EnumerateFilesShim(path, pattern) = inner.EnumerateFilesShim(path, pattern)
                        member _.EnumerateDirectoriesShim path = inner.EnumerateDirectoriesShim path
                        member _.IsStableFileHeuristic fileName = inner.IsStableFileHeuristic fileName
                        member _.ChangeExtensionShim(path, extension) = inner.ChangeExtensionShim(path, extension)
                    }

                FileSystem <- fs
                installed <- true)

    /// Register the given store's contents as the active overlay and return a scope
    /// that removes exactly the keys it added. The scope is disposable so the
    /// compile path can scope the overlay to a single run; the IDE path may ignore
    /// the scope to keep the overlay alive for the lifetime of a project check
    /// (re-running with the same deterministic paths simply overwrites).
    let register (store: FSharpGeneratedSourceStore) : IDisposable =
        ensureInstalled ()
        let added = ResizeArray<string>()
        let ts = DateTime.UtcNow
        for kv in store.ToOverlayMap() do
            entries[kv.Key] <- (kv.Value, ts)
            added.Add(kv.Key)

        { new IDisposable with
            member _.Dispose() =
                for k in added do
                    entries.TryRemove(k) |> ignore }
