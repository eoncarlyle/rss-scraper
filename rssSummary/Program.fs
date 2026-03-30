open System
open System.Threading.Tasks
open DerivedSourceFeeds
open DomainModels

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
            let! maybeRequestBatch =
                parseSourceWithSubmitBatch firstSource OriginalSourceFeeds.Artemis.fetchSource modelActions

            let maybeBatchAppend = maybeRequestBatch |> Option.map (appendBatchToFeed firstSource)
            let! a = tryPollFeedUpdate firstSource modelActions
            Console.WriteLine(a)
            ()
        else
            failwith "not implemented"
    }
    |> Task.WaitAll

    0
