open System
open System.Threading.Tasks
open DerivedSourceFeeds
open DomainModels
open Giraffe.ViewEngine
open Queries

[<EntryPoint>]
let main args =

    let firstSource =
        sourcesConfiguration.Sources
        |> Array.tryFind (fun s -> s.Enabled)
        |> Option.defaultWith (fun () -> failwith "No enabled sources configured")

    task {
        let modelActions =
            firstSource.Model
            |> Option.map (fun m ->
                match m with
                | ClaudeHaiku45 -> AppAnthropic.Haiku45Actions
                | Gemini25FlashLite -> AppGemini.AppGeminiActions)
            |> Option.defaultValue AppAnthropic.Haiku45Actions

        if firstSource.SourceSlug = "artemis" then
            let! _ = parseSourceWithSubmitBatch firstSource OriginalSourceFeeds.Artemis.fetchSource modelActions
            let! _ = getFeedUpdate firstSource modelActions
            ()
        else
            failwith "not implemented"
    }
    |> Task.WaitAll

    0
