open System
open System.Threading.Tasks
open DerivedFeeds
open DomainModels

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"


let summarise (source: SourceFeed) fetchSource modelActions =
    task {
        let! maybeDerivedBatch = parseSourceWithSubmitBatch source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            Console.WriteLine $"Request batch update: {derivedBatch.BatchItems.Length}, {derivedBatch.Id}"
            let! appendBatchResult = appendBatchToFeed source derivedBatch
            Console.WriteLine $"Request batch result: {resultMessage appendBatchResult}"
            ()
        | _ -> ()

        let! pollFeedUpdateResult = tryPollFeedUpdate source modelActions
        Console.WriteLine $"Poll feed update result: {resultMessage pollFeedUpdateResult}"
        ()
    }

let handleSource source =
    task {
        let modelActions =
            source.Model
            |> Option.map (fun m ->
                match m with
                | ClaudeHaiku45 -> AppAnthropic.Haiku45Actions
                | Gemini25FlashLite -> AppGemini.AppGeminiActions)
            |> Option.defaultValue AppAnthropic.Haiku45Actions

        match source.SourceSlug with
        | Artemis -> return! summarise source SourceFeeds.Artemis.fetchSource modelActions
        | GroceryDive -> return! summarise source SourceFeeds.GroceryDive.fetchSource modelActions
        | _ -> failwith "not implemented"
    }

[<EntryPoint>]
let main args =
    let enabledSources = sourcesConfiguration.Sources |> Array.filter _.Enabled

    Array.map handleSource enabledSources |> Task.WhenAll |> _.Wait()

    0
