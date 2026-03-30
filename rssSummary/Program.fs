open System
open System.Threading.Tasks
open DerivedSourceFeeds
open DomainModels

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"

let summariseSource source =
    task {
        let modelActions =
            source.Model
            |> Option.map (fun m ->
                match m with
                | ClaudeHaiku45 -> AppAnthropic.Haiku45Actions
                | Gemini25FlashLite -> AppGemini.AppGeminiActions)
            |> Option.defaultValue AppAnthropic.Haiku45Actions

        match source.SourceSlug with
        | Artemis ->
            let! maybeRequestBatch =
                parseSourceWithSubmitBatch source OriginalSourceFeeds.Artemis.fetchSource modelActions

            match maybeRequestBatch with
            | Some requestBatch ->
                Console.WriteLine $"Request batch update: {requestBatch.BatchItems.Length}, {requestBatch.Id}"
                let! appendBatchResult = appendBatchToFeed source requestBatch
                Console.WriteLine $"Request batch result: {resultMessage appendBatchResult}"
                ()
            | _ -> ()

            let! pollFeedUpdateResult = tryPollFeedUpdate source modelActions
            Console.WriteLine $"Poll feed update result: {resultMessage pollFeedUpdateResult}"
            ()
        | GroceryDive ->
            let! a = OriginalSourceFeeds.GroceryDive.fetchSource source.SourceUrl
            Console.WriteLine a
            ()
        | _ -> failwith "not implemented"
    }

[<EntryPoint>]
let main args =


    let firstSource =
        sourcesConfiguration.Sources
        |> Array.tryFind (fun s -> s.SourceSlug = "grocery-dive")
        |> Option.defaultWith (fun () -> failwith "No enabled sources configured")

    summariseSource firstSource |> Task.WaitAll

    0
