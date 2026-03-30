module DerivedSourceFeeds

open System.IO
open System.Threading.Tasks
open Queries
open Serialisation
open DomainModels


let sourcesConfiguration =
    File.ReadAllText "sources.json" |> deserializeSourcesConfiguration

let getDerivedSourceFeedKey (source: SourceConfig) = $"{source.SourceSlug}.derived.json"

let isEquivalent (sourceItem: MinimalRssItem) (batchItem: BatchRssItem) =
    sourceItem.Title = batchItem.Item.Title
    && sourceItem.Guid = batchItem.Item.Guid
    && sourceItem.Link = batchItem.Item.Link

let getSourceItemsToSubmit (source: SourceConfig) (incomingRssItems: MinimalRssItem array) =
    task {
        let feedKey = getDerivedSourceFeedKey source
        let! maybeContent = ObjectStorage.getObjectAsync feedKey

        match maybeContent with
        | Some content ->
            let existingDerivedFeed = deserializeDerivedSourceFeed content

            let submittedBatchItems =
                existingDerivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return Array.truncate source.MaximumLookback incomingRssItems
            else
                let truncationIndex =
                    submittedBatchItems
                    |> Array.tryFindIndexBack (fun batchItem ->
                        incomingRssItems
                        |> Array.tryFind (fun sourceItem -> isEquivalent sourceItem batchItem)
                        |> Option.isSome)
                    |> Option.defaultValue source.MaximumLookback

                return
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> isEquivalent sourceItem batchItem)

                        maybeMatch.IsNone)
        | None -> return Array.truncate source.MaximumLookback incomingRssItems
    }

let parseSourceWithSubmitBatch
    source
    (fetchSource: string -> Task<MinimalRssItem array>)
    (modelActions: LangaugeModelActions)
    =
    task {
        let! incomingSourceItems = fetchSource source.SourceUrl
        let! submittedSourceItems = getSourceItemsToSubmit source incomingSourceItems

        if Array.length submittedSourceItems > 0 then
            let submitBatchParameters =
                { SystemPrompt = Option.defaultValue defaultSystemPrompt source.SystemPrompt
                  InputTokenCutoff = source.InputTokenCutoff
                  OutputTokenCutoff = source.OutputTokenCutoff }

            let! requestBatch = modelActions.SubmitBatch submittedSourceItems submitBatchParameters

            return Some requestBatch
        else
            return None
    }

let writeUpdatedDerivedSourceFeed (source: SourceConfig) (updatedDerivedFeed: DerivedSourceFeed) =
    task {
        let feedKey = getDerivedSourceFeedKey source
        let! exists = ObjectStorage.objectExistsAsync feedKey

        if not exists then
            failwith $"Derived feed {feedKey} does not exist"

        do! ObjectStorage.putObjectAsync feedKey (serializeDerivedSourceFeed updatedDerivedFeed)
    }

let appendBatchToFeed (source: SourceConfig) (incomingSubmitBatch: SourceFeedSummaryRequestBatch) =
    task {
        let feedKey = getDerivedSourceFeedKey source
        let! maybeContent = ObjectStorage.getObjectAsync feedKey

        match maybeContent with
        | Some content ->
            let existingDerivedFeed = deserializeDerivedSourceFeed content

            let updatedDerivedFeed =
                { SourceUrl = existingDerivedFeed.SourceUrl
                  Batches = Array.append existingDerivedFeed.Batches [| incomingSubmitBatch |] }

            do! ObjectStorage.putObjectAsync feedKey (serializeDerivedSourceFeed updatedDerivedFeed)
        | None ->
            let derivedSourceFeed =
                { SourceUrl = source.SourceUrl
                  Batches = [| incomingSubmitBatch |] }

            do! ObjectStorage.putObjectAsync feedKey (serializeDerivedSourceFeed derivedSourceFeed)
    }

let pollSource source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = parseSourceWithSubmitBatch source fetchSource modelActions

        match maybeSubmitBatch with
        | Some batch -> do! appendBatchToFeed source batch
        | None -> ()
    }

let tryPollFeedUpdate (source: SourceConfig) (modelActions: LangaugeModelActions) =
    task {
        let feedKey = getDerivedSourceFeedKey source
        let! maybeContent = ObjectStorage.getObjectAsync feedKey

        match maybeContent with
        | Some content ->
            let derivedSourceFeed = deserializeDerivedSourceFeed content
            let! maybeUpdatedDerivedSourceFeed = modelActions.GetUpdatedDerivedFeed source derivedSourceFeed

            match maybeUpdatedDerivedSourceFeed with
            | Some updated ->
                do! ObjectStorage.putObjectAsync feedKey (serializeDerivedSourceFeed updated)
                return Ok true
            | None -> return Ok false
        | None -> return Error $"Derived feed {feedKey} does not exist"
    }
