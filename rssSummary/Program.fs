open System
open AppGeminiCommon
open System.Threading.Tasks
open DerivedFeeds
open DomainModel

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"

let summarise (source: SourceSetting) modelActions fetchSource =
    task {
        let! maybeDerivedBatch = submitSummaryBatch source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            Console.WriteLine
                $"{source.SourceSlug} request batch update: {derivedBatch.Id}, {derivedBatch.BatchItems.Length} items"

            let! appendBatchResult = appendToFeed source derivedBatch
            Console.WriteLine $"{source.SourceSlug} request batch result: {resultMessage appendBatchResult}"
            ()
        | _ -> ()

        let! pollFeedUpdateResult = tryFeedUpdateWithSummaryResults source modelActions

        let pollFeedMessage =
            match pollFeedUpdateResult with
            | Ok 0 -> "feed unchanged"
            | Ok value -> $"{value} records added"
            | Error code -> $"failed with status code {code}"

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

        let summarise' = summarise source modelActions

        return!
            match source.SourceSlug with
            | SourceSlug.Artemis -> summarise' SourceFeeds.Artemis.fetchSource
            | GroceryDive -> summarise' SourceFeeds.Dive.fetchSource
            | CStoreDive -> summarise' SourceFeeds.Dive.fetchSource
            | _ -> failwith "not implemented"
    }

[<EntryPoint>]
let main args =
    let enabledSources = sourcesConfiguration.Sources |> Array.filter _.Enabled

    Array.map handleSource enabledSources |> Task.WhenAll |> _.Wait()

    0
