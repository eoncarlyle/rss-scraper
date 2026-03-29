module DerivedSourceFeeds

open System.Threading.Tasks
open System.IO
open Giraffe.ViewEngine
open Queries
open Serialisation
open DomainModels

let sourcesConfiguration =
    File.ReadAllText "Schema/sources.json" |> deserializeSourcesConfiguration

let getDerivedSourceFeedFileName (source: SourceConfig) =
    $"/Users/iain/code/rss-scraper/rssSummary/{source.SourceSlug}.derived.json"

let getTempDerivedSourceFeedFileName (source: SourceConfig) =
    $"{getDerivedSourceFeedFileName source}.tmp"

let isEquivalent (sourceItem: MinimalRssItem) (batchItem: BatchRssItem) =
    sourceItem.Title = batchItem.Item.Title
    && sourceItem.Guid = batchItem.Item.Guid
    && sourceItem.Link = batchItem.Item.Link

let getSourceItemsToSubmit (source: SourceConfig) (incomingRssItems: MinimalRssItem array) =
    let feedFileName = getDerivedSourceFeedFileName source

    if File.Exists feedFileName then
        let existingDerivedFeed =
            File.ReadAllText feedFileName |> deserializeDerivedSourceFeed

        let submittedBatchItems =
            existingDerivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

        if Array.length submittedBatchItems = 0 then
            Array.truncate source.MaximumLookback incomingRssItems
        else

            let truncationIndex = // Don't resubmit anything, respect maximum lookback
                submittedBatchItems
                |> Array.tryFindIndexBack (fun batchItem ->
                    incomingRssItems
                    |> Array.tryFind (fun sourceItem -> isEquivalent sourceItem batchItem)
                    |> Option.isSome)
                |> Option.defaultValue source.MaximumLookback

            incomingRssItems
            |> Array.truncate truncationIndex
            |> Array.filter (fun sourceItem ->
                let maybeMatch =
                    submittedBatchItems
                    |> Array.tryFind (fun batchItem -> isEquivalent sourceItem batchItem)

                maybeMatch.IsNone)
    else
        Array.truncate source.MaximumLookback incomingRssItems

let parseSourceWithSubmitBatch
    source
    (fetchSource: string -> Task<MinimalRssItem array>)
    (modelActions: LangaugeModelActions)
    =
    task {
        let! incomingSourceItems = fetchSource source.SourceUrl

        // Don't resubmit something, don't blow past maximum lookback
        let submittedSourceItems = getSourceItemsToSubmit source incomingSourceItems

        if Array.length submittedSourceItems > 0 then
            let! requestBatch =
                modelActions.SubmitBatch submittedSourceItems
                <| Option.defaultValue defaultSystemPrompt source.SystemPrompt

            return Some requestBatch
        else
            return None
    }

let writeUpdatedDerivedSourceFeed (source: SourceConfig) (updatedDerivedFeed: DerivedSourceFeed) =
    let feedFileName = getDerivedSourceFeedFileName source

    if File.Exists feedFileName |> not then
        failwith $"Derived feed {feedFileName} does not exist"

    let tempFeedFileName = getTempDerivedSourceFeedFileName source
    File.WriteAllText(tempFeedFileName, serializeDerivedSourceFeed updatedDerivedFeed)
    File.Move(tempFeedFileName, feedFileName, true)


let appendBatchToFeed (source: SourceConfig) (incomingSubmitBatch: SourceFeedSummaryRequestBatch) =
    let feedFileName = getDerivedSourceFeedFileName source
    let tempFeedFileName = getTempDerivedSourceFeedFileName source

    if File.Exists feedFileName then
        let existingDerivedFeed =
            File.ReadAllText feedFileName |> deserializeDerivedSourceFeed

        let updatedDerivedFeed =
            { SourceUrl = existingDerivedFeed.SourceUrl
              Batches = Array.append existingDerivedFeed.Batches [| incomingSubmitBatch |] }

        writeUpdatedDerivedSourceFeed source updatedDerivedFeed
        ()
    else
        let derivedSourceFeed =
            { SourceUrl = source.SourceUrl
              Batches = [| incomingSubmitBatch |] }

        File.WriteAllText(feedFileName, serializeDerivedSourceFeed derivedSourceFeed)

    ()

let pollSource source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = parseSourceWithSubmitBatch source fetchSource modelActions
        maybeSubmitBatch |> Option.iter (appendBatchToFeed source)
    }

let getFeedUpdate (source: SourceConfig) (modelActions: LangaugeModelActions) =
    let feedFileName = getDerivedSourceFeedFileName source

    if File.Exists feedFileName then
        let derivedSourceFeed =
            File.ReadAllText feedFileName |> deserializeDerivedSourceFeed

        task {
            let! maybeUpdatedDerivedSourceFeed = modelActions.GetUpdatedDerivedFeed source derivedSourceFeed
            maybeUpdatedDerivedSourceFeed |> Option.iter (writeUpdatedDerivedSourceFeed source)
            return maybeUpdatedDerivedSourceFeed.IsSome
        }

    else failwith $"Derived feed {feedFileName} does not exist"
