open System
open AppGeminiCommon
open System.Threading.Tasks
open DerivedFeeds
open DomainModel
open Giraffe.ViewEngine

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"


let summarise (source: SourceSetting) fetchSource modelActions =
    task {
        let! maybeDerivedBatch = parseSourceWithSubmitBatch source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            Console.WriteLine $"{source.SourceSlug} request batch update: {derivedBatch.Id}, {derivedBatch.BatchItems.Length} items"
            let! appendBatchResult = appendBatchToFeed source derivedBatch
            Console.WriteLine $"{source.SourceSlug} request batch result: {resultMessage appendBatchResult}"
            ()
        | _ -> ()

        let! pollFeedUpdateResult = tryPollFeedUpdate source modelActions

        let pollFeedMessage =
            match pollFeedUpdateResult with
            | Ok 0 -> "feed unchanged"
            | Ok value -> $"${value} records added"
            | Error code -> $"failed with status code ${code}"

        Console.WriteLine $"{source.SourceSlug} poll derived feed update: {pollFeedMessage}"
        ()
    }

let handleSource source =
    task {
        let modelActions =
            match source.Model, source.Synchronous with
            | ClaudeHaiku45, _ -> AppAnthropic.Haiku45Actions
            | Gemini25FlashLite, None -> AppGemini.AppGemini25FlashActions
            | Gemini25FlashLite, Some b ->
                if b then
                    AppGeminiSynchronous.AppGemini25FlashSynchronousActions
                else
                    AppGemini.AppGemini25FlashActions

        match source.SourceSlug with
        | Artemis -> return! summarise source SourceFeeds.Artemis.fetchSource modelActions
        | GroceryDive -> return! summarise source SourceFeeds.Dive.fetchSource modelActions
        | CStoreDive -> return! summarise source SourceFeeds.Dive.fetchSource modelActions
        | _ -> failwith "not implemented"
    }

[<EntryPoint>]
let main args =
    let enabledSources = sourcesConfiguration.Sources |> Array.filter _.Enabled

    Array.map handleSource enabledSources |> Task.WhenAll |> _.Wait()

    0
