module DerivedFeeds

open System
open System.Threading.Tasks
open Serialisation
open DomainModel
open LanguageModelCommon
open ObjectStorage

let getDerivedFeedKeyFromSlug (sourceSlug: SourceSlug) = $"{sourceSlug}.derived.json"

let getDerivedFeedKeyFromSource (sourceSetting: SourceSetting) =
    getDerivedFeedKeyFromSlug sourceSetting.SourceSlug

let crossItemEquivalent (item: RssItem) (batchItem: DerivedItem) =
    item.Guid = batchItem.Item.Guid
    && item.Link = batchItem.Item.Link

let getFreshSourceItems (storage: ObjectStorageService) (sourceSetting: SourceSetting) (incomingRssItems: RssItem array) =
    task {
        let feedKey = getDerivedFeedKeyFromSource sourceSetting
        let! maybeDerivedS3Object = storage.GetObject feedKey

        match maybeDerivedS3Object with
        | Some derivedS3Object ->
            let derivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

            let submittedBatchItems =
                derivedFeed.Batches |> Array.map _.BatchItems |> Array.concat

            if Array.length submittedBatchItems = 0 then
                return Array.truncate sourceSetting.MaximumLookback incomingRssItems
            else
                let truncationIndex =
                    incomingRssItems
                    |> Array.tryFindIndexBack (fun incomingItem ->
                        submittedBatchItems
                        |> Array.tryFind (crossItemEquivalent incomingItem)
                        |> Option.isSome)
                    |> Option.defaultValue sourceSetting.MaximumLookback

                return
                    incomingRssItems
                    |> Array.truncate truncationIndex
                    |> Array.filter (fun sourceItem ->
                        let maybeMatch =
                            submittedBatchItems
                            |> Array.tryFind (fun batchItem -> crossItemEquivalent sourceItem batchItem)

                        maybeMatch.IsNone)
        | None -> return Array.truncate sourceSetting.MaximumLookback incomingRssItems
    }

let submitSummaryBatch
    (storage: ObjectStorageService)
    (source: SourceSetting)
    (fetchSource: string -> Task<RssItem array>)
    (modelActions: LanguageModelActions)
    =
    task {
        let! sourceItems = fetchSource source.SourceUrl
        let! submittedSourceItems = getFreshSourceItems storage source sourceItems

        if Array.length submittedSourceItems > 0 then
            let submitBatchParameters =
                { SystemPrompt = Option.defaultValue defaultSystemPrompt source.SystemPrompt
                  InputTokenCutoff = source.InputTokenCutoff
                  OutputTokenCutoff = source.OutputTokenCutoff }

            let! summaryRequest = modelActions.SubmitBatch submittedSourceItems submitBatchParameters

            return Some summaryRequest
        else
            return None
    }

let appendToFeed (storage: ObjectStorageService) (sourceSetting: SourceSetting) (derivedBatch: DerivedBatch) =
    let append sourceSetting derivedBatch =
        task {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = storage.GetObject feedKey

            match maybeDerivedS3Object with
            | Some derivedS3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> derivedS3Object.Content

                let updatedDerivedFeed =
                    { SourceUrl = existingDerivedFeed.SourceUrl
                      Batches = Array.append existingDerivedFeed.Batches [| derivedBatch |] }

                return! storage.PutObject feedKey (serialise updatedDerivedFeed) (Some derivedS3Object.ETag)
            | None ->
                let derivedFeed =
                    { SourceUrl = sourceSetting.SourceUrl
                      Batches = [| derivedBatch |] }

                return! storage.PutObject feedKey (serialise derivedFeed) None
        }

    storage.RetryHttp 3 (fun () -> append sourceSetting derivedBatch)

let feedUpdateWithSummaryRequests storage source fetchSource modelActions =
    task {
        let! maybeSubmitBatch = submitSummaryBatch storage source fetchSource modelActions
        match maybeSubmitBatch with
        | Some batch ->
            let! b = appendToFeed storage source batch
            return Some b
        | None -> return None
    }

let tryFeedUpdateWithSummaryResults (storage: ObjectStorageService) sourceSetting modelActions =
    let update sourceSetting modelActions =
        task {
            let feedKey = getDerivedFeedKeyFromSource sourceSetting
            let! maybeDerivedS3Object = storage.GetObject feedKey

            match maybeDerivedS3Object with
            | Some s3Object ->
                let existingDerivedFeed = deserialise<DerivedFeed> s3Object.Content
                let! maybeUpdatedDerivedFeed = modelActions.GetUpdatedDerivedFeed sourceSetting existingDerivedFeed

                match maybeUpdatedDerivedFeed with
                | Some updated ->
                    let! putResult = storage.PutObject feedKey (serialise updated) (Some s3Object.ETag)

                    let getUpdatedCount batches =
                        batches
                        |> Array.map _.BatchItems
                        |> Array.concat
                        |> Array.filter _.Result.IsSome
                        |> Array.length

                    return
                        putResult
                        |> Result.map (fun _ ->
                            getUpdatedCount updated.Batches - getUpdatedCount existingDerivedFeed.Batches)

                | None -> return Ok 0
            | None -> return Error Net.HttpStatusCode.NotFound
        }

    storage.RetryHttp 3 (fun () -> update sourceSetting modelActions)
