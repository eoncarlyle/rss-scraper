open System
open AppGeminiCommon
open System.Threading.Tasks
open DomainModel

let resultMessage (result: Result<'a, 'b>) = if result.IsOk then "Ok" else "Error"

let updateDerivedFeed (source: SourceSetting) modelActions fetchSource =
    task {
        let! maybeDerivedBatch = DerivedFeeds.submitSummaryBatch source fetchSource modelActions

        match maybeDerivedBatch with
        | Some derivedBatch ->
            Console.WriteLine
                $"{source.SourceSlug} request batch update: {derivedBatch.Id}, {derivedBatch.BatchItems.Length} items"

            let! appendBatchResult = DerivedFeeds.appendToFeed source derivedBatch
            Console.WriteLine $"{source.SourceSlug} request batch result: {resultMessage appendBatchResult}"
            ()
        | _ -> ()

        let! pollFeedUpdateResult = DerivedFeeds.tryFeedUpdateWithSummaryResults source modelActions

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

        let updateDerivedFeed' = updateDerivedFeed source modelActions

        return!
            match source.SourceSlug with
            | SourceSlug.Artemis -> updateDerivedFeed' SourceFeeds.Artemis.fetchSource
            | GroceryDive -> updateDerivedFeed' SourceFeeds.Dive.fetchSource
            | CStoreDive -> updateDerivedFeed' SourceFeeds.Dive.fetchSource
            | _ -> failwith "not implemented"
    }

let rec handleSink (sink: SinkSetting) =
    task {
        let! feedUpdateResult = SinkFeeds.feedUpdate sink
        match feedUpdateResult with
        | Ok _ -> ()
        | Error result -> Console.WriteLine $"Sink feed update failed with status code {result}"
    }

type testType = {Guid: String option}
    
[<EntryPoint>]
let main args =
    let enabledSources = SourceFeeds.sourceSettings.Sources |> Array.filter _.Enabled
    Array.map handleSource enabledSources |> Task.WhenAll |> _.Wait()
    Array.map handleSink SinkFeeds.sinkSettings.Sinks |> Task.WhenAll |> _.Wait()
    0
